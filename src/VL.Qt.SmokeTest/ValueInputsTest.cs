using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using VL.Qt.Wire;
using VL.Qt;

// Value inputs of #301 (package 2a): QtQuick.Controls CheckBox and Switch (Toggle, bool), Slider (f32) and TextField
// (string), style Basic, raw protocol against QtNative. Same guarantees as SpinBox: EV_VALUE only after user
// changes (click/Space, press/drag/wheel/arrow keys, Enter/focus loss with changed text), never for SET, the user value
// captured at the signal survives a stale SET before the poll, several changes in one frame = one event, a rebind drops
// a change not polled. Input comes from the TEST exports vlqt_test_send_mouse / _wheel / _key_text.
[TestFixture, NonParallelizable]
public static class ValueInputsTest
{
    /// <summary>Value input controls.</summary>
    [Test, Category("valueinputs")]
    public static void ValueInputs() => Scenario.Run(Run);

    // Slider: distance of the handle centre at 0 / 1 from the Bounds edge. Base style: frame padding + handle radius.
    private const float SliderInset = 8 + 14 / 2; // BaseStyle FramePaddingX + IndicatorSize / 2
    private const float StyleMin = 24; // BaseStyle FrameMinWidth/FrameMinHeight: lower bound of every implicit size
    private const int KeyA = 0x41;
    private const int KeySpace = 0x20;
    private const int KeyEscape = 0x01000000;
    private const int KeyReturn = 0x01000004;
    private const int KeyRight = 0x01000014;
    private const int ControlModifier = 0x04000000;

