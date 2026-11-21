using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Lib.Animation;

namespace VL.Qt
{
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
        private readonly HostPumpMeter _meter = new();
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

            _target.PumpFrame(_participants, seconds + _offset, budgetMs, vvvvStart, _meter);
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
}
