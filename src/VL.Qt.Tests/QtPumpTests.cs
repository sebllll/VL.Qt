using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using NUnit.Framework;
using VL.Lib.Animation;

namespace VL.Qt.Tests
{
    /// <summary>
    /// #58: the pump is bound to one vvvv app (its frame clock), not to the process. F8/F5 = old pump disposed,
    /// new pump on a new clock. No Qt here: a fake target records what QtApp would do.
    /// </summary>
    public class QtPumpTests
    {
        sealed class Clock : IFrameClock
        {
            readonly Subject<FrameFinishedMessage> _finished = new();
            readonly Subject<FrameTimeMessage> _ticks = new();
            double _t;
            ulong _n;
            public Clock(double t) { _t = t; }
            public Time Time => new(_t);
            public double TimeDifference => 1.0 / 60;
            public IObservable<FrameTimeMessage> GetTicks() => _ticks;
            public IObservable<FrameFinishedMessage> GetFrameFinished() => _finished;
            public bool HasObservers => _finished.HasObservers;
            public bool HasTickObservers => _ticks.HasObservers;
            public void Tick() => _ticks.OnNext(new FrameTimeMessage(new Time(_t + 1.0 / 60), _n, TimeSpan.FromSeconds(1.0 / 60)));
            public void Frame(double updateSeconds = 0)
            {
                _t += 1.0 / 60;
                _finished.OnNext(new FrameFinishedMessage(new Time(_t), new Time(updateSeconds), _n++, TimeSpan.FromSeconds(1.0 / 60)));
            }
        }

        sealed class FakeApp : IQtPumpTarget
        {
            public double DriverSeconds { get; private set; } = double.NaN;
            public int Participants, Pumps, Frames, Registered, Unregistered;
            public readonly List<IQtFrameParticipant[]> Calls = new();
            public readonly List<double> VvvvMs = new(); // #260: vvvv frame start → pump, -1 = unknown
            public void PumpFrame(IQtFrameParticipant[] participants, double driverSeconds, double budgetMs, long vvvvStart, HostFrameHistory history)
            {
                VvvvMs.Add(vvvvStart != 0 ? Stopwatch.GetElapsedTime(vvvvStart).TotalMilliseconds : -1);
                // Like QtApp (#314): the driver only moves forward; a pump may be slightly behind (another app's clock).
                Assert.That(driverSeconds, Is.GreaterThanOrEqualTo(double.IsNaN(DriverSeconds) ? double.MinValue : DriverSeconds - QtPump.ResyncSeconds), "driver must not run backwards");
                if (!(driverSeconds <= DriverSeconds))
                    DriverSeconds = driverSeconds;
                Frames++;
                Calls.Add(participants);
                foreach (var p in participants) { p.ApplyCommands(); p.AfterEvents(); }
            }
            public void OnParticipantsChanged(int delta)
            {
                Participants += delta;
                if (delta > 0) Registered += delta; else Unregistered -= delta;
            }
            public void OnPumpChanged(int delta) => Pumps += delta;
        }

        sealed class Participant : IQtFrameParticipant
        {
            public int Applies, Afters;
            public void ApplyCommands() => Applies++;
            public void AfterEvents() => Afters++;
        }

        [Test]
        public void TwoApps_PausedAppResumesFromDriverTime()
        {
            // #314: two vvvv apps (entry points) pump the one QtApp. App 2 is paused while app 1 drives the animation
            // driver ahead; when app 2 runs alone again, the driver continues from there instead of standing still.
            var app = new FakeApp();
            var clock1 = new Clock(100);
            var clock2 = new Clock(100);
            using var pump1 = new QtPump(app, clock1);
            using var pump2 = new QtPump(app, clock2);
            pump1.Register(new Participant());
            pump2.Register(new Participant());
            clock1.Frame();
            clock2.Frame();
            for (int i = 0; i < 120; i++)
                clock1.Frame(); // app 2 paused for 2 s
            var driver = app.DriverSeconds;
            pump1.Dispose(); // app 1 closed
            clock2.Frame();
            Assert.That(app.DriverSeconds, Is.GreaterThan(driver), "the resumed app advances the driver");
            var resumed = app.DriverSeconds;
            clock2.Frame();
            Assert.That(app.DriverSeconds - resumed, Is.EqualTo(1.0 / 60).Within(1e-9), "then by its own frame steps");
        }

