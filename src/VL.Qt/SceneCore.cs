using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Stride.Core.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt
{
    /// <summary>
    /// What a Qt scene handle needs on the managed side, whether it is a window (<see cref="WindowRenderer"/>) or an
    /// offscreen scene (<see cref="OffscreenRenderer"/>): reconciler, command buffer, resync after a failed apply,
    /// apply errors at the element nodes, event poll into the sinks and the clear color. Both handles go through the
    /// same vlqt_window_* functions. Main thread only.
    /// </summary>
    internal sealed class SceneCore
    {
        readonly CommandWriter _commands = new CommandWriter();
        readonly CommandWriter _wipe = new CommandWriter(); // RESET of the resync (#76, #92)
        readonly List<SinkState> _applyErrorSlots = new(); // slots showing an apply error (#275)
        readonly Reconciler _reconciler;
        readonly ResyncController _resync;
        IntPtr _handle;
        byte[] _poll = new byte[16 * 1024];

        // Last clear color and Clear flag sent to QtNative (change detection).
        Color4 _clearColor;
        bool _hasClearColor;
        bool _clear;
        bool _hasClear;

        string _pollError = "";
        // Host frame (#245): commands of the last apply, reconcile time since the last report.
        int _appliedOps;
        double _reconcileMs;

        public SceneCore(SceneRegistry scene)
        {
            _reconciler = new Reconciler(_commands, new IdAllocator(), scene);
            _resync = new ResyncController(_reconciler, ResetInQtNative);
        }

        /// <summary>The native scene handle, IntPtr.Zero before it exists and after <see cref="Close"/>. Set once after creation.</summary>
        public IntPtr Handle
        {
            get => _handle;
            set
            {
                _handle = value;
                _reconciler.Window = value;
            }
        }

        public Reconciler Reconciler => _reconciler;

        /// <summary>Command buffer of the next apply (test seam: SmokeTest injects an invalid command, #76).</summary>
        public CommandWriter Commands => _commands;

        /// <summary>Number of resyncs after failed applies (#76).</summary>
        public int Resyncs => _resync.Resyncs;

        /// <summary>Error of the last apply, empty if ok (the element took it, or the apply worked).</summary>
        public string ApplyError { get; private set; } = "";

        /// <summary>Error of the last poll, empty if ok.</summary>
        public string PollError => _pollError;

        /// <summary>Number of commands of the last <see cref="Apply"/>.</summary>
        public int AppliedOps => _appliedOps;

        /// <summary>
        /// Writes the clear color and the Clear flag (when they changed) and the commands of this frame (applied by
        /// <see cref="Apply"/>). <paramref name="clear"/> = false keeps the previous frame and draws the new one over it.
        /// <paramref name="space"/> must be resolved (<see cref="Geometry.Resolve"/>). Appends: the buffer is reset after
        /// apply, so commands of a frame without apply go out with the next one.
        /// </summary>
        public void Update(Element? input, Space space, Color4 clearColor, bool clear)
        {
            if (!_hasClearColor || clearColor != _clearColor)
            {
                _hasClearColor = true;
                _clearColor = clearColor;
                _commands.ClearColor(clearColor);
            }
            if (!_hasClear || clear != _clear)
            {
                _hasClear = true;
                _clear = clear;
                _commands.SetClear(clear);
            }
            // TODO: #143 T3: SpaceMapping from space + Native.vlqt_window_get_scene_metrics (Zero handle: no metrics),
            // _commands.SetSpace on change and again after a resync (like the clear color), _reconciler.Update(input, mapping).
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
            return Native.Apply(_handle, _wipe.EndFrame());
        }

        /// <summary>Sends the written commands to QtNative; a failure starts the resync.</summary>
        public void Apply()
        {
            _appliedOps = 0;
            if (_handle == IntPtr.Zero || _commands.Length == 0)
                return;
            _appliedOps = _commands.Count;
            int rc = Native.Apply(_handle, _commands.EndFrame());
            _commands.Reset();
            if (rc == 0)
            {
                ApplyError = "";
                // Rebuilt or applied normally: the elements' apply errors are over (not while the resync gave up,
                // then only the clear color went out and the failing input is still there).
                if (!_resync.GaveUp)
                    ClearApplyErrors();
            }
            else
                // Before the resync: its RESET overwrites QtNative's last error, Discard drops the bindings.
                ApplyError = ReportApplyError();
            if (_resync.OnApplyResult(rc))
            {
                // QtNative may have lost them with the aborted batch (RESET keeps them); sent again with the rebuild.
                _hasClearColor = false;
                _hasClear = false;
            }
        }

        /// <summary>Reconcile time since the last call (host frame report, #245).</summary>
        public double TakeReconcileMs()
        {
            var ms = _reconcileMs;
            _reconcileMs = 0;
            return ms;
        }

        /// <summary>
        /// #275: the failing object id (vlqt_window_get_apply_error) resolved to its sink slot shows the error at the
        /// element node; returns the text for the renderer's Error output, empty if the element took it.
        /// </summary>
        string ReportApplyError()
        {
            var text = Native.LastError();
            ClearApplyErrors(); // only the latest failure is shown (the retry after a resync reports it again)
            if (Native.vlqt_window_get_apply_error(_handle, out var e) != 0 || e.id == 0
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

        /// <summary>
        /// Reads the queued events of QtNative. Returns them for <see cref="Dispatch"/> (valid until the next call), empty if
        /// there are none or the poll failed (then <see cref="PollError"/> is set). The frame start poll (#24) only sets a
        /// poll error: one from the frame end stays visible to the node updates of the next frame.
        /// </summary>
        public ReadOnlySpan<byte> ReadEvents(bool frameStart)
        {
            int n = Native.Poll(_handle, _poll);
            if (n < -1)
            {
                // Buffer too small: -n bytes needed, the events stay queued.
                _poll = new byte[Math.Max(-n, _poll.Length * 2)];
                n = Native.Poll(_handle, _poll);
            }
            if (n < 0)
            {
                _pollError = "poll: " + Native.LastError();
                return default;
            }
            if (n == 0 && !frameStart)
                _pollError = "";
            return new ReadOnlySpan<byte>(_poll, 0, n);
        }

        /// <summary>Events of <see cref="ReadEvents"/> into the sinks (read by the element nodes in the next Update).</summary>
        public void Dispatch(ReadOnlySpan<byte> events, bool frameStart)
        {
            // Window records (handle 0) count as ignored there.
            var result = _reconciler.Sinks.Dispatch(events);
            if (result.Error is { } e)
                _pollError = "poll: " + e;
            else if (!frameStart)
                _pollError = "";
        }

        /// <summary>
        /// Clear (releases the elements in the app's registry), applies what is left and forgets the handle; returns it so
        /// the caller can destroy the native scene (IntPtr.Zero if there was none).
        /// </summary>
        public IntPtr Close()
        {
            _reconciler.Clear();
            _reconciler.Window = IntPtr.Zero;
            var handle = _handle;
            if (handle != IntPtr.Zero)
            {
                if (_commands.Length > 0)
                    Native.Apply(handle, _commands.EndFrame());
                _handle = IntPtr.Zero;
            }
            _commands.Reset();
            ClearApplyErrors(); // the element nodes may outlive the scene
            return handle;
        }
    }
}
