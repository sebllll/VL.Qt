using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using VL.Lib.Animation;
using VL.Qt;

// Input latency (#24): input queued like platform input between two frames (vlqt_test_queue_mouse / _touch) reaches the
// sinks at the frame start (QtPump tick: vlqt_app_deliver_input, then PollInput), i.e. before the node updates of that
// frame. Before #24 it reached them at the frame end, so the patch saw it one frame later. Measured in both modes on a
// simulated clock (QtPump.DeliverInputAtFrameStart), plus: no event twice or lost (press/release pairs, multitouch), no
// render in the delivery, a window destroyed or created between two frames.
static partial class HandlerTest
{
    sealed class LatencyClock : IFrameClock
    {
        public readonly Subject<FrameFinishedMessage> Finished = new();
        public readonly Subject<FrameTimeMessage> Ticks = new();
        double _t = 20_000; // later than the driver times of the earlier scenarios
        ulong _n;
        public Time Time => new(_t);
        public double TimeDifference => 1.0 / 60;
        public IObservable<FrameTimeMessage> GetTicks() => Ticks;
        public IObservable<FrameFinishedMessage> GetFrameFinished() => Finished;
        public void Tick() => Ticks.OnNext(new FrameTimeMessage(new Time(_t), _n, TimeSpan.Zero));
        public void Frame()
        {
            _t += 1.0 / 60;
            Finished.OnNext(new FrameFinishedMessage(new Time(_t), new Time(0), _n++, TimeSpan.FromSeconds(1.0 / 60)));
        }
    }

    sealed class LatencyLogger : ILogger
    {
        public readonly List<string> Errors = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }

    // A renderer of the pump: the polled pointer records stand for the sinks the element nodes read in their update.
    sealed class LatencyWindow : IQtFrameParticipant
    {
        readonly byte[] _buf = new byte[1 << 16];
        readonly Action<bool, string> _check;
        public readonly IntPtr W;
        public readonly List<Rec> Sinks = new(); // since the last Clear (one measurement)
        public readonly List<Rec> All = new();   // the whole run (pairs, duplicates)
        public bool Destroyed;

        public LatencyWindow(Action<bool, string> check, string title)
        {
            _check = check;
            W = Native.vlqt_window_create(800, 600, title);
            check(W != IntPtr.Zero, $"input-latency: window_create {title}");
            Native.vlqt_window_show(W);
        }

        public IntPtr Window => Destroyed ? IntPtr.Zero : W;
        public void ApplyCommands() { }
        public void AfterEvents() => Poll();
        public void PollInput() => Poll();

        void Poll()
        {
            if (Destroyed)
                return;
            int n = Native.Poll(W, _buf);
            if (n < 0)
                _check(false, $"input-latency: poll ({n}) {Native.LastError()}");
            var recs = Parse(_buf.AsSpan(0, Math.Max(n, 0)), _check).FindAll(r => IsPointerEvent(r.Event));
            Sinks.AddRange(recs);
            All.AddRange(recs);
        }

        public void Destroy()
        {
            Destroyed = true;
            Native.vlqt_window_destroy(W);
        }
    }

    public static void RunInputLatency(Action<bool, string> check)
    {
        var log = new LatencyLogger();
        var app = QtApp.CreateForTest(log);
        var before = MeasureLatency(app, check, atFrameStart: false, out var recsBefore);
        var after = MeasureLatency(app, check, atFrameStart: true, out var recsAfter);
        Console.WriteLine($"INFO input-latency: frames from the frame start after the input to the node update that sees it (mouse press, release, touch press, move, release): before {string.Join(",", before)}, now {string.Join(",", after)}");
        // Before #24 the touch move (index 3) waits for the render: Qt Quick holds it back and requests an update. Windows
        // delivers that update request within the same pump; Linux (QPlatformWindow::requestUpdate: a timer of one refresh
        // interval, ~16 ms) delivers it after this test's next pump (2 ms apart), so the move may take two frames there.
        var beforeOk = true;
        for (int i = 0; i < before.Length; i++)
            beforeOk &= before[i] == 1 || (i == 3 && before[i] == 2 && !OperatingSystem.IsWindows());
        check(beforeOk, $"input-latency: before #24 the patch sees queued input one frame later, the touch move up to two on Linux ({string.Join(",", before)})");
        check(Array.TrueForAll(after, l => l == 0), $"input-latency: input reaches the patch in the frame it arrived before ({string.Join(",", after)})");
        // Nothing twice, nothing lost: the sinks get the same records in the same order as with the poll at the frame end only.
        check(Dump(recsBefore) == Dump(recsAfter), $"input-latency: same records as before #24 (before: {Dump(recsBefore)} | now: {Dump(recsAfter)})");
        LatencyLifecycle(app, check);
        check(log.Errors.Count == 0, $"input-latency: no errors logged ({string.Join(" | ", log.Errors)})");
    }