        [Test]
        public void Restart_NewAppPumpDrivesNewRenderers_OldClockDoesNot()
        {
            var app = new FakeApp();

            // App 1 (first F5): two renderers.
            var clock1 = new Clock(100);
            var pump1 = new QtPump(app, clock1);
            var a = new Participant();
            var b = new Participant();
            pump1.Register(a);
            pump1.Register(b);
            clock1.Frame();
            Assert.That(a.Applies, Is.EqualTo(1));
            Assert.That(b.Afters, Is.EqualTo(1));
            Assert.That(app.Frames, Is.EqualTo(1), "one pump per frame, independent of the number of renderers");

            for (int i = 0; i < 10; i++)
            {
                // F8: renderers disposed, app host disposes its pump service.
                pump1.Unregister(a);
                pump1.Unregister(b);
                pump1.Dispose();
                Assert.That(clock1.HasObservers, Is.False, "stopped app is unsubscribed from its clock");
                Assert.That(app.Pumps, Is.EqualTo(0));
                Assert.That(app.Participants, Is.EqualTo(0));

                // F5: new app host with a new clock starting at 0, new renderers.
                var clock2 = new Clock(0);
                var pump2 = new QtPump(app, clock2);
                a = new Participant();
                b = new Participant();
                pump2.Register(a);
                pump2.Register(b);
                var frames = app.Frames;
                clock1.Frame(); // old clock: nothing
                Assert.That(app.Frames, Is.EqualTo(frames));
                clock2.Frame();
                clock2.Frame();
                Assert.That(app.Frames, Is.EqualTo(frames + 2), $"round {i}: new clock pumps");
                Assert.That(a.Applies, Is.EqualTo(2));
                Assert.That(b.Afters, Is.EqualTo(2));
                Assert.That(app.Pumps, Is.EqualTo(1));
                Assert.That(app.Participants, Is.EqualTo(2));
                pump1 = pump2;
                clock1 = clock2;
            }

            pump1.Unregister(a);
            pump1.Unregister(b);
            pump1.Dispose();
            Assert.That(app.Participants, Is.EqualTo(0));
            Assert.That(app.Registered, Is.EqualTo(app.Unregistered));
        }

        [Test]
        public void Dispose_BeforeRenderers_LeavesNoLeftovers()
        {
            var app = new FakeApp();
            var clock = new Clock(0);
            var pump = new QtPump(app, clock);
            var a = new Participant();
            pump.Register(a);
            pump.Dispose(); // app host disposes the service before the renderer
            Assert.That(app.Participants, Is.EqualTo(0));
            pump.Unregister(a); // renderer disposed afterwards: no-op
            pump.Register(new Participant()); // late register on a stopped app: ignored
            Assert.That(app.Participants, Is.EqualTo(0));
            Assert.That(pump.ParticipantCount, Is.EqualTo(0));
            pump.Dispose(); // idempotent
            Assert.That(app.Pumps, Is.EqualTo(0));
            clock.Frame();
            Assert.That(app.Frames, Is.EqualTo(0));
        }

        [Test]
        public void DriverTime_FirstAppUsesClockTime_RestartedAppIsShifted()
        {
            var app = new FakeApp();
            var clock1 = new Clock(50);
            var pump1 = new QtPump(app, clock1);
            clock1.Frame();
            Assert.That(app.DriverSeconds, Is.EqualTo(clock1.Time.Seconds).Within(1e-9), "first app: driver time = clock time");
            pump1.Dispose();

            var clock2 = new Clock(0);
            using var pump2 = new QtPump(app, clock2);
            var before = app.DriverSeconds;
            clock2.Frame();
            clock2.Frame();
            Assert.That(app.DriverSeconds, Is.GreaterThan(before));
            Assert.That(app.DriverSeconds - before, Is.EqualTo(1.0 / 60).Within(1e-9), "after the shift the driver advances with the new clock");
        }

        [Test]
        public void VvvvPhase_FromTickToPump_FallbackUpdateTime_NoStaleTick()
        {
            var app = new FakeApp();
            var clock = new Clock(0);
            var pump = new QtPump(app, clock);
            Assert.That(clock.HasTickObservers, "the pump listens to the frame start (#260)");

            // Host-side 10 ms spike between frame start and frame finished: the vvvv phase.
            clock.Tick();
            Thread.Sleep(10);
            clock.Frame();
            Assert.That(app.VvvvMs[^1], Is.GreaterThanOrEqualTo(9.5), "at least the sleep");
            // The upper bound is a timing limit (a loaded machine delays the thread): warn-only in the gate.
            PerfLimit.Below(app.VvvvMs[^1], 500, "vvvv phase of a 10 ms spike [ms]");

            // No tick (first frame / clock without ticks): the clock's update time.
            clock.Frame(updateSeconds: 0.004);
            Assert.That(app.VvvvMs[^1], Is.EqualTo(4).Within(1));

            // No tick, no update time: unknown. A tick is used for one pump only.
            clock.Frame();
            Assert.That(app.VvvvMs[^1], Is.EqualTo(-1));

            pump.Dispose();
            Assert.That(clock.HasTickObservers, Is.False);
        }
    }
}
