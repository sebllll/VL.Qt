using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VL.Lib.Animation;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// Global pump without vvvv: simulated IFrameClock, two windows, 120 frames.
[TestFixture, NonParallelizable]
public static class PumpTest
{
    /// <summary>Frame pump with several windows.</summary>
    [Test, Category("pump")]
    public static void Pump() => Scenario.Run(Run);

    private const float U = 100f;

    // Like vvvv's (WinForms) main loop between two frames: dispatches every Win32 message of the thread,
    // including the ones Qt posts to its own hidden window (timers, posted events, update requests).
    internal static void ForeignMessageLoop(int ms)
    {
        if (!OperatingSystem.IsWindows()) { System.Threading.Thread.Sleep(ms); return; } // no foreign Win32 loop on Linux
        var until = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
        while (Stopwatch.GetTimestamp() < until)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            System.Threading.Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Like <see cref="ForeignMessageLoop"/>, but waits until an absolute Stopwatch timestamp and spins for the last ~1.5 ms,
    /// so the frame period stays exact even with the default 15.6 ms Windows timer resolution (Sleep(1) sleeps up to a full tick).
    /// </summary>
    internal static void ForeignMessageLoopUntil(long deadline)
    {
        var spinTicks = Stopwatch.Frequency * 3 / 2000;
        while (true)
        {
            if (OperatingSystem.IsWindows())
            {
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            var left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0) return;
            if (left > spinTicks) System.Threading.Thread.Sleep(1);
            else System.Threading.Thread.SpinWait(50);
        }
    }

    /// <summary>Requests 1 ms timer resolution (Windows); dispose to release. No-op elsewhere.</summary>
    internal static IDisposable HighResTimer()
    {
        if (!OperatingSystem.IsWindows()) return new Release(false);
        timeBeginPeriod(1);
        return new Release(true);
    }
    private sealed class Release(bool active) : IDisposable { public void Dispose() { if (active) timeEndPeriod(1); } }
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; public uint lPrivate; }
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    private const uint RectId = 1, FxId = 2;

    private sealed class SimClock : IFrameClock
    {
        public readonly Subject<FrameFinishedMessage> Finished = new();
        public readonly Subject<FrameTimeMessage> Ticks = new();
        // Monotonic and later than the manual driver times of the earlier smoke tests (default start 10 000 s).
        private double _t;
        public SimClock() : this(10_000) { }

        public SimClock(double t)
        {
            _t = t;
            // QtNative ignores a driver time below the current one: the later scenarios (QtHost.Pump) go on after this clock.
            QtHost.Time = Math.Max(QtHost.Time, t + 100);
        }

        private ulong _n;
        public Time Time => new(_t);
        public double TimeDifference => 1.0 / 60;
        public IObservable<FrameTimeMessage> GetTicks() => Ticks;
        public IObservable<FrameFinishedMessage> GetFrameFinished() => Finished;

        /// <summary>vvvv frame start (FrameClock.BeginFrame), before the patch update.</summary>
        public void Tick() => Ticks.OnNext(new FrameTimeMessage(new Time(_t), _n, TimeSpan.Zero));

        /// <summary>Publishes one frame finished at the current clock time + dt.</summary>
        public void Frame(double dt)
        {
            _t += dt;
            Finished.OnNext(new FrameFinishedMessage(new Time(_t), new Time(0), _n++, TimeSpan.FromSeconds(dt)));
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Lines.Add($"[{logLevel}] {formatter(state, exception)}");
        }
    }

    // A window as participant of the pump (like Renderer): the rect moves every frame so each pump renders.
    private sealed class TestWindow : IQtFrameParticipant
    {
        private readonly CommandWriter _cmd = new();
        public readonly SceneHandle Handle;
        public int Applies, Afters, Frame;
        public Native.vlqt_perf Perf;
        public Native.vlqt_perf_sample Last;

