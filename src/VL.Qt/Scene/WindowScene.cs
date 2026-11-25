using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// A native window scene, the vvvv-independent core of <see cref="Nodes.RendererNode"/>: window state, title, mode,
    /// icon, samples, perf, geometry and close requests around a <see cref="Scene"/>.
    /// <see cref="Update"/> runs in the node's Update (writes commands only), <see cref="ApplyCommands"/> and
    /// <see cref="AfterEvents"/> run in the pump (<see cref="QtPump"/>). Kept separate so the SmokeTest can drive
    /// it without a NodeContext. Main thread only.
    /// </summary>
    internal sealed class WindowScene : IDisposable
    {
        private readonly Scene _scene;

        // Last title sent to QtNative (change detection; a resize by the user is not fought back).
        private string _title;

        private string _createError = "";
        private Native.vlqt_perf _lastPerf;
        // Pumps seen by AfterEvents and the pump the Perf getter last read at: the native summary is read once per pump, on demand.
        private long _pumps, _perfPump;
        // percentiles sort two rings in QtNative, so they are recomputed at most every kPercentileIntervalMs
        // (the ring spans ~1 s, ~4 updates per second are enough); the averages of _lastPerf stay per pump.
        private const long kPercentileIntervalMs = 250;
        private Native.vlqt_perf_stats _stats;
        private Native.vlqt_host_pump_stats _host;
        private long _statsAt = long.MinValue;
        private PerfStats _perf;

        /// <param name="occurrences">Occurrence registry of the app (<see cref="OccurrenceRegistry.ForApp"/>).</param>
        /// <param name="interactions">Interaction registry of the app (<see cref="InteractionRegistry.ForApp"/>).</param>
        /// <param name="title">Initial window title.</param>
        /// <param name="bounds">Initial position and size in DIP, like Bounds on create of VL.Skia/VL.Stride (size = client
        /// size, position = outer top-left), set by QtNative before show. Empty bounds give 600x400 centered; bounds on no
        /// screen are centered (QScreen, all platforms; Wayland ignores the position).</param>
        /// <param name="vSync">Swap interval 1 (default, no tearing, the frame may wait for the refresh) or 0 (Present
        /// never waits, tearing possible). Fixed at creation, Qt reads it when the swap chain is created.</param>
        /// <param name="mode"><see cref="Protocol.WindowMode"/> bits set before the first show (e.g. Borderless: no frame
        /// flash); later changes via <see cref="SetMode"/>.</param>
        /// <param name="iconPath">Full path of the window icon, set before the first show (no flash of the default icon);
        /// null = default icon. The result is in <see cref="IconResult"/>, later changes via <see cref="SetIcon"/>.</param>
        /// <param name="samples">MSAA count set before the first show (no window swap at the first frame); not set =
        /// QtNative's creation default. The count used is in <see cref="UsedSamples"/>, later changes via <see cref="SetSamples"/>.</param>
        public WindowScene(OccurrenceRegistry occurrences, InteractionRegistry interactions, string? title, RectangleF bounds, bool vSync = true, uint mode = 0, string? iconPath = null,
            Optional<MultisampleCount> samples = default)
        {
            _title = title ?? "";
            // Position and size are set in QtNative before show: no flash at Qt's default position.
            var a = WindowPlacement.FromBounds(bounds);
            var window = Native.vlqt_window_create(a.X, a.Y, a.Width, a.Height, _title, vSync ? 1 : 0);
            if (window.IsNull)
                _createError = Native.LastError();
            _scene = new Scene(occurrences, interactions, window);
            if (!window.IsNull)
            {
                if (mode != 0)
                    Native.vlqt_window_set_mode(window, mode, mode);
                if (iconPath is not null)
                    SetIcon(iconPath);
                if (samples.HasValue)
                    SetSamples(samples.Value);
                Native.vlqt_window_show(window);
            }
        }

        /// <summary>The native window, a null handle if the creation failed and after <see cref="Dispose"/>.</summary>
        public SceneHandle Window => _scene.Handle;

        /// <summary>
        /// MSAA count of the window as of the last <see cref="SetSamples"/> (what QtNative used, clamped to the device),
        /// 0 until it was set, negative if the last call failed (text in <see cref="Native.LastError"/>).
        /// </summary>
        public int UsedSamples { get; private set; }

        /// <summary>Result of the last <see cref="SetIcon"/> (vlqt_window_set_icon): Ok, Io = not loadable (default
        /// icon shown), NullHandle = no window.</summary>
        public Native.Result IconResult { get; private set; }

        /// <summary>
        /// Whether the window is shown (false after X/<see cref="Hide"/> and after <see cref="Dispose"/>). Queried live from Qt
        /// (QWindow::isVisible, a plain getter), not cached in the pump: a cached value was false in the frame of the
        /// creation (node created, Undo, F5, Bounds changed) and stayed false whenever this window was not pumped.
        /// </summary>
        public bool IsVisible => _scene.IsOpen && Native.vlqt_window_is_visible(Window) == 1;

        /// <summary>First of: window creation, last apply, last poll error. Empty if ok.</summary>
        public string Error =>
            _createError.Length > 0 ? _createError : _scene.ApplyError.Length > 0 ? _scene.ApplyError : _scene.PollError;

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

        internal Reconciler Reconciler => _scene.Reconciler;

        /// <summary>Exception of the last reconcile, null if it worked (<see cref="Scene.ReconcileFault"/>).</summary>
        internal Exception? ReconcileFault => _scene.ReconcileFault;

        /// <summary>Command buffer of the next apply (test seam: SmokeTest injects an invalid command).</summary>
        internal CommandWriter Commands => _scene.Commands;

        /// <summary>Number of resyncs after failed applies.</summary>
        internal int Resyncs => _scene.Resyncs;

        /// <summary>
        /// Perf of the last pump; the native summary is read here, once per pump, only when someone reads Perf, and a new
        /// record is built only when the values changed.
        /// </summary>
        public PerfStats Perf
        {
            get
            {
                if (_perfPump == _pumps)
                    return _perf;
                _perfPump = _pumps;
                // Compare raw bytes: only build a new record when the values changed.
                if (!_scene.IsOpen || Native.vlqt_scene_get_perf(Window, out var p) != Native.Result.Ok
                    || MemoryMarshal.AsBytes(new ReadOnlySpan<Native.vlqt_perf>(in p))
                        .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<Native.vlqt_perf>(in _lastPerf))))
                    return _perf;
                _lastPerf = p;
                // Percentiles sort the rings: queried at most every kPercentileIntervalMs.
                var now = Environment.TickCount64;
                if (now - _statsAt >= kPercentileIntervalMs)
                {
                    _statsAt = now;
                    Native.vlqt_scene_get_perf_stats(Window, out _stats);
                    Native.vlqt_scene_get_host_pump_stats(Window, out _host);
                }
                _perf = PerfStats.From(_lastPerf, _stats, _host);
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
            if (!_scene.IsOpen)
                return;

            title ??= "";
            if (!string.Equals(title, _title, StringComparison.Ordinal))
            {
                _title = title;
                Native.vlqt_window_set_title(Window, title);
            }

            if (!enabled)
                return;

            _scene.Update(input, space, clearColor, clear);
        }

        public void ApplyCommands()
        {
            if (!_scene.IsOpen)
                return;
            _scene.Apply();
        }

        public void AfterEvents()
        {
            if (!_scene.IsOpen)
                return;

            if (Native.vlqt_window_take_close_request(Window) == 1)
                CloseRequested?.Invoke();
            if (!_scene.IsOpen)
                return;

            PollEvents();

            _pumps++;
        }

        /// <summary>
        /// frame start, after QtNative delivered the queued input: events into the feedback, so the element nodes see them
        /// in this frame. The frame end (<see cref="AfterEvents"/>) polls again; each event is in exactly one of both polls.
        /// </summary>
        public void PollInput()
        {
            if (!_scene.IsOpen)
                return;
            PollEvents(frameStart: true);
        }

        /// <summary>adds ops/reconcile of this window to the host frame and reports it to the native PerfMonitor.</summary>
        public void ReportHostFrame(Native.vlqt_host_pump frame)
        {
            if (!_scene.IsOpen)
                return;
            frame.ops = _scene.AppliedOps;
            frame.reconcile = (float)_scene.TakeReconcileMs();
            Native.vlqt_scene_report_host_pump(Window, frame);
        }

        /// <summary>Last native PerfSample (budget warning). false = none yet.</summary>
        public bool TryGetLastSample(out Native.vlqt_perf_sample sample)
        {
            sample = default;
            return _scene.IsOpen && Native.vlqt_scene_get_perf_sample(Window, out sample) == Native.Result.Ok;
        }

        // Events and measured values into the feedback (read by the element nodes in the next Update).
        private void PollEvents(bool frameStart = false)
        {
            var events = _scene.ReadEvents(frameStart);
            if (events.IsEmpty)
                return;
            if (WindowPlacement.TryReadGeometry(events, out var g) && g != Geometry)
            {
                Geometry = g;
                GeometryChanged?.Invoke(g);
            }
            _scene.Dispatch(events, frameStart);
        }

        /// <summary>Switches one <see cref="Protocol.WindowMode"/> bit live (vlqt_window_set_mode).</summary>
        public void SetMode(uint mode, bool on)
        {
            if (_scene.IsOpen)
                Native.vlqt_window_set_mode(Window, mode, on ? mode : 0u);
        }

        /// <summary>
        /// Sets the window icon live (vlqt_window_set_icon). <paramref name="fullPath"/> null = the built-in default
        /// icon; QtNative resolves a relative path against the working directory, so pass a full path. A file that cannot
        /// be loaded leaves the default icon (<see cref="IconResult"/> = <see cref="Native.Result.Io"/>, text in <see cref="Native.LastError"/>).
        /// </summary>
        public Native.Result SetIcon(string? fullPath)
        {
            IconResult = !_scene.IsOpen ? Native.Result.NullHandle : Native.vlqt_window_set_icon(Window, fullPath);
            return IconResult;
        }

        /// <summary>
        /// Sets the MSAA count of the window (vlqt_window_set_samples). QtNative clamps it to the device and, for a
        /// shown window, swaps the window inside the same handle (some ms, the window is recreated on screen once), so call it
        /// only when the value changed. Returns <see cref="UsedSamples"/>.
        /// </summary>
        public int SetSamples(MultisampleCount samples)
        {
            if (_scene.IsOpen)
                UsedSamples = Native.vlqt_window_set_samples(Window, (int)samples);
            return UsedSamples;
        }

        /// <summary>Debug overlay for all elements live (vlqt_scene_set_debug).</summary>
        public void SetDebug(bool on)
        {
            if (_scene.IsOpen)
                Native.vlqt_scene_set_debug(Window, on ? 1 : 0);
        }

        /// <summary>Diagnostic keys F2, F5, F6, F7 of the window on or off (vlqt_window_set_keys); off = they reach the scene.</summary>
        public void SetKeys(bool on)
        {
            if (_scene.IsOpen)
                Native.vlqt_window_set_keys(Window, on ? 1 : 0);
        }

        public void Hide()
        {
            if (_scene.IsOpen)
                Native.vlqt_window_hide(Window);
        }

        /// <summary>Clear (releases the elements in the app's registry), apply, destroy the window.</summary>
        public void Dispose() => _scene.Dispose();
    }
}
