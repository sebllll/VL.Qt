using System;
using NUnit.Framework;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using static Pixels;

// Group/Row/Column nodes (07d) end to end: nodes → Reconciler → CommandWriter → real QtNative.
// Grabs go to <OutDir>/07d_*.png. Node inputs are units (DIPTopLeft, 1 unit = 100 DIP), pixel checks are DIP.
[TestFixture, NonParallelizable]
public static class ContainerNodeTest
{
    private static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    private static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;
    private static bool IsBlue(Color c) => c.B > 200 && c.R < 40 && c.G < 40;

    private static IDisposable NoWarning(string _) => throw new InvalidOperationException("unexpected node warning");

    private static readonly Color4 Red = new(1, 0, 0, 1), Green = new(0, 1, 0, 1), Blue = new(0, 0, 1, 1);

    /// <summary>Container nodes.</summary>
    [Test, Category("containernode")]
    public static void ContainerNode() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt container node test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry());
        var red = new RectangleNode(NoWarning);
        var green = new RectangleNode(NoWarning);
        var blue = new RectangleNode(NoWarning);
        var column = new ColumnNode(NoWarning);
        var row = new RowNode(NoWarning);
        var group = new GroupNode(NoWarning);

        Element Rect(RectangleNode n, Color4 fill, float w = 1, float h = 1, float x = 0, float y = 0)
            => (Element)n.Update(new Vector2(w, h), Matrix.Translation(x, y, 0), RectangleAnchor.TopLeft, fill, 0, new Color4(0, 0, 0, 1), 0, 1, false, false,
                null, null, out _, out _);

        void Frame(Element root, string what, bool clear = false)
        {
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(root, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"apply {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            s.Pump(3);
        }

        // 1) Column node with 3 Rectangle nodes (1 x 1 units each), implicit size at 0,0, spacing 0.1, Left:
        //    red 0..100, green 110..210, blue 220..320 DIP (y), x 0..100.
        var e = column.Update(Spread.Create<IElement?>(Rect(red, Red), null, Rect(green, Green), Rect(blue, Blue)),
            default, true, Matrix.Identity, RectangleAnchor.TopLeft, 0.1f, 0, HorizontalAlignment.Left, 1, false, false,
            null, null, out _, out _);
        Frame((Element)e, "column of 3 rectangle nodes", clear: true);
        using (var a = s.Grab(w, "07d_column.png"))
        {
            s.Check(IsRed(Px(a, 3, 3)) && IsRed(Px(a, 97, 97)) && IsBg(Px(a, 50, 105)) && IsBg(Px(a, 103, 50)),
                $"column red at (0,0)..(100,100) DIP ({Px(a, 3, 3)}, {Px(a, 97, 97)}, {Px(a, 50, 105)}, {Px(a, 103, 50)})");
            s.Check(IsGreen(Px(a, 50, 113)) && IsGreen(Px(a, 97, 207)) && IsBg(Px(a, 50, 215)),
                $"column green at y 110..210 DIP ({Px(a, 50, 113)}, {Px(a, 97, 207)})");
            s.Check(IsBlue(Px(a, 50, 223)) && IsBlue(Px(a, 97, 317)) && IsBg(Px(a, 50, 325)),
                $"column blue at y 220..320 DIP ({Px(a, 50, 223)}, {Px(a, 97, 317)})");
        }

        // 2) Same column, Center, different widths (red 2, green 1, blue 0.5 units): implicit width = 200 DIP,
        //    green x 50..150, blue x 75..125. Unchanged children → same Rectangle descriptions, only the column changes.
        e = column.Update(Spread.Create<IElement?>(Rect(red, Red, w: 2), Rect(green, Green), Rect(blue, Blue, w: 0.5f)),
            default, true, Matrix.Identity, RectangleAnchor.TopLeft, 0.1f, 0, HorizontalAlignment.Center, 1, false, false,
            null, null, out _, out _);
        Frame((Element)e, "column centered");
        using (var a = s.Grab(w, "07d_column_center.png"))
        {
            s.Check(IsRed(Px(a, 3, 50)) && IsRed(Px(a, 197, 50)) && IsBg(Px(a, 203, 50)), $"center: red 0..200 DIP ({Px(a, 197, 50)})");
            s.Check(IsBg(Px(a, 47, 160)) && IsGreen(Px(a, 53, 160)) && IsGreen(Px(a, 147, 160)) && IsBg(Px(a, 153, 160)),
                $"center: green x 50..150 DIP ({Px(a, 47, 160)}, {Px(a, 53, 160)})");
            s.Check(IsBg(Px(a, 72, 270)) && IsBlue(Px(a, 78, 270)) && IsBlue(Px(a, 122, 270)) && IsBg(Px(a, 128, 270)),
                $"center: blue x 75..125 DIP ({Px(a, 72, 270)}, {Px(a, 78, 270)})");
        }

        // 3) Row at (1, 1) units, size 0 (= implicit size), padding 0.1, spacing 0.2, Bottom; heights 1, 0.5, 0.25 (inner height 100 DIP),
        //    red x 110..210, green 230..330 at y 160..210, blue 350..450 at y 185..210. The Rectangle nodes have no position
        //    (Transform identity), the row places them.
        e = row.Update(Spread.Create<IElement?>(Rect(red, Red), Rect(green, Green, h: 0.5f), Rect(blue, Blue, h: 0.25f)),
            new Vector2(0, 0), true, Matrix.Translation(1, 1, 0), RectangleAnchor.TopLeft, 0.2f, 0.1f, VerticalAlignment.Bottom, 1, false, false,
            null, null, out _, out _);
        Frame((Element)e, "row bottom");
        using (var a = s.Grab(w, "07d_row.png"))
        {
            s.Check(IsRed(Px(a, 113, 113)) && IsRed(Px(a, 207, 207)) && IsBg(Px(a, 105, 150)) && IsBg(Px(a, 215, 150)),
                $"row: red (110,110)..(210,210) DIP ({Px(a, 113, 113)}, {Px(a, 105, 150)})");
            s.Check(IsBg(Px(a, 280, 155)) && IsGreen(Px(a, 280, 165)) && IsGreen(Px(a, 280, 207)),
                $"row: green bottom-aligned y 160..210 DIP ({Px(a, 280, 155)}, {Px(a, 280, 165)})");
            s.Check(IsBg(Px(a, 400, 180)) && IsBlue(Px(a, 400, 190)) && IsBg(Px(a, 400, 215)),
                $"row: blue bottom-aligned y 185..210 DIP ({Px(a, 400, 180)}, {Px(a, 400, 190)})");
        }

        // 4) Group at (2, 1) units: children keep their position (red at 0,0, green at 1,1) → red (200,100), green (300,200).
        e = group.Update(Spread.Create<IElement?>(Rect(red, Red), null, Rect(green, Green, x: 1, y: 1)),
            new Vector2(0, 0), true, Matrix.Translation(2, 1, 0), RectangleAnchor.TopLeft, 1, false, false,
            null, null, null, out _, out _);
        Frame((Element)e, "group");
        using (var a = s.Grab(w, "07d_group.png"))
        {
            s.Check(IsBg(Px(a, 197, 103)) && IsRed(Px(a, 203, 103)) && IsRed(Px(a, 297, 197)) && IsBg(Px(a, 150, 50)),
                $"group: red at (200,100) DIP ({Px(a, 197, 103)}, {Px(a, 203, 103)})");
            s.Check(IsGreen(Px(a, 303, 203)) && IsGreen(Px(a, 397, 297)) && IsBg(Px(a, 403, 250)),
                $"group: green at (300,200) DIP, positions kept ({Px(a, 303, 203)})");
        }

        foreach (var n in new IDisposable[] { red, green, blue, column, row, group })
            n.Dispose();
        cmd.Reset();
        rec.Clear();
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
    }
}
