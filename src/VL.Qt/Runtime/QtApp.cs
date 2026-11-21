using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// The one QGuiApplication per process. Never released: Qt cannot re-create its app in the same process.
    /// Holds no frame clock and no participants: those belong to a vvvv app (<see cref="QtPump"/>), because an app
    /// restart (F8/F5) replaces the app host and its frame clock while this process-wide state lives on.
    /// One <see cref="PumpFrame"/>: all participants apply their commands → advance the animation driver → process events → all participants poll.
    /// </summary>
    internal sealed class QtApp : IQtPumpTarget
    {
        private static QtApp? s_instance;

        private readonly int _threadId;
        private double _driverSeconds = double.NaN;
        private bool _wrongThreadLogged;
        private static bool s_abiMismatchLogged;

        private readonly PumpDiagnostics _diagnostics;
        // Host frame: apply time per participant of the current pump; end of the previous pump of any app.
        private float[] _applyMs = Array.Empty<float>();
        private long _prevPumpEnd;
        // QtNative windows of the current pump, reused (no allocation per frame).
        private SceneHandle[] _windows = new SceneHandle[4];
        private readonly ResourceClient _resources;

        private QtApp()
        {
            _threadId = Environment.CurrentManagedThreadId;
            QtNativeLibrary.EnsureLoaded();
            CheckAbi();
            if (QtTreePathCheck.PathsTooLong)
            {
                Logger?.LogWarning("VL.Qt: the Qt files are installed under a path that is too long for Windows (MAX_PATH {MaxPath}): "
                    + "{Length} characters for {Path}. Qt cannot load plugins from there and reports them as \"not found\". "
                    + "Move the package or exported app to a shorter path (base path {NativeDir}, {BaseLength} characters).",
                    QtTreePathCheck.windowsMaxPath, QtTreePathCheck.LongestPath.Length, QtTreePathCheck.LongestPath.Path, QtNativeLibrary.Directory, QtNativeLibrary.Directory.Length);
            }
            Native.Result initResult;
            unsafe
            {
                initResult = Native.vlqt_app_init(QtNativeLibrary.Directory, QtLog.Fn);
            }
            if (initResult != Native.Result.Ok)
                throw new InvalidOperationException("VL.Qt: vlqt_app_init failed: " + Native.LastError());
            _diagnostics = new PumpDiagnostics();
            _resources = new ResourceClient(new NativeResourceApi(), _threadId, static () => Logger);
        }

        // Before the first window or resource: a vlqt.dll of another ABI version is refused (no window, no QGuiApplication).
        // Every caller of Get throws again, so every Renderer node shows the error; the log gets it once.
        private static void CheckAbi()
        {
            var actual = Native.ReadAbiVersion();
            var message = AbiCheck.Describe(actual, Protocol.AbiVersion, QtNativeLibrary.LibraryPath);
            if (message is null)
                return;
            if (!s_abiMismatchLogged)
            {
                s_abiMismatchLogged = true;
                Logger?.LogError(AbiCheck.Template, QtNativeLibrary.LibraryPath, AbiCheck.ActualText(actual), Protocol.AbiVersion);
            }
            throw new AbiMismatchException(message);
        }

        /// <summary>
        /// The process-wide app (created on first use, never released). A non-null <paramref name="loggerFactory"/> replaces
        /// the previous one (<see cref="QtLog"/>): after an app restart the logger factory of the old app host is gone.
        /// </summary>
        public static QtApp Get(ILoggerFactory? loggerFactory)
        {
            if (loggerFactory != null)
                QtLog.SetFactory(loggerFactory);
            return s_instance ??= new QtApp();
        }

        /// <summary>Test hook (SmokeTest 02b): a separate instance, not cached. Driven by a <see cref="QtPump"/> on a simulated clock.</summary>
        internal static QtApp CreateForTest(ILogger? logger)
        {
            QtLog.SetLogger(logger);
            return new QtApp();
        }

        /// <summary>Process-wide "VL.Qt" logger (pump, stats); node messages use the node's own logger.</summary>
        internal static ILogger? Logger => QtLog.Logger;

        public bool IsQtThread => Environment.CurrentManagedThreadId == _threadId;

        /// <summary>Participants over all live pumps (apps).</summary>
        public int ParticipantCount => _diagnostics.ParticipantCount;
        /// <summary>Pumps (apps) currently subscribed to a frame clock.</summary>
        public int ActivePumps => _diagnostics.ActivePumps;
        public long Pumps => _diagnostics.Pumps;
        public long Advances => _diagnostics.Advances;
        public long ProcessEventsCount => _diagnostics.ProcessEventsCount;
        public double DriverSeconds => _driverSeconds;

        /// <summary>Image resources of QtNative: status queue and release queue, pumped here.</summary>
        public ResourceClient Resources => _resources;

        void IQtPumpTarget.OnParticipantsChanged(int delta) => _diagnostics.OnParticipantsChanged(delta);

        void IQtPumpTarget.OnPumpChanged(int delta)
        {
            _diagnostics.OnPumpChanged(delta);
            _resources.SetPumping(_diagnostics.ActivePumps > 0);
        }

        /// <summary>
        /// One pump: apply → advance → processEvents → poll. Called by a <see cref="QtPump"/> once per frame of its app.
        /// with several apps each pumps once per vvvv frame; only the windows of <paramref name="participants"/> render,
        /// and the process-wide animation driver only moves forward.
        /// </summary>
        public void PumpFrame(IQtFrameParticipant[] participants, double driverSeconds, double budgetMs, long vvvvStart, HostPumpMeter meter)
        {
            if (!IsQtThread)
            {
                if (!_wrongThreadLogged)
                {
                    _wrongThreadLogged = true;
                    Logger?.LogError("VL.Qt: frame finished on thread {Thread}, but Qt lives on thread {QtThread}. Qt is not pumped.",
                        Environment.CurrentManagedThreadId, _threadId);
                }
                return;
            }

            meter.Begin();
            ApplyCommands(participants);
            var tApplied = Stopwatch.GetTimestamp();
            Advance(driverSeconds);
            var tAdvanced = Stopwatch.GetTimestamp();
            // One budget in C# and QtNative: the windows of this pump judge their Qt work against this frame interval.
            Native.vlqt_app_set_frame_budget(budgetMs);
            Native.vlqt_app_process_events_for(_windows, CollectWindows(participants));
            _diagnostics.CountProcessEvents();
            var tProcessed = Stopwatch.GetTimestamp();
            AfterEvents(participants);
            _diagnostics.CountPump();
            var tEnd = Stopwatch.GetTimestamp();

            // Host frame to every window's PerfMonitor.
            var host = meter.End(vvvvStart, _prevPumpEnd, tEnd);
            _prevPumpEnd = tEnd;
            for (int i = 0; i < participants.Length; i++)
            {
                host.apply = _applyMs[i];
                try { participants[i].ReportHostFrame(host); }
                catch (Exception e) { _diagnostics.LogParticipantError(e, nameof(IQtFrameParticipant.ReportHostFrame)); }
            }

            _diagnostics.AfterPump(participants, meter, host, budgetMs, tApplied, tAdvanced, tProcessed, tEnd);
        }

        // Every participant hands its commands to QtNative (apply time per participant for the host frame), then the
        // resources release: after the apply (elements switching away acquire their new image first), before the frame end frees.
        private void ApplyCommands(IQtFrameParticipant[] participants)
        {
            if (_applyMs.Length < participants.Length)
                _applyMs = new float[participants.Length];
            for (int i = 0; i < participants.Length; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                try { participants[i].ApplyCommands(); }
                catch (Exception e) { _diagnostics.LogParticipantError(e, nameof(IQtFrameParticipant.ApplyCommands)); }
                _applyMs[i] = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }

            try { _resources.BeforeEvents(); }
            catch (Exception e) { _diagnostics.LogParticipantError(e, nameof(ResourceClient.BeforeEvents)); }
        }

        // Another app's pump in the same vvvv frame may bring an earlier time (own clock): keep the driver.
        private void Advance(double driverSeconds)
        {
            if (driverSeconds <= _driverSeconds)
                return;
            _driverSeconds = driverSeconds;
            Native.vlqt_app_advance_animations(driverSeconds);
            _diagnostics.CountAdvance();
        }

        private void AfterEvents(IQtFrameParticipant[] participants)
        {
            try { _resources.AfterEvents(); }
            catch (Exception e) { _diagnostics.LogParticipantError(e, nameof(ResourceClient.AfterEvents)); }

            for (int i = 0; i < participants.Length; i++)
            {
                try { participants[i].AfterEvents(); }
                catch (Exception e) { _diagnostics.LogParticipantError(e, nameof(IQtFrameParticipant.AfterEvents)); }
            }
        }

        /// <summary>
        /// start of a vvvv frame. Qt delivers the queued input of all windows without rendering (QtNative closes the
        /// update gate), then the participants of the pumping app poll it into their feedback. Events of another app's windows
        /// stay in their window's buffer until that app polls. Not inside a pump (QtNative ignores the call there).
        /// </summary>
        public void DeliverInput(IQtFrameParticipant[] participants)
        {
            // Wrong thread: PumpFrame of this frame logs it once.
            if (!IsQtThread)
                return;
            LastDeliveredInput = Native.vlqt_app_deliver_input();
            for (int i = 0; i < participants.Length; i++)
            {
                try { participants[i].PollInput(); }
                catch (Exception e) { _diagnostics.LogParticipantError(e, nameof(IQtFrameParticipant.PollInput)); }
            }
        }

        /// <summary>Queued window system events of the last <see cref="DeliverInput"/> (SmokeTest), a negative <see cref="Native.Result"/> = error.</summary>
        internal int LastDeliveredInput { get; private set; }

        /// <summary>Fills <see cref="_windows"/> with QtNative windows of the participants, returns the count.</summary>
        private int CollectWindows(IQtFrameParticipant[] participants)
        {
            if (_windows.Length < participants.Length)
                _windows = new SceneHandle[participants.Length * 2];
            var n = 0;
            for (int i = 0; i < participants.Length; i++)
            {
                SceneHandle window;
                try { window = participants[i].Window; }
                catch (Exception e) { _diagnostics.LogParticipantError(e, nameof(IQtFrameParticipant.Window)); continue; }
                if (!window.IsNull)
                    _windows[n++] = window;
            }
            return n;
        }
    }
}