        // effect: the rect sits in a Group with an effect layer (hidden layer source).
        public TestWindow(string title, bool effect = false)
        {
            Handle = NativeCalls.CreateWindow(400, 300, title);
            if (Handle == SceneHandle.Null) throw new InvalidOperationException(Native.LastError());
            _cmd.Clear();
            _cmd.ClearColor(0.125f, 0.125f, 0.125f, 1);
            _cmd.Create(RectId, Protocol.ElementType.Rectangle);
            _cmd.SetColor(RectId, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
            if (effect)
            {
                _cmd.Create(FxId, Protocol.ElementType.Group);
                _cmd.SetPlace(FxId, 0, 0, 4 * U, 3 * U);
                _cmd.Reparent(FxId, 0, 0);
                _cmd.Chain(FxId, c => c.MultiEffectBool(Protocol.MultiEffectKey.ShadowEnabled, true));
                _cmd.Reparent(RectId, FxId, 0);
            }
            else
                _cmd.Reparent(RectId, 0, 0);
            _cmd.SetPlace(RectId, 0, 0, 2 * U, 1 * U);
            Move();
            var rc = Native.Apply(Handle, _cmd.EndFrame()); // scene setup is not part of the pumped frames
            if (rc != 0) throw new InvalidOperationException(Native.LastError());
            Native.vlqt_window_show(Handle);
        }

        // Size + anchor once, then one SET per frame (TRANSFORM): the host frame reports ops 1.
        private void Move() => _cmd.SetMat4(RectId, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation((0.5f + (Frame++ % 100) * 0.01f) * U, 1 * U, 0));

        // "Update()": write the commands of this frame (before the pump).
        public void Update() { _cmd.Clear(); Move(); }

        // only the windows of the pumping app render in its pump.
        public SceneHandle Window => Handle;

        public void ApplyCommands() { Applies++; var rc = Native.Apply(Handle, _cmd.EndFrame()); if (rc != 0 && Applies < 5) Console.WriteLine("INFO pump apply rc " + rc + " " + Native.LastError()); }

        public void AfterEvents() { Afters++; Native.vlqt_scene_get_perf(Handle, out Perf); Native.vlqt_scene_get_perf_sample(Handle, out Last); }

        // Like WindowScene: ops of this window, then to the native PerfMonitor.
        public void ReportHostFrame(Native.vlqt_host_pump frame) { frame.ops = _cmd.Count; Native.vlqt_scene_report_host_pump(Handle, frame); }

        // Qt phases in the budget warning.
        public bool TryGetLastSample(out Native.vlqt_perf_sample sample) => Native.vlqt_scene_get_perf_sample(Handle, out sample) == 0;

        public void Destroy() => Native.vlqt_scene_destroy(Handle);
    }

    private sealed class Throwing : IQtFrameParticipant
    {
        public int Calls;
        public void ApplyCommands() { Calls++; throw new InvalidOperationException("test"); }
        public void AfterEvents() { Calls++; throw new InvalidOperationException("test"); }
    }

