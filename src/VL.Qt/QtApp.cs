using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Lib.Animation;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// A window (renderer) that takes part in the global pump. Called on the Qt thread, once per vvvv frame,
    /// after all node updates; only <see cref="PollInput"/> runs at the frame start, before them.
    /// </summary>
    internal interface IQtFrameParticipant
    {
        /// <summary>Hands the commands written in Update() to QtNative (vlqt_window_apply).</summary>
        void ApplyCommands();

        /// <summary>
        /// QtNative window of this participant, zero = none. Only the windows of the pumping app may render in its
        /// pump, so with several vvvv apps a window still renders once per vvvv frame, not once per app.
        /// </summary>
        IntPtr Window => IntPtr.Zero;

        /// <summary>After Qt processed its events (and rendered): poll the window, read perf, handle close requests.</summary>
        void AfterEvents();

        /// <summary>
        /// at the start of the vvvv frame, after Qt delivered the queued input (<see cref="Native.vlqt_app_deliver_input"/>):
        /// poll the window into the feedback, so the node updates of this frame see it. Nothing is rendered or applied.
        /// </summary>
        void PollInput() { }

        /// <summary>
        /// after <see cref="AfterEvents"/>, the host values of this pump for the window's native PerfMonitor. QtApp fills
        /// apply time and the shared values (host pump, interval, GC); the participant adds ops/reconcile and reports.
        /// </summary>
        void ReportHostFrame(Native.vlqt_host_frame frame) { }

        /// <summary>Last native PerfSample of the window (budget warning). false = none.</summary>
        bool TryGetLastSample(out Native.vlqt_perf_sample sample)
        {
            sample = default;
            return false;
        }
    }

    /// <summary>What a <see cref="QtPump"/> drives (the <see cref="QtApp"/>; a fake in unit tests, which have no Qt).</summary>
    internal interface IQtPumpTarget
    {
        /// <summary>Last time handed to the animation driver, NaN before the first pump.</summary>
        double DriverSeconds { get; }

        /// <summary>
        /// apply → advance(<paramref name="driverSeconds"/>) → processEvents → poll. <paramref name="vvvvStart"/>: Stopwatch
        /// timestamp of the vvvv frame start before this pump, 0 = unknown. <paramref name="history"/>: host-frame
        /// state of the pumping app (interval and GC per app, not since another app's pump).
        /// </summary>
        void PumpFrame(IQtFrameParticipant[] participants, double driverSeconds, double budgetMs, long vvvvStart, HostFrameHistory history);

        /// <summary>
        /// start of the vvvv frame: Qt delivers the input queued since the last pump (without rendering), then every
        /// participant polls it (<see cref="IQtFrameParticipant.PollInput"/>).
        /// </summary>
        void DeliverInput(IQtFrameParticipant[] participants) { }

        /// <summary>Bookkeeping for [vlqt.stats]: participants (+registered / -unregistered) and live pumps.</summary>
        void OnParticipantsChanged(int delta);
        void OnPumpChanged(int delta);
    }

    /// <summary>
    /// The pump of one vvvv app: subscribes to the frame clock of its <see cref="AppHost"/> and pumps the process-wide
    /// <see cref="QtApp"/> with the participants (renderers) of that app, once per frame (<see cref="IFrameClock.GetFrameFinished"/>).
    /// <para>
    /// also listens to <see cref="IFrameClock.GetTicks"/> (fired in FrameClock.BeginFrame on the same mainloop thread,
    /// before the patch update) and hands its timestamp to the pump, so the host frame can tell vvvv time (frame start →
    /// pump) from waiting (previous pump end → frame start). Without a tick for this frame (the first frame: the renderer
    /// creates the pump inside the update; clocks without ticks) the clock's own update time
    /// (<see cref="FrameFinishedMessage.FrameDuration"/>) is the fallback, else 0 = unknown.
    /// </para>
    /// <para>
    /// the tick also delivers the input that arrived since the last pump (<see cref="IQtPumpTarget.DeliverInput"/>),
    /// so it reaches the patch in this frame instead of the next one. The frame end (apply, render, poll) stays as it is.
    /// </para>
    /// <para>
    /// Lives as a service of the app host (<see cref="ForApp"/>) and is disposed with it. F8/F5 replaces the app host and its
    /// frame clock; the new app gets a new pump bound to the new clock (a process-wide subscription stayed on the
    /// clock of the first app, which no longer ticks after a restart → black window, 0 fps, IsVisible false).
    /// </para>
    /// Main (Qt) thread only. Copy-on-write participants: Register/Unregister allocate, a frame only reads the current
    /// array (no alloc, safe against changes from within a participant callback).
    /// </summary>
    internal sealed class QtPump : IObserver<FrameFinishedMessage>, IObserver<FrameTimeMessage>, IDisposable
    {
        /// <summary>
        /// this far behind the animation driver (another app drove it, e.g. while this app was paused) the pump
        /// re-syncs its offset instead of waiting until its clock catches up. Smaller gaps (two apps in the same vvvv frame)
        /// keep the driver as it is.
        /// </summary>
        internal const double ResyncSeconds = 0.1;

        private readonly IQtPumpTarget _target;
        private readonly HostFrameHistory _history = new();
        private readonly IDisposable _subscription, _ticks;
        private long _frameStart; // Stopwatch timestamp of the last tick (vvvv frame start), 0 = none since the last pump
        private IQtFrameParticipant[] _participants = Array.Empty<IQtFrameParticipant>();
        private double _lastSeconds = double.NaN;
        private double _offset;
        private bool _first = true, _disposed;

        /// <summary>
        /// The pump shared by all renderers of one vvvv app (service of its <see cref="AppHost"/>, disposed with it).
        /// Uses the app host's own frame clock, not <see cref="Clocks.FrameClock"/> (a thread-static cache).
        /// </summary>
        public static QtPump ForApp(AppHost appHost)
            => appHost.Services.GetOrAddService(s =>
            {
                var app = QtApp.Get(appHost.LoggerFactory);
                var clock = s.GetService(typeof(IFrameClock)) as IFrameClock
                    ?? throw new InvalidOperationException("VL.Qt: the app host has no IFrameClock");
                return new QtPump(app, clock);
            }, allowToAskParent: false);

        public QtPump(IQtPumpTarget target, IFrameClock clock)
        {
            _target = target;
            _target.OnPumpChanged(1);
            _ticks = clock.GetTicks().Subscribe(this);
            _subscription = clock.GetFrameFinished().Subscribe(this);
        }

        public int ParticipantCount => _participants.Length;
        public bool IsDisposed => _disposed;

        /// <summary>deliver and poll input at the frame start (default). False = only at the frame end (SmokeTest: latency before).</summary>
        internal bool DeliverInputAtFrameStart { get; set; } = true;

        public void Register(IQtFrameParticipant participant)
        {
            if (_disposed || Array.IndexOf(_participants, participant) >= 0)
                return;
            var next = new IQtFrameParticipant[_participants.Length + 1];
            _participants.CopyTo(next, 0);
            next[^1] = participant;
            _participants = next;
            _target.OnParticipantsChanged(1);
        }

        public void Unregister(IQtFrameParticipant participant)
        {
            var index = Array.IndexOf(_participants, participant);
            if (index < 0)
                return;
            var next = new IQtFrameParticipant[_participants.Length - 1];
            Array.Copy(_participants, 0, next, 0, index);
            Array.Copy(_participants, index + 1, next, index, next.Length - index);
            _participants = next;
            _target.OnParticipantsChanged(-1);
        }

        /// <summary>
        /// One frame of this app (also called directly by tests). <paramref name="updateSeconds"/>: the clock's update time
        /// of this frame, fallback for the vvvv phase when no tick came.
        /// </summary>
        internal void Frame(double seconds, double updateSeconds = 0)
        {
            if (_disposed)
                return;

            // vvvv frame start. A tick from a previous frame never survives (reset per pump); a tick "after" now
            // (other thread, clock skew) is ignored.
            var now = Stopwatch.GetTimestamp();
            var vvvvStart = _frameStart;
            _frameStart = 0;
            if (vvvvStart == 0 || vvvvStart > now)
                vvvvStart = updateSeconds > 0 && updateSeconds < 1 ? now - (long)(updateSeconds * Stopwatch.Frequency) : 0;

            // Frame budget from the vvvv clock; fallback 60 Hz (first frame, pause, jumps).
            var budgetMs = (seconds - _lastSeconds) * 1000.0;
            if (!(budgetMs > 0 && budgetMs < 1000))
                budgetMs = 1000.0 / 60;
            _lastSeconds = seconds;

            // The animation driver lives in the process; a restarted app's clock may start below the time it already got.
            // Shift this app's time so the driver never runs backwards. 0 for the first app (driver time = clock time).
            // later on, another app may have driven it far ahead (this app paused): continue from there by one frame.
            var last = _target.DriverSeconds;
            if (_first)
            {
                _first = false;
                _offset = !double.IsNaN(last) && seconds < last ? last - seconds : 0;
            }
            else if (seconds + _offset < last - ResyncSeconds)
                _offset = last + budgetMs / 1000.0 - seconds;

            _target.PumpFrame(_participants, seconds + _offset, budgetMs, vvvvStart, _history);
        }

        /// <summary>vvvv frame start: remembers the time and delivers the input; also called directly by tests.</summary>
        internal void Tick()
        {
            _frameStart = Stopwatch.GetTimestamp();
            if (DeliverInputAtFrameStart && _participants.Length > 0)
                _target.DeliverInput(_participants);
        }

        void IObserver<FrameFinishedMessage>.OnNext(FrameFinishedMessage message) => Frame(message.Time.Seconds, message.FrameDuration.Seconds);

        void IObserver<FrameTimeMessage>.OnNext(FrameTimeMessage message)
        {
            if (!_disposed)
                Tick();
        }

        void IObserver<FrameTimeMessage>.OnError(Exception error) { } // reported by the FrameFinished subscription
        void IObserver<FrameTimeMessage>.OnCompleted() { }

        void IObserver<FrameFinishedMessage>.OnError(Exception error) =>
            QtApp.Logger?.LogError(error, "VL.Qt: frame clock reported an error, Qt is no longer pumped");

        void IObserver<FrameFinishedMessage>.OnCompleted() =>
            QtApp.Logger?.LogWarning("VL.Qt: frame clock completed, Qt is no longer pumped");

        /// <summary>App stopped: unsubscribe from its clock and drop leftover participants (renderers not disposed cleanly).</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _subscription.Dispose();
            _ticks.Dispose();
            if (_participants.Length > 0)
                _target.OnParticipantsChanged(-_participants.Length);
            _participants = Array.Empty<IQtFrameParticipant>();
            _target.OnPumpChanged(-1);
        }
    }

    /// <summary>
    /// Host-frame state of one vvvv app between two of its pumps: interval and GC/alloc are per app frame.
    /// </summary>
    internal sealed class HostFrameHistory
    {
        public long PumpStart, AllocBytes;
        public int Gc0, Gc1, Gc2;
        public TimeSpan GcPause;
    }

    /// <summary>
    /// The one QGuiApplication per process. Never released: Qt cannot re-create its app in the same process.
    /// Holds no frame clock and no participants: those belong to a vvvv app (<see cref="QtPump"/>), because an app
    /// restart (F8/F5) replaces the app host and its frame clock while this process-wide state lives on.
    /// One <see cref="PumpFrame"/>: all participants apply their commands → advance the animation driver → process events → all participants poll.
    /// </summary>
    internal sealed class QtApp : IQtPumpTarget
    {
        private static QtApp? s_instance;

        private const long StatsIntervalMs = 10_000;
        private const long WarningIntervalMs = 5_000;

        private readonly int _threadId;
        private double _driverSeconds = double.NaN;
        private long _lastWarningTicks, _lastErrorTicks;
        private bool _wrongThreadLogged;
        private static bool s_abiMismatchLogged;

        // Cumulative counters (since process start), logged as [vlqt.stats].
        private long _pumps, _advances, _processEvents, _registered, _unregistered, _participantErrors;
        private int _participantCount, _activePumps;
        // Stats interval.
        private long _statsStartTicks, _statsPumps;
        private double _statsPumpMsSum, _statsPumpMsMax;
        // Host frame: apply time per participant of the current pump; end of the previous pump of any app.
        private float[] _applyMs = Array.Empty<float>();
        private long _prevPumpEnd;
        // QtNative windows of the current pump, reused (no allocation per frame).
        private IntPtr[] _windows = new IntPtr[4];
        private readonly ResourceClient _resources;

        private QtApp()
        {
            _threadId = Environment.CurrentManagedThreadId;
            Native.EnsureLoaded();
            CheckAbi();
            if (Native.PathsTooLong)
            {
                Logger?.LogWarning("VL.Qt: the Qt files are installed under a path that is too long for Windows (MAX_PATH {MaxPath}): "
                    + "{Length} characters for {Path}. Qt cannot load plugins from there and reports them as \"not found\". "
                    + "Move the package or exported app to a shorter path (base path {NativeDir}, {BaseLength} characters).",
                    Native.maxPath, Native.LongestPath.Length, Native.LongestPath.Path, Native.NativeDir, Native.NativeDir.Length);
            }
            if (Native.vlqt_app_init(Native.NativeDir, QtLog.Fn) != 0)
                throw new InvalidOperationException("VL.Qt: vlqt_app_init failed: " + Native.LastError());
            _statsStartTicks = Environment.TickCount64;
            _resources = new ResourceClient(new NativeResourceApi(), _threadId, static () => Logger);
        }

        // Before the first window or resource: a vlqt.dll of another ABI version is refused (no window, no QGuiApplication).
        // Every caller of Get throws again, so every Renderer node shows the error; the log gets it once.
        private static void CheckAbi()
        {
            var actual = Native.ReadAbiVersion();
            var message = AbiCheck.Describe(actual, Protocol.AbiVersion, Native.LibraryPath);
            if (message is null)
                return;
            if (!s_abiMismatchLogged)
            {
                s_abiMismatchLogged = true;
                Logger?.LogError(AbiCheck.Template, Native.LibraryPath, AbiCheck.ActualText(actual), Protocol.AbiVersion);
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
        public int ParticipantCount => _participantCount;
        /// <summary>Pumps (apps) currently subscribed to a frame clock.</summary>
        public int ActivePumps => _activePumps;
        public long Pumps => _pumps;
        public long Advances => _advances;
        public long ProcessEventsCount => _processEvents;
        public double DriverSeconds => _driverSeconds;

        /// <summary>Image resources of QtNative: status queue and release queue, pumped here.</summary>
        public ResourceClient Resources => _resources;

        void IQtPumpTarget.OnParticipantsChanged(int delta)
        {
            _participantCount += delta;
            if (delta > 0)
                _registered += delta;
            else
                _unregistered -= delta;
        }

        void IQtPumpTarget.OnPumpChanged(int delta)
        {
            _activePumps += delta;
            _resources.SetPumping(_activePumps > 0);
        }

        /// <summary>
        /// One pump: apply → advance → processEvents → poll. Called by a <see cref="QtPump"/> once per frame of its app.
        /// with several apps each pumps once per vvvv frame; only the windows of <paramref name="participants"/> render,
        /// and the process-wide animation driver only moves forward.
        /// </summary>
        public void PumpFrame(IQtFrameParticipant[] participants, double driverSeconds, double budgetMs, long vvvvStart, HostFrameHistory history)
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

            var start = Stopwatch.GetTimestamp();
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            var gcPause = GC.GetTotalPauseDuration();
            var allocBytes = GC.GetTotalAllocatedBytes(false);

            if (_applyMs.Length < participants.Length)
                _applyMs = new float[participants.Length];
            for (int i = 0; i < participants.Length; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                try { participants[i].ApplyCommands(); }
                catch (Exception e) { LogParticipantError(e, nameof(IQtFrameParticipant.ApplyCommands)); }
                _applyMs[i] = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }

            // releases after the apply (elements switching away acquire their new image first), before the frame end frees.
            try { _resources.BeforeEvents(); }
            catch (Exception e) { LogParticipantError(e, nameof(ResourceClient.BeforeEvents)); }

            var tApplied = Stopwatch.GetTimestamp();
            // another app's pump in the same vvvv frame may bring an earlier time (own clock): keep the driver.
            if (!(driverSeconds <= _driverSeconds))
            {
                _driverSeconds = driverSeconds;
                Native.vlqt_driver_advance(driverSeconds);
                _advances++;
            }
            var tAdvanced = Stopwatch.GetTimestamp();
            Native.vlqt_app_process_events_for(_windows, CollectWindows(participants));
            _processEvents++;
            var tProcessed = Stopwatch.GetTimestamp();
            try { _resources.AfterEvents(); }
            catch (Exception e) { LogParticipantError(e, nameof(ResourceClient.AfterEvents)); }

            for (int i = 0; i < participants.Length; i++)
            {
                try { participants[i].AfterEvents(); }
                catch (Exception e) { LogParticipantError(e, nameof(IQtFrameParticipant.AfterEvents)); }
            }

            _pumps++;
            var tEnd = Stopwatch.GetTimestamp();
            var ms = Stopwatch.GetElapsedTime(start, tEnd).TotalMilliseconds;
            var now = Environment.TickCount64;

            // Host frame to every window's PerfMonitor: GC/alloc since the previous pump start (= one vvvv frame).
            var host = new Native.vlqt_host_frame
            {
                host = (float)ms,
                interval = history.PumpStart != 0 ? (float)Stopwatch.GetElapsedTime(history.PumpStart, start).TotalMilliseconds : 0,
                // vvvv = frame start → this pump; wait = previous pump end (any app) → frame start. Interleaved apps
                // (frame start before the previous pump ended) give wait 0.
                vvvv = vvvvStart != 0 && vvvvStart <= start ? (float)Stopwatch.GetElapsedTime(vvvvStart, start).TotalMilliseconds : 0,
                wait = vvvvStart != 0 && _prevPumpEnd != 0 && vvvvStart > _prevPumpEnd
                    ? (float)Stopwatch.GetElapsedTime(_prevPumpEnd, vvvvStart).TotalMilliseconds : 0,
            };
            if (history.PumpStart != 0)
            {
                host.gc0 = gc0 - history.Gc0;
                host.gc1 = gc1 - history.Gc1;
                host.gc2 = gc2 - history.Gc2;
                host.gc_pause = (float)(gcPause - history.GcPause).TotalMilliseconds;
                host.alloc_bytes = allocBytes - history.AllocBytes;
            }
            history.PumpStart = start;
            history.Gc0 = gc0; history.Gc1 = gc1; history.Gc2 = gc2;
            history.GcPause = gcPause;
            history.AllocBytes = allocBytes;
            _prevPumpEnd = tEnd;
            for (int i = 0; i < participants.Length; i++)
            {
                host.apply = _applyMs[i];
                try { participants[i].ReportHostFrame(host); }
                catch (Exception e) { LogParticipantError(e, nameof(IQtFrameParticipant.ReportHostFrame)); }
            }

            // Warn only when Qt eats more than half a vvvv frame; single small spikes are normal. The VSync wait (in
            // QRhi::beginFrame with D3D11) and Present are not Qt work: the sample is read only after the cheap checks, so a
            // normal frame costs nothing extra.
            double waitMs = 0;
            if (ms > budgetMs * 0.5 && now - _lastWarningTicks > WarningIntervalMs
                && ExceedsBudget(ms, waitMs = SumWaitMs(participants), budgetMs))
            {
                _lastWarningTicks = now;
                // Phase breakdown: which part of the pump, and did a .NET GC run meanwhile? Plus the Qt phases of each
                // window's last pump (is a spike in sync, render, present, gpu or events?).
                Logger?.LogWarning("Qt frame took {Ms:F1} ms of {Budget:F1} ms (pump {Pump:F1} ms minus vsync wait and present {Wait:F1} ms), vvvv before it {Vvvv:F1} ms (apply {Apply:F1}, advance {Advance:F1}, processEvents {Process:F1}, after {After:F1}; GC gen0 +{Gc0}, gen1 +{Gc1}, gen2 +{Gc2}, pause {GcPause:F1} ms; alloc {AllocKb:F0} KB/frame){Phases}",
                    ms - Math.Min(waitMs, ms), budgetMs, ms, waitMs, host.vvvv,
                    Stopwatch.GetElapsedTime(start, tApplied).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(tApplied, tAdvanced).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(tAdvanced, tProcessed).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(tProcessed, tEnd).TotalMilliseconds,
                    GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2,
                    (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds,
                    host.alloc_bytes / 1024.0,
                    FormatPhases(participants));
            }

            _statsPumps++;
            _statsPumpMsSum += ms;
            if (ms > _statsPumpMsMax)
                _statsPumpMsMax = ms;
            if (now - _statsStartTicks >= StatsIntervalMs)
                LogStats(now);
        }

        /// <summary>
        /// VSync wait plus Present time of the last pump summed over the windows of the participants (ms, 0 without samples).
        /// Both may wait for the display refresh or the GPU, so they do not count as Qt work. No allocation.
        /// </summary>
        internal static double SumWaitMs(IQtFrameParticipant[] participants)
        {
            double sum = 0;
            for (int i = 0; i < participants.Length; i++)
            {
                try
                {
                    if (participants[i].TryGetLastSample(out var s))
                        sum += s.vsync + s.present;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Diagnostics only: a failing sample counts as no waiting time.
                }
            }
            return sum;
        }

        /// <summary>True if the pump took more than half a frame budget of Qt work, i.e. without the VSync wait and the Present time.</summary>
        internal static bool ExceedsBudget(double pumpMs, double waitMs, double budgetMs) =>
            pumpMs - Math.Min(waitMs, pumpMs) > budgetMs * 0.5;

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
                catch (Exception e) { LogParticipantError(e, nameof(IQtFrameParticipant.PollInput)); }
            }
        }

        /// <summary>Queued window system events of the last <see cref="DeliverInput"/> (SmokeTest), -1 = error.</summary>
        internal int LastDeliveredInput { get; private set; }

        /// <summary>Fills <see cref="_windows"/> with QtNative windows of the participants, returns the count.</summary>
        private int CollectWindows(IQtFrameParticipant[] participants)
        {
            if (_windows.Length < participants.Length)
                _windows = new IntPtr[participants.Length * 2];
            var n = 0;
            for (int i = 0; i < participants.Length; i++)
            {
                IntPtr window;
                try { window = participants[i].Window; }
                catch (Exception e) { LogParticipantError(e, nameof(IQtFrameParticipant.Window)); continue; }
                if (window != IntPtr.Zero)
                    _windows[n++] = window;
            }
            return n;
        }

        /// <summary>
        /// " · Qt last pump: vsync wait … sync … render … present … gpu … events … ms (n swaps)" per window with a sample;
        /// windows are numbered when there are several. "" without samples. Allocates: warning path only.
        /// </summary>
        internal static string FormatPhases(IQtFrameParticipant[] participants)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < participants.Length; i++)
            {
                Native.vlqt_perf_sample s;
                try
                {
                    if (!participants[i].TryGetLastSample(out s))
                        continue;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Diagnostics only (slow-frame warning): show the failure instead of the sample.
                    sb.Append(" · Qt last pump");
                    if (participants.Length > 1)
                        sb.Append(" #").Append(i + 1);
                    sb.Append(": sample failed (").Append(e.Message).Append(')');
                    continue;
                }
                sb.Append(" · Qt last pump");
                if (participants.Length > 1)
                    sb.Append(" #").Append(i + 1);
                sb.Append(System.FormattableString.Invariant(
                    $": vsync wait {s.vsync:F1}, sync {s.sync:F1}, render {s.render:F1}, present {s.present:F1}, gpu {s.gpu:F1}, events {s.events:F1} ms ({s.swaps} swaps)"));
            }
            return sb.ToString();
        }

        private void LogStats(long now)
        {
            var seconds = (now - _statsStartTicks) / 1000.0;
            Logger?.LogInformation(
                "[vlqt.stats] {Pumps} pumps in {Seconds:F1} s (pump avg {Avg:F2} ms, max {Max:F2} ms) · total: pumps {TotalPumps}, advances {Advances}, processEvents {ProcessEvents} · participants {Participants} (registered {Registered}, unregistered {Unregistered}, errors {Errors}) · pumps (apps) {ActivePumps}",
                _statsPumps, seconds, _statsPumps > 0 ? _statsPumpMsSum / _statsPumps : 0, _statsPumpMsMax,
                _pumps, _advances, _processEvents, _participantCount, _registered, _unregistered, _participantErrors, _activePumps);
            _statsStartTicks = now;
            _statsPumps = 0;
            _statsPumpMsSum = 0;
            _statsPumpMsMax = 0;
        }

        private void LogParticipantError(Exception e, string phase)
        {
            _participantErrors++;
            var now = Environment.TickCount64;
            if (now - _lastErrorTicks <= WarningIntervalMs)
                return;
            _lastErrorTicks = now;
            Logger?.LogError(e, "VL.Qt: participant failed in {Phase} (further errors within 5 s are counted, not logged)", phase);
        }
    }
}
