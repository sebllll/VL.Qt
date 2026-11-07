using System;
using System.Drawing;
using System.IO;
using VL.Qt;
using Color = System.Drawing.Color;

// SIZE and AUTO_SIZE (#83, #469). Size 0 stays 0 (SIZE always sets width/height explicitly). Containers
// (Group, Row, Column, ShaderEffect) have AUTO_SIZE, default on: the SIZE is ignored, the implicit size
// applies. Off: the SIZE counts, also 0.
static class SizeResetTest
{
    const uint RowId = 1, A = 2, B = 3, GroupId = 4, G1 = 5, RectId = 6, EffectId = 7, EffectSrc = 8;
    const uint HRow = 21, HGroup = 22, HRect = 23, HEffect = 24;

    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;
    static bool IsBg(Color c) => Math.Abs(c.R - 0x20) < 8 && Math.Abs(c.G - 0x20) < 8 && Math.Abs(c.B - 0x20) < 8;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt size / auto size test");
        check(w != IntPtr.Zero, "83: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"83: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        void Expect(int expected, string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == expected, $"83: {what} -> {expected} ({rc}: {Native.LastError()})");
            cmd.Reset();
        }
        MeasureTest.Rec? Bounds(uint handle)
        {
            int n = Native.Poll(w, buf);
            foreach (var r in MeasureTest.Parse(buf.AsSpan(0, Math.Max(n, 0)), check))
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
        pump(3);
        var recs = Bounds(HRow);
        check(Is(recs, 100, 100, 110, 50), $"83: Auto Size (default) row: SCENE_BOUNDS = implicit size ({S(recs)})");
        recs = Bounds(HGroup);
        check(recs == null || Is(recs, 400, 100, 40, 50), $"83: group: implicit size = extent of the children ({S(recs)})");
        using (var a = grab(w, Path.Combine(outDir, "83_autosize.png")))
        {
            // VCenter in 50: red y 110..140 at x 100..140, green y 100..150 at x 150..210.
            check(IsRed(Px(a, 120, 112)) && IsRed(Px(a, 120, 138)) && IsBg(Px(a, 120, 105)) && IsBg(Px(a, 120, 145)),
                $"83: row VCenter uses implicit height ({Px(a, 120, 105)}, {Px(a, 120, 112)}, {Px(a, 120, 145)})");
            check(IsGreen(Px(a, 180, 102)) && IsGreen(Px(a, 180, 148)), "83: row green fills implicit height");
            check(IsRed(Px(a, 415, 125)) && IsBg(Px(a, 405, 125)), $"83: group child at (410, 120) ({Px(a, 405, 125)}, {Px(a, 415, 125)})");
            check(IsBg(Px(a, 600, 100)) && IsBg(Px(a, 601, 101)), "83: rectangle with size 0 is not drawn");
        }

        // Auto Size on: the size is ignored.
        RowSize(300, 200);
        CheckApply("row size 300 x 200 (Auto Size on)");
        pump(2);
        recs = Bounds(HRow);
        check(recs == null || Is(recs, 100, 100, 110, 50), $"83: Auto Size on ignores size ({S(recs)})");

        // Auto Size off: size 300 x 200 counts. VCenter in 200 -> red y 185..215.
        cmd.SetBool(RowId, Protocol.Prop.AutoSize, false);
        CheckApply("row Auto Size off");
        pump(2);
        recs = Bounds(HRow);
        check(Is(recs, 100, 100, 300, 200), $"83: Auto Size off re-applies the size (100, 100, 300, 200) ({S(recs)})");
        using (var a = grab(w, Path.Combine(outDir, "83_autosize_off.png")))
            check(IsRed(Px(a, 120, 190)) && IsBg(Px(a, 120, 150)) && IsBg(Px(a, 120, 222)), "83: explicit size centers children in 200");

        // Auto Size off + size 0: stays 0 (no jump to the implicit size).
        RowSize(0, 0);
        CheckApply("row size 0 (Auto Size off)");
        pump(2);
        recs = Bounds(HRow);
        check(Is(recs, 100, 100, 0, 0), $"83: Auto Size off + size 0 -> 0 x 0 ({S(recs)})");

        // 0 -> 300 x 200 -> 0 without a jump.
        RowSize(300, 200);
        CheckApply("row 300 x 200 again");
        pump(2);
        check(Is(Bounds(HRow), 100, 100, 300, 200), "83: 0 -> 300 x 200");
        RowSize(0, 0);
        CheckApply("row 0 again");
        pump(2);
        recs = Bounds(HRow);
        check(Is(recs, 100, 100, 0, 0), $"83: 300 x 200 -> 0 stays 0 ({S(recs)})");

        // Auto Size on again: implicit size.
        cmd.SetBool(RowId, Protocol.Prop.AutoSize, true);
        CheckApply("row Auto Size on again");
        pump(2);
        recs = Bounds(HRow);
        check(Is(recs, 100, 100, 110, 50), $"83: Auto Size on again -> implicit (100, 100, 110, 50) ({S(recs)})");
        using (var a = grab(w, Path.Combine(outDir, "83_autosize_on_again.png")))
            check(IsRed(Px(a, 120, 112)) && IsBg(Px(a, 120, 105)) && IsBg(Px(a, 120, 150)), "83: row aligned in implicit height again");

        // Rectangle: 0 -> 50 x 50 (visible) -> 0 (gone).
        cmd.SetPlace(RectId, 600, 100, 50, 50);
        CheckApply("rectangle 50 x 50");
        pump(2);
        using (var a = grab(w, Path.Combine(outDir, "83_rect_size.png")))
            check(IsRed(Px(a, 620, 120)), "83: rectangle 50 x 50 is drawn");
        check(Is(Bounds(HRect), 600, 100, 50, 50), "83: rectangle SCENE_BOUNDS 50 x 50");
        cmd.SetPlace(RectId, 600, 100, 0, 0);
        CheckApply("rectangle 0 again");
        pump(2);
        using (var a = grab(w, Path.Combine(outDir, "83_rect_zero.png")))
            check(IsBg(Px(a, 620, 120)), "83: rectangle back to size 0 is not drawn");
        recs = Bounds(HRect);
        check(Is(recs, 600, 100, 0, 0), $"83: rectangle SCENE_BOUNDS 0 x 0 ({S(recs)})");

        // ShaderEffect: implicit size = size of the input.
        recs = null;
        cmd.SetPlace(EffectId, 500, 300, 0, 0);
        CheckApply("effect size again");
        pump(2);
        cmd.SetBool(EffectId, Protocol.Prop.AutoSize, false);
        CheckApply("effect Auto Size off");
        pump(2);
        recs = Bounds(HEffect);
        check(Is(recs, 500, 300, 0, 0), $"83: ShaderEffect Auto Size off + size 0 -> 0 x 0 ({S(recs)})");
        cmd.SetBool(EffectId, Protocol.Prop.AutoSize, true);
        CheckApply("effect Auto Size on");
        pump(2);
        recs = Bounds(HEffect);
        check(Is(recs, 500, 300, 40, 30), $"83: ShaderEffect Auto Size on -> input size 40 x 30 ({S(recs)})");

        // Errors: AUTO_SIZE is valid for containers only (-6), value type bool (-7).
        cmd.SetBool(RectId, Protocol.Prop.AutoSize, false);
        Expect(-6, "AUTO_SIZE on Rectangle");
        cmd.SetF32(RowId, Protocol.Prop.AutoSize, 1);
        Expect(-7, "AUTO_SIZE as f32");

        Native.vlqt_window_destroy(w);
        pump(2);
    }
}
