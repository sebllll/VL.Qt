using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Elements;
using VL.Qt.Wire;

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
        private readonly SceneCore _core;
        private SceneHandle _window;
        private bool _disposed;

        // Last title sent to QtNative (change detection; a resize by the user is not fought back).
        private string _title;

        private string _createError = "";
        private Native.vlqt_perf _lastPerf;
        private bool _perfChanged;
        // percentiles sort two rings in QtNative, so they are recomputed at most every kPercentileIntervalMs
        // (the ring spans ~1 s, ~4 updates per second are enough); the averages of _lastPerf stay per pump.
        private const long kPercentileIntervalMs = 250;
        private Native.vlqt_perf_stats _stats;
        private Native.vlqt_host_pump_stats _host;
        private long _statsAt = long.MinValue;
        private PerfStats _perf = PerfStats.Empty;

        /// <param name="scene">Duplicate registry of the app (<see cref="SceneRegistry.ForApp"/>).</param>
        /// <param name="title">Initial window title.</param>
        /// <param name="bounds">Initial position and size in DIP, like Bounds on create of VL.Skia/VL.Stride (size = client
        /// size, position = outer top-left), set by QtNative before show. Empty bounds give 600x400 centered; bounds on no
        /// screen are centered (QScreen, all platforms; Wayland ignores the position).</param>
        /// <param name="vSync">Swap interval 1 (default, no tearing, the frame may wait for the refresh) or 0 (Present never waits, tearing possible). Fixed at creation, Qt reads it when the swap chain is created.</param>
        /// <param name="mode"><see cref="Protocol.WindowMode"/> bits set before the first show (e.g. Borderless: no frame
        /// flash); later changes via <see cref="SetMode"/>.</param>
        /// <param name="iconPath">Full path of the window icon, set before the first show (no flash of the default icon);
        /// null = default icon. The result is in <see cref="IconResult"/>, later changes via <see cref="SetIcon"/>.</param>
        /// <param name="samples">MSAA count set before the first show (no window swap at the first frame); not set =
        /// QtNative's creation default. The count used is in <see cref="UsedSamples"/>, later changes via <see cref="SetSamples"/>.</param>
        public WindowRenderer(SceneRegistry scene, string? title, RectangleF bounds, bool vSync = true, uint mode = 0, string? iconPath = null,
            Optional<MultisampleCount> samples = default)
        {
            _core = new SceneCore(scene);
            _title = title ?? "";
            // Position and size are set in QtNative before show: no flash at Qt's default position.
            var a = WindowPlacement.FromBounds(bounds);
            _window = Native.vlqt_window_create(a.X, a.Y, a.Width, a.Height, _title, vSync ? 1 : 0);
            _core.Handle = _window;
            if (_window.IsNull)
                _createError = Native.LastError();
            else
            {
                if (mode != 0)
                    Native.vlqt_window_set_mode(_window, mode, mode);
                if (iconPath is not null)
                    SetIcon(iconPath);
                if (samples.HasValue)
                    SetSamples(samples.Value);
                Native.vlqt_window_show(_window);
            }
        }

        public SceneHandle Window => _window;

        /// <summary>
        /// MSAA count of the window as of the last <see cref="SetSamples"/> (what QtNative used, clamped to the device),
        /// 0 until it was set, negative if the last call failed (text in <see cref="Native.LastError"/>).
        /// </summary>
        public int UsedSamples { get; private set; }

        /// <summary>Result of the last <see cref="SetIcon"/> (vlqt_window_set_icon): Ok, Io = not loadable (default icon shown), NullHandle = no window.</summary>
        public Native.Result IconResult { get; private set; }

        /// <summary>
        /// Whether the window is shown (false after X/<see cref="Hide"/> and after <see cref="Dispose"/>). Queried live from Qt
        /// (QWindow::isVisible, a plain getter), not cached in the pump: a cached value was false in the frame of the
        /// creation (node created, Undo, F5, Bounds changed) and stayed false whenever this window was not pumped.
        /// </summary>
        public bool IsVisible => !_disposed && !_window.IsNull && Native.vlqt_window_is_visible(_window) == 1;

        /// <summary>First of: window creation, last apply, last poll error. Empty if ok.</summary>
        public string Error =>
            _createError.Length > 0 ? _createError : _core.ApplyError.Length > 0 ? _core.ApplyError : _core.PollError;

        /// <summary>
        /// Last window geometry reported by QtNative (EVENT_WINDOW_GEOMETRY, DIP, Bounds convention: outer top-left + client
        /// size, plus client bounds); null until the first pump after creation. Used by Save Bounds and the
        /// Client Bounds output.
        /// </summary>
        public WindowGeometry? Geometry { get; private set; }

        /// <summary>Raised in <see cref="AfterEvents"/> when the window moved, resized or changed state (minimized, ...).</summary>
        public event Action<WindowGeometry>? GeometryChanged;

        /// <summary>Raised in <see cref="AfterEvents"/> when X was clicked (QtNative swallowed the close).</summary>
        public event Action? CloseRequested;

        internal Reconciler Reconciler => _core.Reconciler;

        /// <summary>Command buffer of the next apply (test seam: SmokeTest injects an invalid command).</summary>
        internal CommandWriter Commands => _core.Commands;

        /// <summary>Number of resyncs after failed applies.</summary>
        internal int Resyncs => _core.Resyncs;

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
                    if (!_disposed && !_window.IsNull && now - _statsAt >= kPercentileIntervalMs)
                    {
                        _statsAt = now;
                        Native.vlqt_scene_get_perf_stats(_window, out _stats);
                        Native.vlqt_scene_get_host_pump_stats(_window, out _host);
                    }
                    _perf = PerfStats.From(_lastPerf, _stats, _host);
                }
                return _perf;
            }
        }

        /// <summary>
        /// Writes the commands of this frame (applied by the next pump). <paramref name="enabled"/> = false freezes
        /// the scene: no reconcile, clear color or Clear flag, the window keeps showing the last state. Title is
        /// sent whenever the pin value changes (also when disabled).
        /// </summary>
        public void Update(Element? input, Space space, Color4 clearColor, bool clear, bool enabled, string? title)
        {
            if (_disposed || _window.IsNull)
                return;

            title ??= "";
            if (!string.Equals(title, _title, StringComparison.Ordinal))
            {
                _title = title;
                Native.vlqt_window_set_title(_window, title);
            }

            if (!enabled)
                return;

            _core.Update(input, space, clearColor, clear);
        }

        public void ApplyCommands()
        {
            if (_disposed || _window.IsNull)
                return;
            _core.Apply();
        }

        public void AfterEvents()
        {
            if (_disposed || _window.IsNull)
                return;

            if (Native.vlqt_window_take_close_request(_window) == 1)
                CloseRequested?.Invoke();
            if (_disposed)
                return;

            PollEvents();

            // Compare raw bytes: PerfStats is a record, only build a new one when the values changed.
            if (Native.vlqt_scene_get_perf(_window, out var p) == Native.Result.Ok)
            {
                if (!MemoryMarshal.AsBytes(new ReadOnlySpan<Native.vlqt_perf>(in p))
                        .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Native.vlqt_perf>(in _lastPerf))))
                {
                    _lastPerf = p;
                    _perfChanged = true;
                }
            }
        }

        /// <summary>
        /// frame start, after QtNative delivered the queued input: events into the feedback, so the element nodes see them
        /// in this frame. The frame end (<see cref="AfterEvents"/>) polls again; each event is in exactly one of both polls.
        /// </summary>
        public void PollInput()
        {
            if (_disposed || _window.IsNull)
                return;
            PollEvents(frameStart: true);
        }

        /// <summary>adds ops/reconcile of this window to the host frame and reports it to the native PerfMonitor.</summary>
        public void ReportHostFrame(Native.vlqt_host_pump frame)
        {
            if (_disposed || _window.IsNull)
                return;
            frame.ops = _core.AppliedOps;
            frame.reconcile = (float)_core.TakeReconcileMs();
            Native.vlqt_scene_report_host_pump(_window, frame);
        }

        /// <summary>Last native PerfSample (budget warning). false = none yet.</summary>
        public bool TryGetLastSample(out Native.vlqt_perf_sample sample)
        {
            sample = default;
            return !_disposed && !_window.IsNull && Native.vlqt_scene_get_perf_sample(_window, out sample) == Native.Result.Ok;
        }

        // Events and measured values into the feedback (read by the element nodes in the next Update).
        private void PollEvents(bool frameStart = false)
        {
            var events = _core.ReadEvents(frameStart);
            if (events.IsEmpty)
                return;
            if (WindowPlacement.TryReadGeometry(events, out var g) && g != Geometry)
            {
                Geometry = g;
                GeometryChanged?.Invoke(g);
            }
            _core.Dispatch(events, frameStart);
        }

        /// <summary>Switches one <see cref="Protocol.WindowMode"/> bit live (vlqt_window_set_mode).</summary>
        public void SetMode(uint mode, bool on)
        {
            if (!_disposed && !_window.IsNull)
                Native.vlqt_window_set_mode(_window, mode, on ? mode : 0u);
        }

        /// <summary>
        /// Sets the window icon live (vlqt_window_set_icon). <paramref name="fullPath"/> null = the built-in default
        /// icon; QtNative resolves a relative path against the working directory, so pass a full path. A file that cannot
        /// be loaded leaves the default icon (<see cref="IconResult"/> = <see cref="Native.Result.Io"/>, text in <see cref="Native.LastError"/>).
        /// </summary>
        public Native.Result SetIcon(string? fullPath)
        {
            IconResult = _disposed || _window.IsNull ? Native.Result.NullHandle : Native.vlqt_window_set_icon(_window, fullPath);
            return IconResult;
        }

        /// <summary>
        /// Sets the MSAA count of the window (vlqt_window_set_samples). QtNative clamps it to the device and, for a
        /// shown window, swaps the window inside the same handle (some ms, the window is recreated on screen once), so call it
        /// only when the value changed. Returns <see cref="UsedSamples"/>.
        /// </summary>
        public int SetSamples(MultisampleCount samples)
        {
            if (!_disposed && !_window.IsNull)
                UsedSamples = Native.vlqt_window_set_samples(_window, (int)samples);
            return UsedSamples;
        }

        /// <summary>Debug overlay for all elements live (vlqt_scene_set_debug).</summary>
        public void SetDebug(bool on)
        {
            if (!_disposed && !_window.IsNull)
                Native.vlqt_scene_set_debug(_window, on ? 1 : 0);
        }

        public void Hide()
        {
            if (!_window.IsNull)
                Native.vlqt_window_hide(_window);
        }

        /// <summary>Clear (releases the elements in the app's registry), apply, destroy the window.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            var window = _core.Close();
            if (!window.IsNull)
                Native.vlqt_scene_destroy(window);
            _window = SceneHandle.Null;
            _disposed = true;
        }
    }
}
