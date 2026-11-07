using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt
{
    /// <summary>
    /// The vvvv-independent core of <see cref="Renderer"/>: one Qt window, its reconciler and command buffer.
    /// <see cref="Update"/> runs in the node's Update (writes commands only), <see cref="ApplyCommands"/> and
    /// <see cref="AfterEvents"/> run in the pump (<see cref="QtPump"/>). Kept separate so the SmokeTest can drive
    /// it without a NodeContext. Main thread only.
    /// </summary>
    internal sealed class WindowRenderer : IDisposable
    {
        readonly CommandWriter _commands = new CommandWriter();
        readonly CommandWriter _wipe = new CommandWriter(); // RESET of the resync (#76, #92)
        readonly List<SinkState> _applyErrorSlots = new(); // slots showing an apply error (#275)
        readonly Reconciler _reconciler;
        readonly ResyncController _resync;
        IntPtr _window;
        byte[] _poll = new byte[16 * 1024];
        bool _disposed;

        // Last values sent to QtNative (change detection; a resize by the user is not fought back).
        string _title;
        Color4 _clearColor;
        bool _hasClearColor;

        string _createError = "", _applyError = "", _pollError = "";
        Native.vlqt_perf _lastPerf;
        bool _perfChanged;
        // #574: percentiles sort two rings in QtNative, so they are recomputed at most every kPercentileIntervalMs
        // (the ring spans ~1 s, ~4 updates per second are enough); the averages of _lastPerf stay per pump.
        const long kPercentileIntervalMs = 250;
        Native.vlqt_perf_stats _stats;
        Native.vlqt_host_stats _host;
        long _statsAt = long.MinValue;
        PerfStats _perf = PerfStats.Empty;
        // Host frame (#245): commands of the last apply, reconcile time since the last report.
        int _appliedOps;
        double _reconcileMs;

        /// <param name="scene">Duplicate registry of the app (<see cref="SceneRegistry.ForApp"/>).</param>
        /// <param name="title">Initial window title.</param>
        /// <param name="bounds">Initial position and size in DIP, like Bounds on create of VL.Skia/VL.Stride (size = client
        /// size, position = outer top-left), set by QtNative before show. Empty bounds give 600x400 centered; bounds on no
        /// screen are centered (QScreen, all platforms; Wayland ignores the position).</param>
        /// <param name="vSync">Swap interval 1 instead of 0 (fixed at creation, Qt reads it when the swap chain is created).</param>
        /// <param name="mode"><see cref="Protocol.WindowMode"/> bits set before the first show (e.g. Borderless: no frame
        /// flash); later changes via <see cref="SetMode"/>.</param>
        /// <param name="iconPath">Full path of the window icon, set before the first show (no flash of the default icon);
        /// null = default icon. The result is in <see cref="IconResult"/>, later changes via <see cref="SetIcon"/>.</param>
        public WindowRenderer(SceneRegistry scene, string? title, RectangleF bounds, bool vSync = false, uint mode = 0, string? iconPath = null)
        {
            _reconciler = new Reconciler(_commands, new IdAllocator(), scene);
            _resync = new ResyncController(_reconciler, ResetInQtNative);
            _title = title ?? "";
            // Position and size are set in QtNative before show (#100): no flash at Qt's default position.
            var a = WindowPlacement.FromBounds(bounds);
            _window = Native.vlqt_window_create_at(a.X, a.Y, a.Width, a.Height, _title, vSync ? 1 : 0);
            _reconciler.Window = _window;
            if (_window == IntPtr.Zero)
                _createError = Native.LastError();
            else
            {
                if (mode != 0)
                    Native.vlqt_window_set_mode(_window, mode, mode);
                if (iconPath is not null)
                    SetIcon(iconPath);
                Native.vlqt_window_show(_window);
            }
        }

        public IntPtr Window => _window;

        /// <summary>Result of the last <see cref="SetIcon"/> (vlqt_window_set_icon): 0 ok, -7 not loadable (default icon shown), -1 no window.</summary>
        public int IconResult { get; private set; }

        /// <summary>
        /// Whether the window is shown (false after X/<see cref="Hide"/> and after <see cref="Dispose"/>). Queried live from Qt
        /// (QWindow::isVisible, a plain getter), not cached in the pump (#93): a cached value was false in the frame of the
        /// creation (node created, Undo, F5, Bounds changed) and stayed false whenever this window was not pumped.
        /// </summary>
        public bool IsVisible => !_disposed && _window != IntPtr.Zero && Native.vlqt_window_is_visible(_window) != 0;

        /// <summary>First of: window creation, last apply, last poll error. Empty if ok.</summary>
        public string Error =>
            _createError.Length > 0 ? _createError : _applyError.Length > 0 ? _applyError : _pollError;

        /// <summary>
        /// Last window geometry reported by QtNative (EV_WINDOW_GEOMETRY, DIP, Bounds convention: outer top-left + client
        /// size, plus client bounds); null until the first pump after creation. Used by Save Bounds (#143) and the
        /// Client Bounds output (#465).
        /// </summary>
        public WindowGeometry? Geometry { get; private set; }

        /// <summary>Raised in <see cref="AfterEvents"/> when the window moved, resized or changed state (minimized, ...).</summary>
        public event Action<WindowGeometry>? GeometryChanged;

        /// <summary>Raised in <see cref="AfterEvents"/> when X was clicked (QtNative swallowed the close).</summary>
        public event Action? CloseRequested;

        internal Reconciler Reconciler => _reconciler;

        /// <summary>Command buffer of the next apply (test seam: SmokeTest injects an invalid command, #76).</summary>
        internal CommandWriter Commands => _commands;

        /// <summary>Number of resyncs after failed applies (#76).</summary>
        internal int Resyncs => _resync.Resyncs;

        /// <summary>Perf of the last pump; a new record only when the values changed.</summary>
        public PerfStats Perf
        {
            get
            {
                if (_perfChanged)
                {
                    _perfChanged = false;
                    // Percentiles sort the rings: queried only when read and at most every kPercentileIntervalMs.
                    var now = Environment.TickCount64;
                    if (!_disposed && _window != IntPtr.Zero && now - _statsAt >= kPercentileIntervalMs)
                    {
                        _statsAt = now;
                        Native.vlqt_window_get_perf_stats(_window, out _stats);
                        Native.vlqt_window_get_host_stats(_window, out _host);
                    }
                    _perf = PerfStats.From(_lastPerf, _stats, _host);
                }
                return _perf;
            }
        }

        /// <summary>
        /// Writes the commands of this frame (applied by the next pump). <paramref name="enabled"/> = false freezes
        /// the scene: no reconcile and no clear color, the window keeps showing the last state. Title is
        /// sent whenever the pin value changes (also when disabled). <paramref name="space"/> must be resolved
        /// (<see cref="Geometry.Resolve"/>).
        /// </summary>
        public void Update(Element? input, Space space, Color4 clearColor, bool enabled, string? title)
        {
            if (_disposed || _window == IntPtr.Zero)
                return;

            title ??= "";
            if (!string.Equals(title, _title, StringComparison.Ordinal))
            {
                _title = title;
                Native.vlqt_window_set_title(_window, title);
            }

            if (!enabled)
                return;

            if (!_hasClearColor || clearColor != _clearColor)
            {
                _hasClearColor = true;
                _clearColor = clearColor;
                _commands.ClearColor(clearColor);
            }
            // Appends: the buffer is reset after apply, so commands of a frame without pump go out with the next one.
            if (_resync.ShouldReconcile(input))
            {
                var t0 = Stopwatch.GetTimestamp();
                _reconciler.Update(input, space);
                _reconcileMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
        }

        int ResetInQtNative()
        {
            _wipe.Reset();
            _wipe.ResetScene();
            return Native.Apply(_window, _wipe.EndFrame());
        }

        public void ApplyCommands()
        {
            _appliedOps = 0;
            if (_disposed || _window == IntPtr.Zero || _commands.Length == 0)
                return;
            _appliedOps = _commands.Count;
            int rc = Native.Apply(_window, _commands.EndFrame());
            _commands.Reset();
            if (rc == 0)
            {
                _applyError = "";
                // Rebuilt or applied normally: the elements' apply errors are over (not while the resync gave up,
                // then only the clear color went out and the failing input is still there).
                if (!_resync.GaveUp)
                    ClearApplyErrors();
            }
            else
                // Before the resync: its RESET overwrites QtNative's last error, Discard drops the bindings.
                _applyError = ReportApplyError();
            if (_resync.OnApplyResult(rc))
                _hasClearColor = false; // QtNative may have lost it with the aborted batch; sent again with the rebuild
        }

        /// <summary>
        /// #275: the failing object id (vlqt_window_get_apply_error) resolved to its sink slot shows the error at the
        /// element node; returns the text for the renderer's Error output, empty if the element took it.
        /// </summary>
        string ReportApplyError()
        {
            var text = Native.LastError();
            ClearApplyErrors(); // only the latest failure is shown (the retry after a resync reports it again)
            if (Native.vlqt_window_get_apply_error(_window, out var e) != 0 || e.id == 0
                || !_reconciler.Bindings.TryGetValue(e.id, out var slot) || slot.State is not { } state)
                return text;
            state.ApplyError = DescribeApplyError(e);
            if (!_applyErrorSlots.Contains(state))
                _applyErrorSlots.Add(state);
            return "";
        }

        void ClearApplyErrors()
        {
            if (_applyErrorSlots.Count == 0)
                return;
            foreach (var state in _applyErrorSlots)
                state.ApplyError = null;
            _applyErrorSlots.Clear();
        }

        /// <summary>Element-facing text, e.g. "Qt rejected FillMode: property 545: value 4 not allowed" (allocates; errors only).</summary>
        internal static string DescribeApplyError(in Native.vlqt_apply_error e)
        {
            if (e.op != Protocol.Op.Set)
            {
                var reason = e.Reason;
                // #313: a plugin under a path beyond MAX_PATH is reported as "not found" although it is there.
                if (reason.Contains("plugin", StringComparison.Ordinal) && reason.Contains("not found", StringComparison.Ordinal) && Native.PathsTooLong)
                    reason += ". " + Native.PathTooLongHint;
                return $"Qt rejected {ConstantName(typeof(Protocol.Op), e.op) ?? "command " + e.op}: {reason}";
            }
            return $"Qt rejected {ConstantName(typeof(Protocol.Prop), e.prop) ?? "property " + e.prop}: {e.Reason}";
        }

        static string? ConstantName(Type constants, ushort value)
        {
            foreach (var f in constants.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.IsLiteral && f.GetRawConstantValue() is ushort v && v == value)
                    return f.Name;
            return null;
        }

        public void AfterEvents()
        {
            if (_disposed || _window == IntPtr.Zero)
                return;

            if (Native.vlqt_window_take_close_request(_window) != 0)
                CloseRequested?.Invoke();
            if (_disposed)
                return;

            PollEvents();

            // Compare raw bytes: PerfStats is a record, only build a new one when the values changed.
            if (Native.vlqt_window_get_perf(_window, out var p) == 0)
            {
                if (!MemoryMarshal.AsBytes(new ReadOnlySpan<Native.vlqt_perf>(in p))
                        .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Native.vlqt_perf>(in _lastPerf))))
                {
                    _lastPerf = p;
                    _perfChanged = true;
                }
            }
        }

        /// <summary>#245: adds ops/reconcile of this window to the host frame and reports it to the native PerfMonitor.</summary>
        public void ReportHostFrame(Native.vlqt_host_frame frame)
        {
            if (_disposed || _window == IntPtr.Zero)
                return;
            frame.ops = _appliedOps;
            frame.reconcile = (float)_reconcileMs;
            _reconcileMs = 0;
            Native.vlqt_window_report_host_frame(_window, frame);
        }

        /// <summary>Last native PerfSample (budget warning, #241). false = none yet.</summary>
        public bool TryGetLastSample(out Native.vlqt_perf_sample sample)
        {
            sample = default;
            return !_disposed && _window != IntPtr.Zero && Native.vlqt_window_get_perf_sample(_window, out sample) == 0;
        }

        // Events and measured values into the sinks (read by the element nodes in the next Update).
        void PollEvents()
        {
            int n = Native.Poll(_window, _poll);
            if (n < -1)
            {
                // Buffer too small: -n bytes needed, the events stay queued.
                _poll = new byte[Math.Max(-n, _poll.Length * 2)];
                n = Native.Poll(_window, _poll);
            }
            if (n < 0)
            {
                _pollError = "poll: " + Native.LastError();
                return;
            }
            if (n == 0)
            {
                _pollError = "";
                return;
            }
            var events = new ReadOnlySpan<byte>(_poll, 0, n);
            if (WindowPlacement.TryReadGeometry(events, out var g) && g != Geometry)
            {
                Geometry = g;
                GeometryChanged?.Invoke(g);
            }
            // Window records (handle 0) count as ignored there.
            var result = _reconciler.Sinks.Dispatch(events);
            _pollError = result.Error is { } e ? "poll: " + e : "";
        }

        /// <summary>Switches one <see cref="Protocol.WindowMode"/> bit live (vlqt_window_set_mode).</summary>
        public void SetMode(uint mode, bool on)
        {
            if (!_disposed && _window != IntPtr.Zero)
                Native.vlqt_window_set_mode(_window, mode, on ? mode : 0u);
        }

        /// <summary>
        /// Sets the window icon live (vlqt_window_set_icon, #517). <paramref name="fullPath"/> null = the built-in default
        /// icon; QtNative resolves a relative path against the working directory, so pass a full path. A file that cannot
        /// be loaded leaves the default icon (<see cref="IconResult"/> -7, text in <see cref="Native.LastError"/>).
        /// </summary>
        public int SetIcon(string? fullPath)
        {
            IconResult = _disposed || _window == IntPtr.Zero ? -1 : Native.vlqt_window_set_icon(_window, fullPath);
            return IconResult;
        }

        /// <summary>Debug overlay for all elements live (vlqt_window_set_debug, #280).</summary>
        public void SetDebug(bool on)
        {
            if (!_disposed && _window != IntPtr.Zero)
                Native.vlqt_window_set_debug(_window, on ? 1 : 0);
        }

        public void Hide()
        {
            if (_window != IntPtr.Zero)
                Native.vlqt_window_hide(_window);
        }

        /// <summary>Clear (releases the elements in the app's registry), apply, destroy the window.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _reconciler.Clear();
            _reconciler.Window = IntPtr.Zero;
            if (_window != IntPtr.Zero)
            {
                if (_commands.Length > 0)
                    Native.Apply(_window, _commands.EndFrame());
                Native.vlqt_window_destroy(_window);
                _window = IntPtr.Zero;
            }
            _commands.Reset();
            ClearApplyErrors(); // the element nodes may outlive the window
            _disposed = true;
        }
    }
}
