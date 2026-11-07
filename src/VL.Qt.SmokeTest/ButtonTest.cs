using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using VL.Qt;

// Button (#213, package A): QtQuick.Controls Button with style Basic. Raw protocol against QtNative: creation, implicit
// size from the text (AUTO_SIZE default on), Bounds size with AUTO_SIZE off, EV_CLICK per clicked signal without the
// Interactive flag, Enabled false suppresses clicks, AutoRepeat repeats clicks while held. Mouse input comes from the
// TEST export vlqt_test_send_mouse (see InteractionTest).
static class ButtonTest
{
    readonly record struct Rec(ushort Event, uint Handle, byte[] Payload)
    {
        public float F(int i) => BinaryPrimitives.ReadSingleLittleEndian(Payload.AsSpan(4 * i));
        public uint U32 => BinaryPrimitives.ReadUInt32LittleEndian(Payload);
    }

    static List<Rec> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off + 12 <= b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 2));
            if (len < 8 || off + 4 + len > b.Length)
                break;
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4)), b.Slice(off + 12, len - 8).ToArray()));
            off += (4 + len + 3) & ~3;
        }
        return list;
    }

    public static void Run(Action<bool, string> check, Action<int> pump)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt button test");
        check(w != IntPtr.Zero, "213: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"213: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        List<Rec> Frame()
        {
            pump(1);
            int n = Native.Poll(w, buf);
            check(n >= 0, $"213: poll ({n})");
            return Parse(buf.AsSpan(0, Math.Max(n, 0)));
        }
        void Mouse(int type, float x, float y) => check(Native.vlqt_test_send_mouse(w, type, x, y) == 0, $"213: test_send_mouse {type}");
        static Rec? Last(List<Rec> recs, ushort ev)
        {
            Rec? found = null;
            foreach (var r in recs)
                if (r.Event == ev && r.Handle == H) found = r;
            return found;
        }
        static uint Clicks(List<Rec> recs)
        {
            uint c = 0;
            foreach (var r in recs)
                if (r.Event == Protocol.Ev.Click && r.Handle == H) c += r.U32;
            return c;
        }
        static string S(List<Rec> recs) => string.Join("; ", recs.ConvertAll(r => $"ev{r.Event} h{r.Handle} [{string.Join(",", r.Payload)}]"));

        // Created with text, bound without flags; Bounds size 0 + AUTO_SIZE default on -> implicit size.
        cmd.Reset();
        cmd.Create(B, Protocol.Type.Button);
        VL.Qt.Styles.BaseStyle.Write(cmd, B); // like the Reconciler (#292)
        cmd.SetPlace(B, 100, 100, 0, 0);
        cmd.SetString(B, Protocol.Prop.Text, "Click me");
        cmd.SetBool(B, Protocol.Prop.Enabled, true);
        cmd.SetBool(B, Protocol.Prop.AutoRepeat, false);
        cmd.Reparent(B, 0, 0);
        cmd.Bind(B, H, 0);
        CheckApply("button create + text/enabled/autoRepeat");
        Mouse(Protocol.Mouse.Move, 700, 500);
        pump(2);
        var recs = Frame();
        recs.AddRange(Frame());
        var m = Last(recs, Protocol.Ev.Measured);
        var sb = Last(recs, Protocol.Ev.SceneBounds);
        float w0 = m?.F(0) ?? 0, h0 = m?.F(1) ?? 0;
        check(w0 > 20 && h0 > 10, $"213: implicit size from text ({w0} x {h0}) ({S(recs)})");
        check(sb is { } b0 && b0.F(0) == 100 && b0.F(1) == 100 && b0.F(2) == w0 && b0.F(3) == h0,
            $"213: AUTO_SIZE on -> item size = implicit size ({S(recs)})");
        check(Native.vlqt_test_handler_count(w, B) == 0, "213: no Interactive handlers needed for clicks");

        // Longer text -> wider implicit size, follows without a new Bounds.
        cmd.SetString(B, Protocol.Prop.Text, "Click me, I am a much longer label");
        CheckApply("button longer text");
        recs = Frame();
        var m1 = Last(recs, Protocol.Ev.Measured);
        check(m1 is { } mm && mm.F(0) > w0 + 50, $"213: longer text -> wider implicit width ({S(recs)})");
        float w1 = m1?.F(0) ?? w0;

        // Click = press + release over the button -> EV_CLICK 1 without the Interactive flag.
        float cx = 100 + w1 / 2, cy = 100 + h0 / 2;
        Mouse(Protocol.Mouse.Move, cx, cy);
        Mouse(Protocol.Mouse.Press, cx, cy);
        recs = Frame();
        check(Clicks(recs) == 0, $"213: press alone -> no click ({S(recs)})");
        Mouse(Protocol.Mouse.Release, cx, cy);
        recs = Frame();
        check(Clicks(recs) == 1, $"213: release over the button -> CLICK 1 ({S(recs)})");
        recs = Frame();
        check(Clicks(recs) == 0, $"213: click is sent once ({S(recs)})");

        // Release outside -> no click (Qt default).
        Mouse(Protocol.Mouse.Press, cx, cy);
        Mouse(Protocol.Mouse.Move, 700, 500);
        Mouse(Protocol.Mouse.Release, 700, 500);
        recs = Frame();
        check(Clicks(recs) == 0, $"213: release outside -> no click ({S(recs)})");

        // Enabled false -> no clicks.
        cmd.SetBool(B, Protocol.Prop.Enabled, false);
        CheckApply("button disabled");
        Mouse(Protocol.Mouse.Move, cx, cy);
        Mouse(Protocol.Mouse.Press, cx, cy);
        Mouse(Protocol.Mouse.Release, cx, cy);
        recs = Frame();
        check(Clicks(recs) == 0, $"213: disabled -> no click ({S(recs)})");

        // AutoRepeat: holding the press repeats clicked (Qt: delay 300 ms, interval 100 ms, wall clock timers).
        cmd.SetBool(B, Protocol.Prop.Enabled, true);
        cmd.SetBool(B, Protocol.Prop.AutoRepeat, true);
        CheckApply("button autoRepeat");
        Mouse(Protocol.Mouse.Press, cx, cy);
        uint repeated = 0;
        // Hold until 3 repeats arrived (typically ~700 ms), at most 3 s: a stalled machine delays the wall clock timers,
        // a missing auto repeat never gets there (#379).
        var held = System.Diagnostics.Stopwatch.StartNew();
        while (repeated < 3 && held.ElapsedMilliseconds < 3000)
        {
            Thread.Sleep(50);
            repeated += Clicks(Frame());
        }
        Mouse(Protocol.Mouse.Release, cx, cy);
        repeated += Clicks(Frame());
        check(repeated >= 3, $"213: autoRepeat -> repeated clicks while held ({repeated})");

        // AUTO_SIZE off -> Bounds size counts.
        cmd.SetBool(B, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(B, 100, 100, 200, 60);
        CheckApply("button AUTO_SIZE off");
        recs = Frame();
        var sb2 = Last(recs, Protocol.Ev.SceneBounds);
        check(sb2 is { } b2 && b2.F(2) == 200 && b2.F(3) == 60, $"213: AUTO_SIZE off -> Bounds size ({S(recs)})");

        cmd.Destroy(B);
        CheckApply("button destroy");
        Native.vlqt_window_destroy(w);
        pump(1);
    }

    const uint B = 1;
    const uint H = 31;
}
