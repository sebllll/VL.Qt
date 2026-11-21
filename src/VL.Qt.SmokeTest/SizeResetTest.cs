using System;
using NUnit.Framework;
using System.Drawing;
using System.IO;
using VL.Qt.Wire;
using VL.Qt;
using Color = System.Drawing.Color;
using static Pixels;

// SIZE and AUTO_SIZE. Size 0 stays 0 (SIZE always sets width/height explicitly). Containers
// (Group, Row, Column, ShaderEffect) have AUTO_SIZE, default on: the SIZE is ignored, the implicit size
// applies. Off: the SIZE counts, also 0.
[TestFixture, NonParallelizable]
public static class SizeResetTest
{
    private const uint RowId = 1, A = 2, B = 3, GroupId = 4, G1 = 5, RectId = 6, EffectId = 7, EffectSrc = 8;
    private const uint HRow = 21, HGroup = 22, HRect = 23, HEffect = 24;

    private static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    private static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;

    /// <summary>Size reset.</summary>
    [Test, Category("sizereset")]
    public static void SizeReset() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt size / auto size test");
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
        void Expect(Native.Result expected, string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == expected, $"{what} -> {expected} ({rc}: {Native.LastError()})");
            cmd.Reset();
        }
        MeasureTest.Rec? Bounds(uint handle)
        {
            int n = NativeCalls.Poll(w, buf);
            foreach (var r in MeasureTest.Parse(buf.AsSpan(0, Math.Max(n, 0)), s.Check))
                if (r.Event == Protocol.Ev.SceneBounds && r.Handle == handle) return r;
            return null;
        }
        static string S(MeasureTest.Rec? r) => r is { } x ? string.Join(", ", x.V) : "none";
        static bool Is(MeasureTest.Rec? r, float x, float y, float bw, float bh) =>
            r is { } v && v.V[0] == x && v.V[1] == y && v.V[2] == bw && v.V[3] == bh;
        void Rect(uint id, uint parent, float x, float y, float rw, float rh, float r, float g)
        {
            cmd.Create(id, Protocol.Type.Rectangle);
            cmd.SetPlace(id, x, y, rw, rh);
            cmd.SetColor(id, Protocol.Prop.Color, r, g, 0, 1);
            cmd.Reparent(id, parent, 1000);
        }
        void RowSize(float rw, float rh) => cmd.SetPlace(RowId, 100, 100, rw, rh);

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // Row at (100, 100), size 0, Auto Size default (on), spacing 10, VCenter (0x80): implicit 110 x 50; red 40x30, green 60x50.
        cmd.Create(RowId, Protocol.Type.Row);
        RowSize(0, 0);
        cmd.SetF32(RowId, Protocol.Prop.Spacing, 10);
        cmd.SetI32(RowId, Protocol.Prop.ChildAlignment, 0x80);
        cmd.Reparent(RowId, 0, 1000);
        cmd.Bind(RowId, HRow, 0);
        Rect(A, RowId, 0, 0, 40, 30, 1, 0);
        Rect(B, RowId, 0, 0, 60, 50, 0, 1);
        // Group at (400, 100), size 0, Auto Size on, one child at (10, 20) 30x30 -> implicit 40 x 50.
        cmd.Create(GroupId, Protocol.Type.Item);
        cmd.SetPlace(GroupId, 400, 100, 0, 0);
        cmd.Reparent(GroupId, 0, 1001);
        cmd.Bind(GroupId, HGroup, 0);
        Rect(G1, GroupId, 10, 20, 30, 30, 1, 0);
        // Rectangle at (600, 100), size 0, bound: is not drawn (0 stays 0).
        Rect(RectId, 0, 600, 100, 0, 0, 1, 0);
        cmd.Bind(RectId, HRect, 0);
        // ShaderEffect at (500, 300), Auto Size on: implicit size = size of the input (40 x 30).
        cmd.Create(EffectId, Protocol.Type.ShaderEffect);
        cmd.SetPlace(EffectId, 500, 300, 0, 0);
        cmd.Reparent(EffectId, 0, 1002);
        cmd.Bind(EffectId, HEffect, 0);
        Rect(EffectSrc, EffectId, 0, 0, 40, 30, 0, 1);
        cmd.SetRef(EffectId, Protocol.Prop.Source, EffectSrc);
        CheckApply("row, group, rectangle, effect with size 0");
        s.Pump(3);
        var recs = Bounds(HRow);
        s.Check(Is(recs, 100, 100, 110, 50), $"Auto Size (default) row: SCENE_BOUNDS = implicit size ({S(recs)})");
        recs = Bounds(HGroup);
        s.Check(recs == null || Is(recs, 400, 100, 40, 50), $"group: implicit size = extent of the children ({S(recs)})");
        using (var a = s.Grab(w, "83_autosize.png"))
        {
            // VCenter in 50: red y 110..140 at x 100..140, green y 100..150 at x 150..210.
            s.Check(IsRed(Px(a, 120, 112)) && IsRed(Px(a, 120, 138)) && IsBg(Px(a, 120, 105)) && IsBg(Px(a, 120, 145)),
                $"row VCenter uses implicit height ({Px(a, 120, 105)}, {Px(a, 120, 112)}, {Px(a, 120, 145)})");
            s.Check(IsGreen(Px(a, 180, 102)) && IsGreen(Px(a, 180, 148)), "row green fills implicit height");
            s.Check(IsRed(Px(a, 415, 125)) && IsBg(Px(a, 405, 125)), $"group child at (410, 120) ({Px(a, 405, 125)}, {Px(a, 415, 125)})");
            s.Check(IsBg(Px(a, 600, 100)) && IsBg(Px(a, 601, 101)), "rectangle with size 0 is not drawn");
        }

        // Auto Size on: the size is ignored.
        RowSize(300, 200);
        CheckApply("row size 300 x 200 (Auto Size on)");
        s.Pump(2);
        recs = Bounds(HRow);
        s.Check(recs == null || Is(recs, 100, 100, 110, 50), $"Auto Size on ignores size ({S(recs)})");

        // Auto Size off: size 300 x 200 counts. VCenter in 200 -> red y 185..215.
        cmd.SetBool(RowId, Protocol.Prop.AutoSize, false);
        CheckApply("row Auto Size off");
        s.Pump(2);
        recs = Bounds(HRow);
        s.Check(Is(recs, 100, 100, 300, 200), $"Auto Size off re-applies the size (100, 100, 300, 200) ({S(recs)})");
        using (var a = s.Grab(w, "83_autosize_off.png"))
            s.Check(IsRed(Px(a, 120, 190)) && IsBg(Px(a, 120, 150)) && IsBg(Px(a, 120, 222)), "explicit size centers children in 200");

        // Auto Size off + size 0: stays 0 (no jump to the implicit size).
        RowSize(0, 0);
        CheckApply("row size 0 (Auto Size off)");
        s.Pump(2);
        recs = Bounds(HRow);
        s.Check(Is(recs, 100, 100, 0, 0), $"Auto Size off + size 0 -> 0 x 0 ({S(recs)})");

        // 0 -> 300 x 200 -> 0 without a jump.
        RowSize(300, 200);
        CheckApply("row 300 x 200 again");
        s.Pump(2);
        s.Check(Is(Bounds(HRow), 100, 100, 300, 200), "0 -> 300 x 200");
        RowSize(0, 0);
        CheckApply("row 0 again");
        s.Pump(2);
        recs = Bounds(HRow);
        s.Check(Is(recs, 100, 100, 0, 0), $"x 200 -> 0 stays 0 ({S(recs)})");

        // Auto Size on again: implicit size.
        cmd.SetBool(RowId, Protocol.Prop.AutoSize, true);
        CheckApply("row Auto Size on again");
        s.Pump(2);
        recs = Bounds(HRow);
        s.Check(Is(recs, 100, 100, 110, 50), $"Auto Size on again -> implicit (100, 100, 110, 50) ({S(recs)})");
        using (var a = s.Grab(w, "83_autosize_on_again.png"))
            s.Check(IsRed(Px(a, 120, 112)) && IsBg(Px(a, 120, 105)) && IsBg(Px(a, 120, 150)), "row aligned in implicit height again");

        // Rectangle: 0 -> 50 x 50 (visible) -> 0 (gone).
        cmd.SetPlace(RectId, 600, 100, 50, 50);
        CheckApply("rectangle 50 x 50");
        s.Pump(2);
        using (var a = s.Grab(w, "83_rect_size.png"))
            s.Check(IsRed(Px(a, 620, 120)), "rectangle 50 x 50 is drawn");
        s.Check(Is(Bounds(HRect), 600, 100, 50, 50), "rectangle SCENE_BOUNDS 50 x 50");
        cmd.SetPlace(RectId, 600, 100, 0, 0);
        CheckApply("rectangle 0 again");
        s.Pump(2);
        using (var a = s.Grab(w, "83_rect_zero.png"))
            s.Check(IsBg(Px(a, 620, 120)), "rectangle back to size 0 is not drawn");
        recs = Bounds(HRect);
        s.Check(Is(recs, 600, 100, 0, 0), $"rectangle SCENE_BOUNDS 0 x 0 ({S(recs)})");

        // ShaderEffect: implicit size = size of the input.
        recs = null;
        cmd.SetPlace(EffectId, 500, 300, 0, 0);
        CheckApply("effect size again");
        s.Pump(2);
        cmd.SetBool(EffectId, Protocol.Prop.AutoSize, false);
        CheckApply("effect Auto Size off");
        s.Pump(2);
        recs = Bounds(HEffect);
        s.Check(Is(recs, 500, 300, 0, 0), $"ShaderEffect Auto Size off + size 0 -> 0 x 0 ({S(recs)})");
        cmd.SetBool(EffectId, Protocol.Prop.AutoSize, true);
        CheckApply("effect Auto Size on");
        s.Pump(2);
        recs = Bounds(HEffect);
        s.Check(Is(recs, 500, 300, 40, 30), $"ShaderEffect Auto Size on -> input size 40 x 30 ({S(recs)})");

        // Errors: AUTO_SIZE is valid for containers only (-6), value type bool (-7).
        cmd.SetBool(RectId, Protocol.Prop.AutoSize, false);
        Expect(Native.Result.UnknownProperty, "AUTO_SIZE on Rectangle");
        cmd.SetF32(RowId, Protocol.Prop.AutoSize, 1);
        Expect(Native.Result.BadArgument, "AUTO_SIZE as f32");

        Native.vlqt_window_destroy(w);
        s.Pump(2);
    }
}