    private readonly record struct Rec(ushort Event, uint Handle, byte[] Payload)
    {
        public float F(int i) => BinaryPrimitives.ReadSingleLittleEndian(Payload.AsSpan(4 * i));
        public ushort ValueType => BinaryPrimitives.ReadUInt16LittleEndian(Payload);
        public float F32 => BinaryPrimitives.ReadSingleLittleEndian(Payload.AsSpan(4));
        public string Str => Encoding.UTF8.GetString(Payload, 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(4)));
    }

    private static List<Rec> Parse(ReadOnlySpan<byte> b)
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

    private static void Run(Scenario s)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt value inputs test");
        s.Check(w != IntPtr.Zero, "window_create");
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
            int n = Native.Poll(w, buf);
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
                Key(c < 128 ? char.ToUpperInvariant(c) : 0, 0, c.ToString()); // Qt key codes of letters are upper case
        }
        static List<Rec> Values(List<Rec> recs, uint handle) => recs.FindAll(r => r.Event == Protocol.Ev.Value && r.Handle == handle);
        static string S(List<Rec> recs) => string.Join("; ", recs.ConvertAll(r => $"ev{r.Event} h{r.Handle} [{string.Join(",", r.Payload)}]"));
        void ExpectNone(List<Rec> recs, uint h, string what) => s.Check(Values(recs, h).Count == 0, $"{what} -> no EV_VALUE ({S(recs)})");
        void ExpectBool(List<Rec> recs, uint h, bool expected, string what)
        {
            var v = Values(recs, h);
            s.Check(v.Count == 1 && v[0].ValueType == Protocol.Vt.Bool && v[0].Payload.Length == 5 && v[0].Payload[4] == (expected ? 1 : 0),
                $"{what} -> one EV_VALUE bool {expected} ({S(recs)})");
        }
        void ExpectF32(List<Rec> recs, uint h, float expected, float tolerance, string what)
        {
            var v = Values(recs, h);
            s.Check(v.Count == 1 && v[0].ValueType == Protocol.Vt.F32 && v[0].Payload.Length == 8 && Math.Abs(v[0].F32 - expected) <= tolerance,
                $"{what} -> one EV_VALUE f32 {expected}±{tolerance} ({S(recs)})");
        }
        void ExpectString(List<Rec> recs, uint h, string expected, string what)
        {
            var v = Values(recs, h);
            s.Check(v.Count == 1 && v[0].ValueType == Protocol.Vt.String && v[0].Payload.Length >= 8 && v[0].Str == expected,
                $"{what} -> one EV_VALUE string \"{expected}\" ({S(recs)})");
        }
        Rec? Measured(List<Rec> recs, uint h)
        {
            var i = recs.FindLastIndex(r => r.Event == Protocol.Ev.Measured && r.Handle == h);
            return i >= 0 ? recs[i] : null;
        }

        // --- Toggle: CheckBox and Switch, no text. AUTO_SIZE default on -> indicator + padding; SET VALUE no echo.
        cmd.Reset();
        cmd.Create(C, Protocol.Type.CheckBox);
        VL.Qt.Styles.BaseStyle.Write(cmd, C); // like the Reconciler
        cmd.SetPlace(C, 100, 100, 0, 0);
        cmd.SetBool(C, Protocol.Prop.Value, false);
        cmd.SetBool(C, Protocol.Prop.Enabled, true);
        cmd.Reparent(C, 0, 0);
        cmd.Bind(C, HC, 0);
        cmd.Create(Sw, Protocol.Type.Switch);
        VL.Qt.Styles.BaseStyle.Write(cmd, Sw); // like the Reconciler
        cmd.SetPlace(Sw, 300, 100, 0, 0);
        cmd.SetBool(Sw, Protocol.Prop.Value, true);
        cmd.Reparent(Sw, 0, 1);
        cmd.Bind(Sw, HS, 0);
        CheckApply("checkbox + switch create");
        Mouse(TestProtocol.Mouse.Move, 790, 590);
        s.Pump(2);
        var recs = Frame();
        recs.AddRange(Frame());
        var mc = Measured(recs, HC);
        var ms = Measured(recs, HS);
        s.Check(mc is { } a && a.F(0) >= StyleMin && a.F(0) <= 48 && a.F(1) >= StyleMin && a.F(1) <= 48, $"checkbox implicit size without text ({S(recs)})");
        s.Check(ms is { } b && b.F(0) >= StyleMin && b.F(0) <= 80 && b.F(1) >= StyleMin && b.F(1) <= 48, $"switch implicit size without text ({S(recs)})");
        ExpectNone(recs, HC, "checkbox SET VALUE on creation");
        ExpectNone(recs, HS, "switch SET VALUE on creation");
        float cx = 100 + (mc?.F(0) ?? 40) / 2, cy = 100 + (mc?.F(1) ?? 40) / 2;
        float sx = 300 + (ms?.F(0) ?? 68) / 2, sy = 100 + (ms?.F(1) ?? 40) / 2;

        Click(cx, cy);
        ExpectBool(recs = Frame(), HC, true, "checkbox click");
        ExpectNone(Frame(), HC, "checkbox next frame (sent once)");
        Key(KeySpace, 0, " "); // the click gave it focus
        ExpectBool(Frame(), HC, false, "checkbox Space");
        Click(cx, cy);
        Click(cx, cy);
        ExpectBool(Frame(), HC, false, "checkbox two clicks in one frame (last value)");
        cmd.SetBool(C, Protocol.Prop.Value, true);
        CheckApply("checkbox set value true");
        recs = Frame();
        recs.AddRange(Frame());
        ExpectNone(recs, HC, "checkbox SET VALUE true");
        Click(cx, cy); // true -> false by the user
        cmd.SetBool(C, Protocol.Prop.Value, true); // stale SET before the poll
        CheckApply("checkbox stale set");
        ExpectBool(Frame(), HC, false, "checkbox user change + stale SET before the poll");
        cmd.SetBool(C, Protocol.Prop.Enabled, false);
        CheckApply("checkbox enabled false");
        Click(cx, cy);
        ExpectNone(Frame(), HC, "checkbox click with Enabled false");
        cmd.SetBool(C, Protocol.Prop.Enabled, true);
        CheckApply("checkbox enabled true");
        Click(cx, cy);
        cmd.Bind(C, HC2, 0);
        CheckApply("checkbox rebind");
        recs = Frame();
        s.Check(Values(recs, HC).Count == 0 && Values(recs, HC2).Count == 0, $"checkbox rebind drops the unpolled change ({S(recs)})");
        Click(cx, cy);
        ExpectBool(Frame(), HC2, true, "checkbox new handle gets the next change");

        Click(sx, sy);
        ExpectBool(Frame(), HS, false, "switch click");
        Key(KeySpace, 0, " ");
        ExpectBool(Frame(), HS, true, "switch Space");
        cmd.SetBool(Sw, Protocol.Prop.Value, false);
        CheckApply("switch set value false");
        recs = Frame();
        recs.AddRange(Frame());
        ExpectNone(recs, HS, "switch SET VALUE false");
        Click(sx, sy);
        cmd.SetBool(Sw, Protocol.Prop.Value, false);
        CheckApply("switch stale set");
        ExpectBool(Frame(), HS, true, "switch user change + stale SET before the poll");
        // Fixed Bounds larger than the implicit size: the indicator stays centred and clickable.
        cmd.SetBool(Sw, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(Sw, 300, 100, 160, 60);
        CheckApply("switch fixed bounds");
        Frame();
        Click(380, 130);
        ExpectBool(Frame(), HS, true, "switch click into the centre of larger Bounds"); // the stale SET above left it off
        cmd.Destroy(C);
        cmd.Destroy(Sw);
        CheckApply("toggle destroy");
        Frame();

        // --- Slider: Qt defaults 0..1, step 0. Fixed Bounds 400 x 40 at (100, 200): Basic padding 6, handle 28 ->
        // position p at x = 100 + 6 + 14 + p * (400 - 12 - 28) = 120 + 360 p.
        cmd.Create(Sl, Protocol.Type.Slider);
        VL.Qt.Styles.BaseStyle.Write(cmd, Sl); // like the Reconciler
        cmd.SetPlace(Sl, 100, 200, 0, 0);
        cmd.Reparent(Sl, 0, 0);
        cmd.Bind(Sl, HL, 0);
        CheckApply("slider create");
        recs = Frame();
        recs.AddRange(Frame());
        var ml = Measured(recs, HL);
        s.Check(ml is { } c && c.F(0) >= StyleMin && c.F(1) >= StyleMin && c.F(1) <= 60, $"slider implicit size horizontal ({S(recs)})");
        ExpectNone(recs, HL, "slider creation");
        cmd.SetBool(Sl, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(Sl, 100, 200, 400, 40);
        cmd.SetF32(Sl, Protocol.Prop.Value, 0.25f);
        CheckApply("slider bounds + value 0.25");
        recs = Frame();
        recs.AddRange(Frame());
        ExpectNone(recs, HL, "slider SET VALUE 0.25");
        static float X(float p) => 100 + SliderInset + p * (400 - 2 * SliderInset);
        const float y = 220;

        Click(X(0.5f), y);
        ExpectF32(Frame(), HL, 0.5f, 0.01f, "slider click at 0.5");
        ExpectNone(Frame(), HL, "slider next frame (sent once)");
        Mouse(TestProtocol.Mouse.Move, X(0.5f), y);
        Mouse(TestProtocol.Mouse.Press, X(0.5f), y);
        Mouse(TestProtocol.Mouse.Move, X(0.6f), y);
        Mouse(TestProtocol.Mouse.Move, X(0.7f), y);
        Mouse(TestProtocol.Mouse.Move, X(0.75f), y);
        ExpectF32(Frame(), HL, 0.75f, 0.01f, "slider drag, several moves in one frame (last value)");
        Mouse(TestProtocol.Mouse.Move, X(0.9f), y);
        ExpectF32(Frame(), HL, 0.9f, 0.01f, "slider drag continues (live)");
        Mouse(TestProtocol.Mouse.Release, X(0.9f), y);
        ExpectNone(Frame(), HL, "slider release at the same position");

        cmd.SetF32(Sl, Protocol.Prop.Value, 0.1f);
        CheckApply("slider set 0.1");
        ExpectNone(Frame(), HL, "slider SET VALUE 0.1");
        Mouse(TestProtocol.Mouse.Move, X(0.5f), y);
        s.Check(NativeTest.vlqt_test_send_wheel(w, X(0.5f), y, 0, 120) == 0, "slider send_wheel");
        ExpectF32(Frame(), HL, 0.2f, 0.001f, "slider wheel up with step 0 (Qt: 0.1)");
        Key(KeyRight, 0, null); // focus from the clicks above
        recs = Frame();
        var keyValues = Values(recs, HL);
        s.Check(keyValues.Count == 1 && keyValues[0].F32 > 0.2f, $"slider arrow key -> one EV_VALUE above 0.2 ({S(recs)})");

        // Range and step: -10..10, step 2.5: drag snaps, wheel steps; SET MAXIMUM below the value clamps without echo.
        cmd.SetF32(Sl, Protocol.Prop.Minimum, -10);
        cmd.SetF32(Sl, Protocol.Prop.Maximum, 10);
        cmd.SetF32(Sl, Protocol.Prop.Step, 2.5f);
        cmd.SetF32(Sl, Protocol.Prop.Value, 0);
        CheckApply("slider range -10..10 step 2.5");
        ExpectNone(Frame(), HL, "slider SET range/step/value");
        Click(X(0.6f), y); // 2.0 -> snaps to 2.5
        ExpectF32(Frame(), HL, 2.5f, 0.001f, "slider click snaps to the step");
        Mouse(TestProtocol.Mouse.Move, X(0.5f), y);
        s.Check(NativeTest.vlqt_test_send_wheel(w, X(0.5f), y, 0, 120) == 0, "slider send_wheel step");
        ExpectF32(Frame(), HL, 5f, 0.001f, "slider wheel up with step 2.5");
        Click(X(0.85f), y); // 7.0 -> 7.5
        ExpectF32(Frame(), HL, 7.5f, 0.001f, "slider click at 7 snaps to 7.5");
        cmd.SetF32(Sl, Protocol.Prop.Maximum, 5);
        CheckApply("slider maximum below the value");
        ExpectNone(Frame(), HL, "slider clamp by SET MAXIMUM");

        // Stale SET between the user change and the poll.
        cmd.SetF32(Sl, Protocol.Prop.Maximum, 10);
        CheckApply("slider maximum 10");
        Frame();
        Click(X(0.25f), y); // -5
        cmd.SetF32(Sl, Protocol.Prop.Value, 10);
        CheckApply("slider stale set");
        ExpectF32(Frame(), HL, -5f, 0.001f, "slider user change + stale SET before the poll");
        ExpectNone(Frame(), HL, "slider stale SET itself");

        // Enabled false: click and wheel change nothing.
        cmd.SetBool(Sl, Protocol.Prop.Enabled, false);
        CheckApply("slider enabled false");
        Click(X(0.75f), y);
        s.Check(NativeTest.vlqt_test_send_wheel(w, X(0.75f), y, 0, 120) == 0, "slider send_wheel disabled");
        ExpectNone(Frame(), HL, "slider click + wheel with Enabled false");
        cmd.SetBool(Sl, Protocol.Prop.Enabled, true);
        CheckApply("slider enabled true");

        // Rebind drops a change not polled.
        Click(X(0.75f), y);
        cmd.Bind(Sl, HL2, 0);
        CheckApply("slider rebind");
        recs = Frame();
        s.Check(Values(recs, HL).Count == 0 && Values(recs, HL2).Count == 0, $"slider rebind drops the unpolled change ({S(recs)})");

        // Vertical: implicit size swaps, top = maximum. Invalid orientation -> error.
        cmd.SetI32(Sl, Protocol.Prop.Orientation, 2);
        cmd.SetBool(Sl, Protocol.Prop.AutoSize, true);
        CheckApply("slider vertical");
        recs = Frame();
        recs.AddRange(Frame());
        ml = Measured(recs, HL2);
        s.Check(ml is { } d && d.F(1) >= StyleMin && d.F(0) >= StyleMin && d.F(0) <= 60, $"slider implicit size vertical ({S(recs)})");
        cmd.SetBool(Sl, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(Sl, 600, 100, 40, 400);
        CheckApply("slider vertical bounds");
        Frame();
        Click(620, 100 + SliderInset + (400 - 2 * SliderInset) * 0.1f); // 10 % from the top = 90 % of -10..10 = 8 -> 7.5 (the value is 5 from the dropped change)
        ExpectF32(Frame(), HL2, 7.5f, 0.001f, "slider vertical click (top = maximum)");
        cmd.SetI32(Sl, Protocol.Prop.Orientation, 3);
        var badRc = Native.Apply(w, cmd.EndFrame());
        cmd.Reset();
        s.Check(badRc != 0, $"slider orientation 3 is rejected ({badRc})");
        cmd.Destroy(Sl);
        CheckApply("slider destroy");
        Frame();

        // --- TextField: commit on Enter / focus loss only if the text changed; SET VALUE no echo, also not on focus loss.
        cmd.Create(T1, Protocol.Type.TextField);
        VL.Qt.Styles.BaseStyle.Write(cmd, T1); // like the Reconciler
        cmd.SetPlace(T1, 100, 300, 0, 0);
        cmd.SetString(T1, Protocol.Prop.Value, "start");
        cmd.SetString(T1, Protocol.Prop.Placeholder, "Name");
        cmd.SetBool(T1, Protocol.Prop.Editable, true);
        cmd.SetBool(T1, Protocol.Prop.Enabled, true);
        cmd.Reparent(T1, 0, 0);
        cmd.Bind(T1, HT, 0);
        cmd.Create(T2, Protocol.Type.TextField);
        VL.Qt.Styles.BaseStyle.Write(cmd, T2); // like the Reconciler
        cmd.SetBool(T2, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(T2, 100, 400, 300, 40);
        cmd.Reparent(T2, 0, 1);
        cmd.Bind(T2, HT2, 0);
        CheckApply("textfield create");
        recs = Frame();
        recs.AddRange(Frame());
        var mt = Measured(recs, HT);
        s.Check(mt is { } e && e.F(0) >= StyleMin && e.F(1) >= StyleMin && e.F(1) <= 60, $"textfield implicit size ({S(recs)})");
        ExpectNone(recs, HT, "textfield SET VALUE on creation");
        cmd.SetBool(T1, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(T1, 100, 300, 300, 40);
        CheckApply("textfield fixed bounds");
        Frame();
        const float t1x = 250, t1y = 320, t2x = 250, t2y = 420;

        Click(t1x, t1y);
        ExpectNone(Frame(), HT, "textfield focus");
        Click(t2x, t2y);
        recs = Frame();
        ExpectNone(recs, HT, "textfield focus loss without an edit");
        ExpectNone(recs, HT2, "textfield 2 focus");
        Click(t1x, t1y);
        Key(KeyReturn, 0, "\r");
        ExpectNone(Frame(), HT, "textfield Enter without an edit");

        Type("hello");
        ExpectNone(Frame(), HT, "textfield typing without Enter");
        Key(KeyReturn, 0, "\r");
        ExpectString(Frame(), HT, "hello", "textfield typed + Enter");
        Key(KeyReturn, 0, "\r");
        ExpectNone(Frame(), HT, "textfield second Enter");
        Type("world");
        Click(t2x, t2y);
        recs = Frame();
        ExpectString(recs, HT, "world", "textfield typed + focus loss");
        ExpectNone(recs, HT2, "textfield 2 gets focus (nothing for it)");

        cmd.SetString(T1, Protocol.Prop.Value, "abc");
        CheckApply("textfield set abc");
        ExpectNone(Frame(), HT, "textfield SET VALUE abc");
        Click(t1x, t1y);
        Key(KeyReturn, 0, "\r");
        Click(t2x, t2y);
        ExpectNone(Frame(), HT, "textfield Enter + focus loss after SET (no echo)");
        Click(t1x, t1y);
        Type("abc");
        Key(KeyReturn, 0, "\r");
        ExpectNone(Frame(), HT, "textfield retyping the same text");

        Type("xyz");
        Key(KeyReturn, 0, "\r");
        cmd.SetString(T1, Protocol.Prop.Value, "abc");
        CheckApply("textfield stale set");
        ExpectString(Frame(), HT, "xyz", "textfield user change + stale SET before the poll");
        ExpectNone(Frame(), HT, "textfield stale SET itself");

        Type("one");
        Key(KeyReturn, 0, "\r");
        Type("two");
        Key(KeyReturn, 0, "\r");
        ExpectString(Frame(), HT, "two", "textfield two commits in one frame (last value)");

        Type("grüße €");
        Key(KeyReturn, 0, "\r");
        ExpectString(Frame(), HT, "grüße €", "textfield non-ASCII text (UTF-8)");

        // SET while typing replaces the edit and becomes the reference: Enter then sends nothing.
        Type("draft");
        cmd.SetString(T1, Protocol.Prop.Value, "server");
        CheckApply("textfield set while typing");
        Key(KeyReturn, 0, "\r");
        ExpectNone(Frame(), HT, "textfield SET while typing, then Enter");

        // Editable false (readOnly): typing changes nothing, Enter sends nothing.
        cmd.SetBool(T1, Protocol.Prop.Editable, false);
        CheckApply("textfield editable false");
        Click(t1x, t1y);
        Type("nope");
        Key(KeyReturn, 0, "\r");
        ExpectNone(Frame(), HT, "textfield typing with Editable false");
        cmd.SetBool(T1, Protocol.Prop.Editable, true);
        cmd.SetBool(T1, Protocol.Prop.Enabled, false);
        CheckApply("textfield enabled false");
        Click(t1x, t1y);
        Type("nope");
        Key(KeyReturn, 0, "\r");
        ExpectNone(Frame(), HT, "textfield typing with Enabled false");
        cmd.SetBool(T1, Protocol.Prop.Enabled, true);
        CheckApply("textfield enabled true");

        // Rebind drops a change not polled.
        Click(t1x, t1y);
        Type("lost");
        Key(KeyReturn, 0, "\r");
        cmd.Bind(T1, HT3, 0);
        CheckApply("textfield rebind");
        recs = Frame();
        s.Check(Values(recs, HT).Count == 0 && Values(recs, HT3).Count == 0, $"textfield rebind drops the unpolled change ({S(recs)})");
        Type("kept");
        Key(KeyReturn, 0, "\r");
        ExpectString(Frame(), HT3, "kept", "textfield new handle gets the next change");

        // Context menu (right click, Qt 6.9+ TextEditingContextMenu from the Basic impl plugin): no QML warning in the
        // log (the loose .qml is trimmed, the plugin's compiled copy must load); Escape closes it, then typing works.
        int logStart = s.Log.Length;
        Mouse(TestProtocol.Mouse.Move, t1x, t1y);
        Mouse(TestProtocol.Mouse.PressRight, t1x, t1y);
        Mouse(TestProtocol.Mouse.ReleaseRight, t1x, t1y);
        s.Pump(3);
        var menuLog = s.Log.Substring(logStart);
        s.Check(menuLog.IndexOf("qml", StringComparison.OrdinalIgnoreCase) < 0 && menuLog.IndexOf("warn", StringComparison.OrdinalIgnoreCase) < 0,
            $"textfield right click -> no QML warning ({menuLog.Trim()})");
        ExpectNone(Frame(), HT3, "textfield right click");
        Key(KeyEscape, 0, null);
        s.Pump(2);
        Click(t1x, t1y);
        Type("after menu");
        Key(KeyReturn, 0, "\r");
        ExpectString(Frame(), HT3, "after menu", "textfield after the context menu");

        cmd.Destroy(T1);
        cmd.Destroy(T2);
        CheckApply("textfield destroy");
        Native.vlqt_window_destroy(w);
        s.Pump(1);
    }

    private const uint C = 1;
    private const uint Sw = 2;
    private const uint Sl = 3;
    private const uint T1 = 4;
    private const uint T2 = 5;
    private const uint HC = 51;
    private const uint HC2 = 52;
    private const uint HS = 53;
    private const uint HL = 54;
    private const uint HL2 = 55;
    private const uint HT = 56;
    private const uint HT2 = 57;
    private const uint HT3 = 58;
}