    private static void Run(Scenario s)
    {
        var clock = new SimClock();
        var log = new CollectingLogger();
        var app = QtApp.Create(log);
        var pump = new QtPump(app, clock);
        s.Check(app.ParticipantCount == 0, "pump: no participants at start");

        var a = new TestWindow("pump A");
        var b = new TestWindow("pump B");
        pump.Register(a);
        pump.Register(b);
        pump.Register(a); // duplicate is ignored
        s.Check(app.ParticipantCount == 2, $"pump: 2 participants ({app.ParticipantCount})");

        // 1. 120 frames, 2 windows: exactly one advance + processEvents per frame, 1 swap per window and pump.
        var ms = new double[120];
        var present = new double[120]; // Present share of the pump (both windows), WSLg limit below
        bool swapsOk = true;
        bool aSwapped = false, bSwapped = false;
        string swapErr = "";
        for (int f = 0; f < 120; f++)
        {
            a.Update(); b.Update();
            // Paced like vvvv at 60 fps: Qt's update request (idle time 0) fires between two pumps, not inside one.
            System.Threading.Thread.Sleep(16);
            var t = Stopwatch.GetTimestamp();
            clock.Frame(1.0 / 60);
            ms[f] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            present[f] = a.Last.present + b.Last.present;
            // Per pump (last perf sample): exactly 1 swap, also in the first pump: the gate counts expose renders too
            // (XWayland sends two Expose on show; before that 2 were allowed). Linux (X11, Wayland): the window is
            // mapped/configured asynchronously, pumps before its first swap may have none (platform, L4e). Windows: strict.
            // The 60-sample summary ring must hold one sample per pump.
            foreach (var w in new[] { a, b })
            {
                bool first = w == a ? !aSwapped : !bSwapped;
                if (w.Last.swaps > 0) { if (w == a) aSwapped = true; else bSwapped = true; }
                var minSwaps = !OperatingSystem.IsWindows() && first ? 0 : 1;
                var expect = Math.Min(f + 1, 60);
                if (w.Last.swaps != 1) s.Info($"pump frame {f} {w.Perf.samples} samples, swaps in this pump {w.Last.swaps}");
                if (w.Last.swaps < minSwaps || w.Last.swaps > 1 || w.Perf.samples != expect)
                {
                    swapsOk = false;
                    if (swapErr.Length == 0) swapErr = $" (first mismatch frame {f}: swaps {w.Last.swaps}, samples {w.Perf.samples}, expected 1 swap and {expect} samples)";
                }
            }
        }
        s.Check(app.Pumps == 120 && app.Advances == 120 && app.ProcessEventsCount == 120,
            $"pump: 120 frames -> pumps {app.Pumps}, advances {app.Advances}, processEvents {app.ProcessEventsCount}");
        s.Check(a.Applies == 120 && b.Applies == 120 && a.Afters == 120 && b.Afters == 120,
            $"pump: each window 120 apply / 120 after-events ({a.Applies}/{a.Afters}, {b.Applies}/{b.Afters})");
        s.Check(swapsOk, "pump: exactly 1 swap per window and frame (perf ring)" + swapErr);
        // QtApp reports apply/host/interval/GC per pump, the window adds its ops.
        var hrc = Native.vlqt_scene_get_host_pump(a.Handle, out var hf);
        Native.vlqt_scene_get_host_pump_stats(a.Handle, out var hs);
        s.Check(hrc == 0 && hf.apply > 0 && hf.interval > 0 && hf.ops == 1 && hf.alloc_bytes >= 0
              && hs.samples == 60 && hs.ops == 1 && hs.apply_ms > 0,
            $"pump: host frame reported (rc {hrc}, apply {hf.apply:F3}, interval {hf.interval:F1}, ops {hf.ops}, alloc {hf.alloc_bytes}; stats samples {hs.samples}, ops {hs.ops}, gen0/s {hs.gc0_per_s:F1})");
        s.Check(hf.vvvv == 0 && hf.wait == 0, $"pump: no tick, no clock update time -> vvvv/wait unknown (vvvv {hf.vvvv}, wait {hf.wait})");

        // host-side 10 ms spike in the vvvv update (tick -> frame finished) shows up as vvvv, not in the Qt phases.
        for (int f = 0; f < 2; f++)
        {
            a.Update(); b.Update();
            System.Threading.Thread.Sleep(16); // frame timer: wait
            clock.Tick();
            System.Threading.Thread.Sleep(f == 1 ? 10 : 0); // the "patch"
            clock.Frame(1.0 / 60);
        }
        hrc = Native.vlqt_scene_get_host_pump(a.Handle, out hf);
        Native.vlqt_scene_get_host_pump_stats(a.Handle, out hs);
        var qt = a.Last.events + a.Last.sync + a.Last.render + a.Last.present;
        // WSLg: Present copies to the host compositor (L4e) -> the Qt/host limits without the Present share.
        var wslgPresent = QtHost.Wslg ? a.Last.present + b.Last.present : 0;
        s.Check(hrc == 0 && hf.vvvv >= 9.5f && hf.vvvv < 100 && hf.wait >= 10 && hf.wait < 200
              && qt - (QtHost.Wslg ? a.Last.present : 0) < 9 && hf.apply < 9
              && hs.vvvv_ms_max >= 9.5 && hs.wait_ms_max >= 10 && hf.interval >= hf.vvvv + hf.wait,
            $"pump: 10 ms vvvv spike -> vvvv phase (vvvv {hf.vvvv:F2}, wait {hf.wait:F2}, apply {hf.apply:F2}, interval {hf.interval:F2}, Qt phases {qt:F2}{(QtHost.Wslg ? $", WSLg present {wslgPresent:F2}" : "")}; stats vvvv max {hs.vvvv_ms_max:F2}, wait max {hs.wait_ms_max:F2})");

        var warm = ms[10..];
        var sorted = (double[])warm.Clone();
        Array.Sort(sorted);
        var mean = Average(warm);
        s.Info($"pump time (2 windows, rect moves each frame), frames 10..119: mean {mean:F3} ms, median {sorted[sorted.Length / 2]:F3} ms, max {sorted[^1]:F3} ms; " +
            $"all 120 frames mean {Average(ms):F3} ms, first frame {ms[0]:F3} ms");
        // Acceptance 02a "pump < 2 ms for the test rect" is a per-window figure (spike: one window); with 2 windows the cost adds up.
        PumpLimit(s, mean / 2, Average(present[10..]) / 2, "per window",
            $"2 windows: {mean:F3} ms total, {mean / 2:F3} ms per window");

        // 1b. One window only, same pacing.
        pump.Unregister(b);
        // best of 3 rounds. Background load on the machine (other sessions, indexers) can double a whole round;
        // a real slowdown of the pump shows in every round.
        double mean1 = double.MaxValue, presentMean1 = 0;
        for (int round = 0; round < 3; round++)
        {
            var ms1 = new double[60];
            var present1 = new double[60];
            for (int f = 0; f < ms1.Length; f++)
            {
                a.Update();
                System.Threading.Thread.Sleep(16);
                var t = Stopwatch.GetTimestamp();
                clock.Frame(1.0 / 60);
                ms1[f] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                present1[f] = a.Last.present;
            }
            var sorted1 = ms1[10..];
            var roundMean = Average(sorted1);
            Array.Sort(sorted1);
            s.Info($"pump time (1 window, round {round + 1}), frames 10..59: mean {roundMean:F3} ms, median {sorted1[sorted1.Length / 2]:F3} ms, max {sorted1[^1]:F3} ms");
            if (roundMean < mean1) (mean1, presentMean1) = (roundMean, Average(present1[10..]));
            if (mean1 < 2.0 * PerfLimits.Factor) break; // within the limit, no need for another round
        }
        pump.Register(b);
        s.Perf("pump.mean_1_window", mean1);
        PumpLimit(s, mean1, presentMean1, "1 window", $"{mean1:F3} ms");

        // 1c. Moving content inside an effect layer source: exactly one swap per pump, no overlay needed,
        // no second render right after the first (that showed ~1100 fps in vvvv). Swaps are counted by the perf ring.
        pump.Unregister(a);
        pump.Unregister(b);
        var fx = new TestWindow("pump effect", effect: true);
        pump.Register(fx);
        int fxBad = 0;
        string fxErr = "";
        for (int f = 0; f < 100; f++)
        {
            fx.Update();
            ForeignMessageLoop(16);
            clock.Frame(1.0 / 60);
            if (f < 61) continue; // the perf ring (60 pumps) no longer holds frame 0 (setup + expose render)
            var swaps = Math.Round(fx.Perf.swaps_per_pump * fx.Perf.samples);
            var expect = fx.Perf.samples;
            if (swaps != expect || fx.Perf.frame_ms < 10)
            {
                if (fxBad++ == 0) fxErr = $" (first mismatch frame {f}: swaps {swaps} in {expect} pumps, frame {fx.Perf.frame_ms:F2} ms)";
            }
        }
        s.Check(fxBad == 0, "pump: effect layer source moving -> 1 swap per pump, frame >= 10 ms" + fxErr);
        pump.Unregister(fx);
        fx.Destroy();

        // 1d. Two vvvv apps (two entry points): each app has its own clock and pump, both pump the one QtApp once
        // per vvvv frame. Each window must render once per frame (in its own app's pump), not once per app (~120 fps).
        RunTwoApps(app, s);
        pump.Register(a);
        pump.Register(b);

        // 2. Pause/Play: while the clock stands still nothing is pumped; Play continues with the next frame.
        // Deltas: since 1d (two apps) not every pump advances the driver.
        var pumps = app.Pumps;
        var advances = app.Advances;
        System.Threading.Thread.Sleep(300);
        s.Check(app.Pumps == pumps && app.Advances == advances, "pause: no pump while the clock stands still");
        a.Update(); b.Update();
        clock.Frame(1.0 / 60);
        s.Check(app.Pumps == pumps + 1 && app.Advances == advances + 1, "play: pumping continues with the next frame, one advance");

        // 3. Register/unregister stress: 10x create + destroy a window between frames.
        for (int i = 0; i < 10; i++)
        {
            var w = new TestWindow($"stress {i}");
            pump.Register(w);
            a.Update(); b.Update(); w.Update();
            clock.Frame(1.0 / 60);
            s.Check(w.Applies == 1 && w.Afters == 1, $"stress {i}: new window pumped once");
            pump.Unregister(w);
            w.Destroy();
            pump.Unregister(w); // second unregister is a no-op
            a.Update(); b.Update();
            clock.Frame(1.0 / 60);
            s.Check(app.ParticipantCount == 2, $"stress {i}: participants back to 2 ({app.ParticipantCount})");
        }

        // 4. A failing participant does not stop the others.
        var bad = new Throwing();
        pump.Register(bad);
        var aBefore = a.Afters;
        a.Update(); b.Update();
        clock.Frame(1.0 / 60);
        s.Check(a.Afters == aBefore + 1 && bad.Calls == 2, "pump: a throwing participant does not stop the others");
        pump.Unregister(bad);

        pump.Unregister(a);
        pump.Unregister(b);
        a.Destroy();
        b.Destroy();
        s.Check(app.ParticipantCount == 0, "pump: all unregistered");
        var pumpErrors = log.Lines.FindAll(l => l.Contains("Qt is not pumped"));
        s.Check(pumpErrors.Count == 0, "pump: no thread/pump errors logged");
        RunRestart(app, pump, clock, s);
        foreach (var l in log.Lines) s.Info("pump log " + l);
    }