    // Latencies in frames of: mouse press, mouse release, two fingers down, finger 1 moves, both fingers up.
    static int[] MeasureLatency(QtApp app, Action<bool, string> check, bool atFrameStart, out List<Rec> records)
    {
        var tag = atFrameStart ? "now" : "before";
        var clock = new LatencyClock();
        var pump = new QtPump(app, clock) { DeliverInputAtFrameStart = atFrameStart };
        var w = new LatencyWindow(check, $"VL.Qt input-latency {tag}");
        const uint A = 1, TA = 10, TP = 11, STA = 41, STP = 42;
        var cmd = new CommandWriter();
        cmd.Create(A, Protocol.Type.Rectangle);
        cmd.SetPlace(A, 100, 100, 300, 300);
        cmd.Reparent(A, 0, 0);
        foreach (var (id, type, sink) in new[] { (TA, Protocol.Type.TapHandler, STA), (TP, Protocol.Type.TouchPointsHandler, STP) })
        {
            cmd.Create(id, type);
            cmd.Reparent(id, A, 0);
            cmd.Bind(id, sink, 0);
        }
        var rc = Native.Apply(w.W, cmd.EndFrame());
        check(rc == 0, $"input-latency {tag}: scene ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        pump.Register(w);
        for (int i = 0; i < 3; i++)
            Frame(clock);

        // Frames from the first frame start after the input until the "node update" (between tick and frame end) sees it.
        int Latency(Action input, Func<List<Rec>, bool> seen)
        {
            w.Sinks.Clear();
            input();
            PumpTest.ForeignMessageLoop(2); // vvvv's message loop between two frames
            for (int k = 0; k < 4; k++)
            {
                clock.Tick();
                if (seen(w.Sinks))
                {
                    Frame(clock);
                    return k;
                }
                Frame(clock);
            }
            return -1;
        }
        void Mouse(int type, float x, float y) => check(Native.vlqt_test_queue_mouse(w.W, type, x, y) == 0, $"input-latency {tag}: queue_mouse {type}");
        void Touch(int type, int id, float x, float y) => check(Native.vlqt_test_queue_touch(w.W, type, id, x, y) == 0, $"input-latency {tag}: queue_touch {type} {id}");

        var lat = new int[5];
        lat[0] = Latency(() => { Mouse(Protocol.Mouse.Move, 200, 200); Mouse(Protocol.Mouse.Press, 200, 200); },
            s => HasFlag(s, Protocol.Ev.Press, STA, true));
        lat[1] = Latency(() => Mouse(Protocol.Mouse.Release, 200, 200), s => HasU32(s, Protocol.Ev.Click, STA, 1));
        Frame(clock);
        var mouse = new List<Rec>(w.All);
        lat[2] = Latency(() => { Touch(Protocol.Touch.Press, 1, 150, 150); Touch(Protocol.Touch.Press, 2, 250, 200); },
            s => HasPoints(s, STP, (1, 50, 50), (2, 150, 100)));
        lat[3] = Latency(() => Touch(Protocol.Touch.Move, 1, 160, 170), s => HasPoints(s, STP, (1, 60, 70), (2, 150, 100)));
        lat[4] = Latency(() => { Touch(Protocol.Touch.Release, 1, 160, 170); Touch(Protocol.Touch.Release, 2, 250, 200); },
            s => HasPoints(s, STP));
        for (int i = 0; i < 2; i++)
            Frame(clock);

        // The mouse click: one press/release pair and one click (the grab of the Tap handler held from press to release);
        // the touch points: the last state has no point left (none lost or stuck).
        int presses = mouse.FindAll(r => r.Event == Protocol.Ev.Press && r.Handle == STA && r.Flag).Count;
        int releases = mouse.FindAll(r => r.Event == Protocol.Ev.Press && r.Handle == STA && !r.Flag).Count;
        int clicks = mouse.FindAll(r => r.Event == Protocol.Ev.Click && r.Handle == STA).Count;
        check(presses == 1 && releases == 1 && clicks == 1,
            $"input-latency {tag}: mouse press/release pair and one click (press {presses}, release {releases}, click {clicks}; {Dump(mouse)})");
        check(LastTouch(w.All, STP) is { Length: 0 }, $"input-latency {tag}: all touch points released ({Pts(w.All, STP)})");
        records = new List<Rec>(w.All);

        pump.Unregister(w);
        w.Destroy();
        pump.Dispose();
        Frame(clock); // a disposed pump neither delivers nor pumps
        return lat;
    }

    static void Frame(LatencyClock clock)
    {
        clock.Frame();
        PumpTest.ForeignMessageLoop(2);
    }

    // Lifecycle: a window destroyed with queued input, a renderer without window, a window created between two frames.
    static void LatencyLifecycle(QtApp app, Action<bool, string> check)
    {
        var clock = new LatencyClock();
        var pump = new QtPump(app, clock);
        var a = new LatencyWindow(check, "VL.Qt input-latency lifecycle A");
        var gone = new LatencyWindow(check, "VL.Qt input-latency lifecycle B");
        pump.Register(a);
        pump.Register(gone);
        for (int i = 0; i < 3; i++)
            Frame(clock);

        // Input queued for a window that is destroyed before the next frame starts: Qt drops it, A still gets its own.
        check(Native.vlqt_test_queue_touch(gone.W, Protocol.Touch.Press, 7, 50, 50) == 0, "input-latency: queue_touch for B");
        check(Native.vlqt_test_queue_mouse(a.W, Protocol.Mouse.Move, 20, 20) == 0, "input-latency: queue_mouse for A");
        gone.Destroy(); // still registered: a renderer whose window is gone (Window zero, PollInput skips it)
        clock.Tick();
        check(app.LastDeliveredInput >= 0, $"input-latency: deliver with a destroyed window -> no crash ({app.LastDeliveredInput})");
        Frame(clock);
        pump.Unregister(gone);

        // A window created and shown between two frames, input queued for it right away: the delivery at the frame start
        // renders nothing (a render there would count into the pump's sample), the first pump renders it exactly once.
        var born = new LatencyWindow(check, "VL.Qt input-latency lifecycle C");
        pump.Register(born);
        check(Native.vlqt_test_queue_mouse(born.W, Protocol.Mouse.Move, 30, 30) == 0, "input-latency: queue_mouse for C");
        clock.Tick();
        Frame(clock);
        var src = Native.vlqt_window_get_perf_sample(born.W, out var first);
        check(src == 0 && first.swaps <= 1 && (first.swaps == 1 || !OperatingSystem.IsWindows()),
            $"input-latency: new window rendered once in its first pump, not in the delivery (rc {src}, swaps {first.swaps})");

        // Not in a pump: the native call alone delivers and returns >= 0; a second call has nothing left.
        check(Native.vlqt_test_queue_mouse(a.W, Protocol.Mouse.Move, 40, 40) == 0, "input-latency: queue_mouse for A again");
        var delivered = Native.vlqt_app_deliver_input();
        var again = Native.vlqt_app_deliver_input();
        check(delivered >= 1 && again == 0, $"input-latency: deliver_input -> queued events once ({delivered}, then {again})");

        pump.Unregister(a);
        pump.Unregister(born);
        a.Destroy();
        born.Destroy();
        pump.Dispose();
        Native.vlqt_app_process_events();
    }
}
