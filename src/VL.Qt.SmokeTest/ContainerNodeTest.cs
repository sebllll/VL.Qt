using System;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;

// Group/Row/Column nodes (07d) end to end: nodes → Reconciler → CommandWriter → real QtNative.
// Grabs go to <outDir>/07d_*.png. Node inputs are units (DIPTopLeft, 1 unit = 100 DIP), pixel checks are DIP.
static class ContainerNodeTest
{
    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;
    static bool IsBlue(Color c) => c.B > 200 && c.R < 40 && c.G < 40;
    static bool IsBg(Color c) => Math.Abs(c.R - 0x20) < 8 && Math.Abs(c.G - 0x20) < 8 && Math.Abs(c.B - 0x20) < 8;

    static IDisposable NoWarning(string _) => throw new InvalidOperationException("unexpected node warning");

    static readonly Color4 Red = new(1, 0, 0, 1), Green = new(0, 1, 0, 1), Blue = new(0, 0, 1, 1);

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt container node test");
        check(w != IntPtr.Zero, "07d: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var red = new RectangleNode(NoWarning);
        var green = new RectangleNode(NoWarning);
        var blue = new RectangleNode(NoWarning);
        var column = new ColumnNode(NoWarning);
        var row = new RowNode(NoWarning);
        var group = new GroupNode(NoWarning);

        Element Rect(RectangleNode n, Color4 fill, float w = 1, float h = 1, float x = 0, float y = 0)
            => n.Update(new Vector2(w, h), Matrix.Translation(x, y, 0), RectangleAnchor.TopLeft, fill, 0, new Color4(0, 0, 0, 1), 0, 1, false, false,
                null, null, out _, out _);

        void Frame(Element root, string what, bool clear = false)
        {
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(root, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                check(rc == 0, $"07d: apply {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            pump(3);
        }

        // 1) Column node with 3 Rectangle nodes (1 x 1 units each), implicit size at 0,0, spacing 0.1, Left:
        //    red 0..100, green 110..210, blue 220..320 DIP (y), x 0..100.
        var e = column.Update(Spread.Create<IElement?>(Rect(red, Red), null, Rect(green, Green), Rect(blue, Blue)),
            default, true, Matrix.Identity, RectangleAnchor.TopLeft, 0.1f, 0, HorizontalAlignment.Left, 1, false, false,
            null, null, out _, out _);
        Frame(e, "column of 3 rectangle nodes", clear: true);
        using (var a = grab(w, Path.Combine(outDir, "07d_column.png")))
        {
            check(IsRed(Px(a, 3, 3)) && IsRed(Px(a, 97, 97)) && IsBg(Px(a, 50, 105)) && IsBg(Px(a, 103, 50)),
                $"07d: column red at (0,0)..(100,100) DIP ({Px(a, 3, 3)}, {Px(a, 97, 97)}, {Px(a, 50, 105)}, {Px(a, 103, 50)})");
            check(IsGreen(Px(a, 50, 113)) && IsGreen(Px(a, 97, 207)) && IsBg(Px(a, 50, 215)),
                $"07d: column green at y 110..210 DIP ({Px(a, 50, 113)}, {Px(a, 97, 207)})");
            check(IsBlue(Px(a, 50, 223)) && IsBlue(Px(a, 97, 317)) && IsBg(Px(a, 50, 325)),
                $"07d: column blue at y 220..320 DIP ({Px(a, 50, 223)}, {Px(a, 97, 317)})");
        }

        // 2) Same column, Center, different widths (red 2, green 1, blue 0.5 units): implicit width = 200 DIP,
        //    green x 50..150, blue x 75..125. Unchanged children → same Rectangle descriptions, only the column changes.
        e = column.Update(Spread.Create<IElement?>(Rect(red, Red, w: 2), Rect(green, Green), Rect(blue, Blue, w: 0.5f)),
            default, true, Matrix.Identity, RectangleAnchor.TopLeft, 0.1f, 0, HorizontalAlignment.Center, 1, false, false,
            null, null, out _, out _);
        Frame(e, "column centered");
        using (var a = grab(w, Path.Combine(outDir, "07d_column_center.png")))
        {
            check(IsRed(Px(a, 3, 50)) && IsRed(Px(a, 197, 50)) && IsBg(Px(a, 203, 50)), $"07d: center: red 0..200 DIP ({Px(a, 197, 50)})");
            check(IsBg(Px(a, 47, 160)) && IsGreen(Px(a, 53, 160)) && IsGreen(Px(a, 147, 160)) && IsBg(Px(a, 153, 160)),
                $"07d: center: green x 50..150 DIP ({Px(a, 47, 160)}, {Px(a, 53, 160)})");
            check(IsBg(Px(a, 72, 270)) && IsBlue(Px(a, 78, 270)) && IsBlue(Px(a, 122, 270)) && IsBg(Px(a, 128, 270)),
                $"07d: center: blue x 75..125 DIP ({Px(a, 72, 270)}, {Px(a, 78, 270)})");
        }

        // 3) Row at (1, 1) units, size 0 (= implicit size, #83), padding 0.1, spacing 0.2, Bottom; heights 1, 0.5, 0.25 (inner height 100 DIP),
        //    red x 110..210, green 230..330 at y 160..210, blue 350..450 at y 185..210. The Rectangle nodes have no position
        //    (Transform identity), the row places them.
        e = row.Update(Spread.Create<IElement?>(Rect(red, Red), Rect(green, Green, h: 0.5f), Rect(blue, Blue, h: 0.25f)),
            new Vector2(0, 0), true, Matrix.Translation(1, 1, 0), RectangleAnchor.TopLeft, 0.2f, 0.1f, VerticalAlignment.Bottom, 1, false, false,
            null, null, out _, out _);
        Frame(e, "row bottom");
        using (var a = grab(w, Path.Combine(outDir, "07d_row.png")))
        {
            check(IsRed(Px(a, 113, 113)) && IsRed(Px(a, 207, 207)) && IsBg(Px(a, 105, 150)) && IsBg(Px(a, 215, 150)),
                $"07d: row: red (110,110)..(210,210) DIP ({Px(a, 113, 113)}, {Px(a, 105, 150)})");
            check(IsBg(Px(a, 280, 155)) && IsGreen(Px(a, 280, 165)) && IsGreen(Px(a, 280, 207)),
                $"07d: row: green bottom-aligned y 160..210 DIP ({Px(a, 280, 155)}, {Px(a, 280, 165)})");
            check(IsBg(Px(a, 400, 180)) && IsBlue(Px(a, 400, 190)) && IsBg(Px(a, 400, 215)),
                $"07d: row: blue bottom-aligned y 185..210 DIP ({Px(a, 400, 180)}, {Px(a, 400, 190)})");
        }

        // 4) Group at (2, 1) units: children keep their position (red at 0,0, green at 1,1) → red (200,100), green (300,200).
        e = group.Update(Spread.Create<IElement?>(Rect(red, Red), null, Rect(green, Green, x: 1, y: 1)),
            new Vector2(0, 0), true, Matrix.Translation(2, 1, 0), RectangleAnchor.TopLeft, 1, false, false,
            null, null, null, out _, out _);
        Frame(e, "group");
        using (var a = grab(w, Path.Combine(outDir, "07d_group.png")))
        {
            check(IsBg(Px(a, 197, 103)) && IsRed(Px(a, 203, 103)) && IsRed(Px(a, 297, 197)) && IsBg(Px(a, 150, 50)),
                $"07d: group: red at (200,100) DIP ({Px(a, 197, 103)}, {Px(a, 203, 103)})");
            check(IsGreen(Px(a, 303, 203)) && IsGreen(Px(a, 397, 297)) && IsBg(Px(a, 403, 250)),
                $"07d: group: green at (300,200) DIP, positions kept ({Px(a, 303, 203)})");
        }

        foreach (var n in new IDisposable[] { red, green, blue, column, row, group })
            n.Dispose();
        cmd.Reset();
        rec.Clear();
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_destroy(w);
        pump(2);
    }
}