    private static void RunTwoApps(QtApp app, Scenario s)
    {
        var clockA = new SimClock();
        var clockB = new SimClock();
        var pumpA = new QtPump(app, clockA);
        var pumpB = new QtPump(app, clockB);
        var wa = new TestWindow("app A");
        var wb = new TestWindow("app B");
        pumpA.Register(wa);
        pumpB.Register(wb);
        int bad = 0;
        string err = "";
        for (int f = 0; f < 100; f++)
        {
            wa.Update(); wb.Update();
            ForeignMessageLoop(16);
            clockA.Frame(1.0 / 60);
            clockB.Frame(1.0 / 60);
            if (f < 61) continue; // the perf ring (60 pumps) no longer holds the setup/expose frames
            foreach (var w in new[] { wa, wb })
            {
                var swaps = Math.Round(w.Perf.swaps_per_pump * w.Perf.samples);
                if (w.Last.swaps != 1 || swaps != w.Perf.samples || w.Perf.frame_ms < 10)
                {
                    if (bad++ == 0)
                        err = $" (first mismatch frame {f}, {(w == wa ? "A" : "B")}: last pump {w.Last.swaps} swaps, {swaps} swaps in {w.Perf.samples} pumps, frame {w.Perf.frame_ms:F2} ms)";
                }
            }
        }
        s.Check(bad == 0, "two apps: each window 1 swap per vvvv frame, frame >= 10 ms (not one render per app)" + err);
        s.Check(wa.Applies == 100 && wb.Applies == 100 && wa.Afters == 100 && wb.Afters == 100,
            $"two apps: each window pumped once per frame by its own app ({wa.Applies}/{wa.Afters}, {wb.Applies}/{wb.Afters})");
        pumpA.Unregister(wa);
        pumpB.Unregister(wb);
        pumpA.Dispose();
        pumpB.Dispose();
        wa.Destroy();
        wb.Destroy();
    }

