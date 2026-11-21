using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VL.Qt.Wire;
using VL.Qt;

// SpinBox (package 1a): QtQuick.Controls SpinBox (style Basic) as value input. Raw protocol against QtNative:
// implicit size (AUTO_SIZE), EVENT_VALUE i32 only after user changes (+/- click, wheel, typed text on Enter and on focus
// loss), never for SET VALUE/MINIMUM/MAXIMUM, the captured user value survives a stale SET before the poll, several
// changes in one frame = one event with the last value, full int range, Editable/Enabled false, rebind drops a change
// not polled. Input comes from the TEST exports vlqt_test_send_mouse / _wheel / _key_text.
[TestFixture, NonParallelizable]
public static class SpinBoxTest
{
    /// <summary>SpinBox control.</summary>
    [Test, Category("spinbox")]
    public static void SpinBox() => Scenario.Run(Run);

    private const float StyleMin = 24; // BaseStyle FrameMinWidth/FrameMinHeight: lower bound of every implicit size
    private const int KeyA = 0x41;
    private const int KeyReturn = 0x01000004;
    private const int ControlModifier = 0x04000000;

    private readonly record struct Rec(ushort Event, uint Handle, byte[] Payload)
    {
        public float F(int i) => BinaryPrimitives.ReadSingleLittleEndian(Payload.AsSpan(4 * i));
        public ushort ValueType => BinaryPrimitives.ReadUInt16LittleEndian(Payload);
        public int I32 => BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(4));
    }

    private static List<Rec> Parse(ReadOnlySpan<byte> b)
    {
        var list = new List<Rec>();
        int off = 0;
        while (off + 12 <= b.Length)
        {
            ushort ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4));
            if (len < 4 || off + 8 + len > b.Length)
                break;
            list.Add(new Rec(ev, BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 8)), b.Slice(off + 12, len - 4).ToArray()));
            off += (8 + len + 3) & ~3;
        }
        return list;
    }

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt spinbox test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        List<Rec> Frame()
        {
            s.Pump(1);
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"poll ({n})");
            return Parse(buf.AsSpan(0, Math.Max(n, 0)));
        }
        void Mouse(int type, float x, float y) => s.Check(NativeTest.vlqt_test_send_mouse(w, type, x, y) == 0, $"test_send_mouse {type}");
        void Click(float x, float y)
        {
            Mouse(TestProtocol.Mouse.Move, x, y);
            Mouse(TestProtocol.Mouse.Press, x, y);
            Mouse(TestProtocol.Mouse.Release, x, y);
        }
        void Key(int key, int modifiers, string? text) => s.Check(NativeTest.vlqt_test_send_key_text(w, key, modifiers, text) == 0, $"send_key_text {key}");
        void Type(string s)
        {
            Key(KeyA, ControlModifier, null); // select all, typing replaces the text
            foreach (var c in s)
                Key(c, 0, c.ToString()); // '0'..'9' and '-' are their Qt key codes
        }
        static List<Rec> Values(List<Rec> recs, uint handle) => recs.FindAll(r => r.Event == Protocol.EventId.Value && r.Handle == handle);
        static string S(List<Rec> recs) => string.Join("; ", recs.ConvertAll(r => $"ev{r.Event} h{r.Handle} [{string.Join(",", r.Payload)}]"));
        void ExpectValue(List<Rec> recs, int expected, string what)
        {
            var v = Values(recs, H);
            s.Check(v.Count == 1 && v[0].ValueType == Protocol.ValueType.I32 && v[0].Payload.Length == 8 && v[0].I32 == expected,
                $"{what} -> one EVENT_VALUE i32 {expected} ({S(recs)})");
        }
        void ExpectNone(List<Rec> recs, string what) => s.Check(Values(recs, H).Count == 0, $"{what} -> no EVENT_VALUE ({S(recs)})");

        // Created with a large range and a value, bound without flags; AUTO_SIZE default on -> implicit size; SET no echo.
        cmd.Reset();
        cmd.Create(S1, Protocol.ElementType.SpinBox);
        VL.Qt.Styles.BaseStyle.Write(cmd, S1); // like the Reconciler
        cmd.SetPlace(S1, 100, 100, 0, 0);
        cmd.SetI32(S1, Protocol.PropertyId.Minimum, -2_000_000_000);
        cmd.SetI32(S1, Protocol.PropertyId.Maximum, 2_000_000_000);
        cmd.SetI32(S1, Protocol.PropertyId.Value, 5);
        cmd.SetI32(S1, Protocol.PropertyId.Step, 1);
        cmd.SetBool(S1, Protocol.PropertyId.Editable, true);
        cmd.SetBool(S1, Protocol.PropertyId.Enabled, true);
        cmd.Reparent(S1, 0, 0);
        cmd.Bind(S1, H);
        CheckApply("spinbox create + range/value/step/editable/enabled");
        Mouse(TestProtocol.Mouse.Move, 700, 500);
        s.Pump(2);
        var recs = Frame();
        recs.AddRange(Frame());
        var m = recs.FindLast(r => r.Event == Protocol.EventId.Measured && r.Handle == H);
        s.Check(m.Payload != null && m.F(0) >= StyleMin && m.F(1) >= StyleMin, $"implicit size ({S(recs)})");
        ExpectNone(recs, "SET VALUE/MINIMUM/MAXIMUM on creation");

        // Fixed Bounds from here (AUTO_SIZE off): text left, down and up buttons right.
        cmd.SetBool(S1, Protocol.PropertyId.AutoSize, false);
        cmd.SetPlace(S1, 100, 100, 240, 40);
        cmd.Create(S2, Protocol.ElementType.SpinBox);
        VL.Qt.Styles.BaseStyle.Write(cmd, S2); // like the Reconciler
        cmd.SetBool(S2, Protocol.PropertyId.AutoSize, false);
        cmd.SetPlace(S2, 100, 200, 240, 40);
        cmd.Reparent(S2, 0, 1);
        cmd.Bind(S2, H2);
        CheckApply("spinbox bounds + second spinbox");
        Frame();
        // Base style: both buttons at the right, squares of the box height minus the border (BaseStyle FrameBorderWidth 1).
        const float bs = 40 - 2 * 1, upX = 100 + 240 - 1 - bs / 2, downX = 100 + 240 - 1 - bs * 1.5f, textX = 100 + 100, y1 = 120, y2 = 220;

        // + -> 6 at once.
        Click(upX, y1);
        recs = Frame();
        ExpectValue(recs, 6, "click +");
        ExpectNone(Frame(), "next frame (sent once)");

        // Two - in one frame -> one event with the last value.
        Click(downX, y1);
        Click(downX, y1);
        ExpectValue(Frame(), 4, "two clicks - in one frame");

        // SET VALUE -> no echo.
        cmd.SetI32(S1, Protocol.PropertyId.Value, 100);
        CheckApply("set value 100");
        recs = Frame();
        recs.AddRange(Frame());
        ExpectNone(recs, "SET VALUE 100");

        // Wheel one notch up over the spinbox -> 101 at once.
        Mouse(TestProtocol.Mouse.Move, textX, y1);
        s.Check(NativeTest.vlqt_test_send_wheel(w, textX, y1, 0, 120) == 0, "send_wheel");
        ExpectValue(Frame(), 101, "wheel up");

        // Step 1000: + -> 1101.
        cmd.SetI32(S1, Protocol.PropertyId.Step, 1000);
        CheckApply("set step 1000");
        Click(upX, y1);
        ExpectValue(Frame(), 1101, "click + with step 1000");

        // Typed value: nothing until Enter, then the typed value (large negative, inside the range).
        Click(textX, y1);
        Type("-1999999999");
        ExpectNone(Frame(), "typing without Enter");
        Key(KeyReturn, 0, "\r");
        ExpectValue(Frame(), -1_999_999_999, "typed value + Enter");

        // Typed value committed by focus loss (click into the second spinbox).
        Type("123");
        ExpectNone(Frame(), "typing 123 without Enter");
        Click(textX, y2);
        recs = Frame();
        ExpectValue(recs, 123, "typed value + focus loss");
        s.Check(Values(recs, H2).Count == 0, $"focusing the second spinbox sends nothing for it ({S(recs)})");

        // User wins over a stale SET between change and poll: the captured user value still arrives.
        Click(upX, y1); // step 1000 -> 1123
        cmd.SetI32(S1, Protocol.PropertyId.Value, 123);
        CheckApply("stale set value after the user change");
        ExpectValue(Frame(), 1123, "user change + stale SET before the poll");
        ExpectNone(Frame(), "stale SET itself");

        // Full int range: + at int.MaxValue - 1 -> MaxValue, again -> nothing (no change); same at the minimum.
        cmd.SetI32(S1, Protocol.PropertyId.Step, 1);
        cmd.SetI32(S1, Protocol.PropertyId.Minimum, int.MinValue);
        cmd.SetI32(S1, Protocol.PropertyId.Maximum, int.MaxValue);
        cmd.SetI32(S1, Protocol.PropertyId.Value, int.MaxValue - 1);
        CheckApply("full int range");
        ExpectNone(Frame(), "SET full int range");
        Click(upX, y1);
        ExpectValue(Frame(), int.MaxValue, "+ to int.MaxValue");
        Click(upX, y1);
        ExpectNone(Frame(), "+ at int.MaxValue");
        cmd.SetI32(S1, Protocol.PropertyId.Value, int.MinValue + 1);
        CheckApply("value int.MinValue + 1");
        Click(downX, y1);
        ExpectValue(Frame(), int.MinValue, "- to int.MinValue");

        // SET MAXIMUM below the value: Qt clamps without an event (no echo).
        cmd.SetI32(S1, Protocol.PropertyId.Value, 50);
        cmd.SetI32(S1, Protocol.PropertyId.Maximum, 10);
        CheckApply("maximum below the value");
        ExpectNone(Frame(), "clamp by SET MAXIMUM");
        Click(downX, y1);
        ExpectValue(Frame(), 9, "- after the clamp to 10");

        // Editable false: typing + Enter changes nothing; +/- still work.
        cmd.SetBool(S1, Protocol.PropertyId.Editable, false);
        cmd.SetI32(S1, Protocol.PropertyId.Maximum, 1000);
        CheckApply("editable false");
        Click(textX, y1);
        Type("77");
        Key(KeyReturn, 0, "\r");
        ExpectNone(Frame(), "typing with Editable false");
        Click(upX, y1);
        ExpectValue(Frame(), 10, "+ with Editable false");

        // Enabled false: + and wheel change nothing.
        cmd.SetBool(S1, Protocol.PropertyId.Enabled, false);
        CheckApply("enabled false");
        Click(upX, y1);
        s.Check(NativeTest.vlqt_test_send_wheel(w, textX, y1, 0, 120) == 0, "send_wheel disabled");
        ExpectNone(Frame(), "+ and wheel with Enabled false");

        // A new feedback id drops a change not polled yet (like Button clicks).
        cmd.SetBool(S1, Protocol.PropertyId.Enabled, true);
        CheckApply("enabled true");
        Click(upX, y1);
        cmd.Bind(S1, H3);
        CheckApply("rebind");
        recs = Frame();
        s.Check(Values(recs, H).Count == 0 && Values(recs, H3).Count == 0, $"rebind drops the unpolled change ({S(recs)})");
        Click(upX, y1);
        recs = Frame();
        var v3 = Values(recs, H3);
        s.Check(v3.Count == 1 && v3[0].I32 == 12, $"new handle gets the next change ({S(recs)})");

        cmd.Destroy(S1);
        cmd.Destroy(S2);
        CheckApply("spinbox destroy");
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }

    private const uint S1 = 1;
    private const uint S2 = 2;
    private const uint H = 41;
    private const uint H2 = 42;
    private const uint H3 = 43;
}