    // F8/F5 in vvvv stops the app (its app host disposes the pump service and the renderers) and starts a new app
    // with a new frame clock. QtApp (QGuiApplication) stays; the new app's pump must drive the new windows.
    private static void RunRestart(QtApp app, QtPump pump, SimClock clock, Scenario s)
    {
        // F8: the app host disposes its services (pump) - the old clock may still exist but no longer drives anything.
        pump.Dispose();
        var pumps = app.Pumps;
        clock.Frame(1.0 / 60);
        s.Check(app.Pumps == pumps && app.ActivePumps == 0, $"restart: stopped app no longer pumps (pumps +{app.Pumps - pumps}, active {app.ActivePumps})");

        for (int i = 0; i < 10; i++)
        {
            // F5: new app host, new clock. It starts below the driver time already reached (driver must not run backwards).
            var driverBefore = app.DriverSeconds;
            var clock2 = new SimClock(i);
            var pump2 = new QtPump(app, clock2);
            var w = new TestWindow($"restart {i}");
            pump2.Register(w);
            for (int f = 0; f < 10; f++)
            {
                w.Update();
                System.Threading.Thread.Sleep(16);
                clock2.Frame(1.0 / 60);
            }
            var visible = Native.vlqt_window_is_visible(w.Handle) != 0;
            s.Check(w.Applies == 10 && w.Afters == 10 && visible && w.Perf.samples > 0,
                $"restart {i}: window of the new app is pumped and renders (apply {w.Applies}, after {w.Afters}, visible {visible}, perf samples {w.Perf.samples})");
            s.Check(app.DriverSeconds > driverBefore, $"restart {i}: driver time monotonic ({driverBefore:F3} -> {app.DriverSeconds:F3})");
            s.Check(app.ActivePumps == 1 && app.ParticipantCount == 1, $"restart {i}: 1 pump, 1 participant ({app.ActivePumps}, {app.ParticipantCount})");

            // The old clock (previous app) must not drive the new app's window.
            var applies = w.Applies;
            clock.Frame(1.0 / 60);
            s.Check(w.Applies == applies, $"restart {i}: old clock does not pump the new app");

            // F8 again: renderer disposed (unregister + destroy), then the app host disposes the pump.
            // Every other round the pump goes first (renderer not unregistered cleanly) - no leftovers either way.
            if (i % 2 == 0) { pump2.Unregister(w); pump2.Dispose(); }
            else { pump2.Dispose(); pump2.Unregister(w); }
            w.Destroy();
            s.Check(app.ActivePumps == 0 && app.ParticipantCount == 0, $"restart {i}: stopped app leaves no pump/participant ({app.ActivePumps}, {app.ParticipantCount})");
        }
    }

    // Acceptance 02a: pump < 2 ms. WSLg (L4e): Present copies to the host compositor (4.6 ms d3d12, 12 ms
    // llvmpipe) -> hard limit on QtNative/Qt share (pump - present), the total stays visible as WARN.
    private static void PumpLimit(Scenario s, double pump, double present, string what, string detail)
    {
        if (!QtHost.Wslg)
        {
            PerfLimits.Below(s, pump, 2.0, $"pump: mean pump time {what} < 2 ms ({detail})");
            return;
        }
        PerfLimits.Below(s, pump - present, 2.0, $"pump: mean pump time minus present {what} < 2 ms (WSLg; {pump - present:F3} ms, {detail})");
        if (pump >= 2.0 * PerfLimits.Factor)
            s.Warn($"pump: mean pump time {what} {pump:F3} ms >= 2 ms, present {present:F3} ms (WSLg)");
    }

    private static double Average(double[] v) { double s = 0; foreach (var x in v) s += x; return s / v.Length; }
}
