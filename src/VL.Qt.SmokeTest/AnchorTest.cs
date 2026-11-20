using System;
using NUnit.Framework;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using static Pixels;

// Anchor against the actual item size (#88, #469): like VL.Skia a point v of the element lands at (v - anchor) * M in its
// parent (outside Row/Column); in Row/Column the layout places it and the anchor is only the pivot of Transform.
// Row/Group with Auto Size on use the center of their implicit size; a Group with Auto Size off keeps its children at its
// origin and places only its frame (Size) by the anchor (#475), so Transform pivots around the origin. The
// anchor follows a size change without a matrix resend. Nodes → Reconciler → CommandWriter → real QtNative. Grabs go to
// <OutDir>/88_*.png (pixel checks in DIP); the analytic expectations use the Skia formula (v - anchor) * M.
// Stride RotationZ(+90) turns clockwise on screen (y down): (x, y) → (-y, x); the left child goes up, the right child down.
[TestFixture, NonParallelizable]
public static class AnchorTest
{
    private static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    private static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;

    private static IDisposable NoWarning(string _) => throw new InvalidOperationException("unexpected node warning");

    private static readonly Color4 Red = new(1, 0, 0, 1), Green = new(0, 1, 0, 1);
    private static readonly Matrix Rot90 = Matrix.RotationZ(MathUtil.PiOverTwo);

    /// <summary>Anchors.</summary>
    [Test, Category("anchor")]
    public static void Anchor() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt anchor test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var red = new RectangleNode(NoWarning);
        var green = new RectangleNode(NoWarning);
        var row = new RowNode(NoWarning);
        var group = new GroupNode(NoWarning);

        // Child rectangle (Row/Group child: Anchor TopLeft, position via Transform; inside a Row only the size counts).
        Element Rect(RectangleNode n, Color4 fill, float x = 0, float y = 0, float w = 1, float h = 1)
            => n.Update(new Vector2(w, h), Matrix.Translation(x, y, 0), RectangleAnchor.TopLeft, fill, 0, new Color4(0, 0, 0, 1), 0, 1, false, false,
                null, null, out _, out _);

        // Rotated by 90 degrees around the point (cx, cy) in units, the anchor at its center: Rot90 · T(c).
        static Matrix RotAround(float cx, float cy) => Rot90 * Matrix.Translation(cx, cy, 0);

        // Row (implicit size, 2 children): anchor Center + Rot90 · T(2, 1.5) rotates around (200, 150) DIP.
        Element Row(float redWidth = 1) => row.Update(Spread.Create<IElement?>(Rect(red, Red, w: redWidth), Rect(green, Green)),
            new Vector2(0, 0), true, RotAround(2, 1.5f), RectangleAnchor.Center, 0, 0, VerticalAlignment.Top, 1, false, false,
            null, null, out _, out _);

        Element Group(Vector2 size, bool autoSize, RectangleAnchor anchor, Matrix m, float cx, float cy)
            => group.Update(Spread.Create<IElement?>(Rect(red, Red, cx, cy), Rect(green, Green, cx + 1, cy)),
                size, autoSize, m, anchor, 1, false, false,
                null, null, null, out _, out _);

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

        // Unrotated, both cases are red 100..200 / green 200..300 (x) at y 100..200, implicit size 200 x 100 at (100, 100).
        // 90 deg around the center (200, 150): red x 150..250 / y 50..150, green x 150..250 / y 150..250.
        void CheckCenter200x150(string name, string what)
        {
            using var a = s.Grab(w, name);
            s.Check(IsRed(Px(a, 200, 60)) && IsRed(Px(a, 155, 145)) && IsGreen(Px(a, 200, 240)) && IsGreen(Px(a, 245, 155)),
                $"{what}: rotated around (200, 150) ({Px(a, 200, 60)}, {Px(a, 200, 240)})");
            s.Check(IsBg(Px(a, 120, 150)) && IsBg(Px(a, 280, 150)) && IsBg(Px(a, 200, 40)) && IsBg(Px(a, 200, 260)),
                $"{what}: nothing outside the rotated box ({Px(a, 120, 150)}, {Px(a, 280, 150)}, {Px(a, 200, 40)})");
        }

        // 1) Row, Auto Size on, anchor Center: the anchor is the center of the implicit size (200 x 100), placed at (200, 150).
        Frame(Row(), "row auto size", clear: true);
        CheckCenter200x150("88_row_autosize.png", "row auto size on");

        // 2) The anchor follows a size change of the implicit size (red 2 units wide → 300 x 100, anchor (150, 50) in
        //    item coordinates, still at (200, 150)): red x 150..250 / y 0..200, green x 150..250 / y 200..300.
        //    Only the child size is sent, no new matrix.
        Frame(Row(redWidth: 2), "row grows");
        using (var a = s.Grab(w, "88_row_grown.png"))
        {
            s.Check(IsRed(Px(a, 200, 10)) && IsRed(Px(a, 200, 190)) && IsGreen(Px(a, 200, 210)) && IsGreen(Px(a, 200, 290)),
                $"row grown: anchor follows the implicit size ({Px(a, 200, 10)}, {Px(a, 200, 290)})");
            s.Check(IsBg(Px(a, 140, 100)) && IsBg(Px(a, 260, 100)) && IsBg(Px(a, 200, 310)),
                $"row grown: nothing outside ({Px(a, 140, 100)}, {Px(a, 260, 100)}, {Px(a, 200, 310)})");
        }

        // 3) Group, Auto Size on, children at (0, 0) and (1, 0): implicit size 200 x 100 as in 1).
        Frame(Group(new Vector2(0, 0), true, RectangleAnchor.Center, RotAround(2, 1.5f), 0, 0), "group auto size");
        CheckCenter200x150("88_group_autosize.png", "group auto size on");

        // 4) Group, Auto Size off, size (8, 6), children around the group origin (red -100..0, green 0..100, y -50..50).
        //    The origin (= anchor point of the frame, #475) is placed at (400, 300): red x 350..450 / y 200..300,
        //    green y 300..400.
        Frame(Group(new Vector2(8, 6), false, RectangleAnchor.Center, RotAround(4, 3), -1, -0.5f), "group auto size off");
        using (var a = s.Grab(w, "88_group_size_center.png"))
        {
            s.Check(IsRed(Px(a, 400, 210)) && IsRed(Px(a, 355, 295)) && IsGreen(Px(a, 400, 390)) && IsGreen(Px(a, 445, 305)),
                $"group auto size off, anchor Center: rotated around (400, 300) ({Px(a, 400, 210)}, {Px(a, 400, 390)})");
            s.Check(IsBg(Px(a, 320, 300)) && IsBg(Px(a, 480, 300)),
                $"group auto size off: unrotated positions empty ({Px(a, 320, 300)}, {Px(a, 480, 300)})");
        }

        // 5) Same group with Anchor TopLeft: Auto Size off moves only the frame (#475), the children stay at the origin,
        //    the picture is the same as in 4).
        Frame(Group(new Vector2(8, 6), false, RectangleAnchor.TopLeft, RotAround(4, 3), -1, -0.5f), "group anchor top left");
        using (var a = s.Grab(w, "88_group_size_topleft.png"))
            s.Check(IsRed(Px(a, 400, 210)) && IsGreen(Px(a, 400, 390)) && IsBg(Px(a, 320, 300)) && IsBg(Px(a, 480, 300)),
                $"group auto size off, anchor TopLeft: children unmoved, rotated around the origin ({Px(a, 400, 210)}, {Px(a, 400, 390)})");

        // 6) Back to Center: only ANCHOR changes, the group is rotated around (400, 300) again.
        Frame(Group(new Vector2(8, 6), false, RectangleAnchor.Center, RotAround(4, 3), -1, -0.5f), "group anchor back to center");
        using (var a = s.Grab(w, "88_group_size_center2.png"))
            s.Check(IsRed(Px(a, 400, 210)) && IsGreen(Px(a, 400, 390)),
                $"group anchor back to Center ({Px(a, 400, 210)}, {Px(a, 400, 390)})");

        // 7) Placement through the anchor, Identity transform, a 2 x 1 units (200 x 100 DIP) rectangle at the root.
        Frame(Rect(red, Red, 0, 0, 2, 1), "identity top left", clear: true);
        using (var a = s.Grab(w, "88_identity_topleft.png"))
            s.Check(IsRed(Px(a, 10, 10)) && IsRed(Px(a, 190, 90)) && IsBg(Px(a, 210, 50)) && IsBg(Px(a, 100, 110)),
                $"Identity + Anchor TopLeft: rectangle in the top left corner ({Px(a, 10, 10)}, {Px(a, 190, 90)}, {Px(a, 210, 50)}, {Px(a, 100, 110)})");

        Frame(red.Update(new Vector2(2, 1), Matrix.Identity, RectangleAnchor.Center, Red, 0, new Color4(0, 0, 0, 1), 0, 1, false, false,
            null, null, out _, out _), "identity center");
        using (var a = s.Grab(w, "88_identity_center.png"))
            s.Check(IsRed(Px(a, 10, 10)) && IsRed(Px(a, 90, 40)) && IsBg(Px(a, 110, 25)) && IsBg(Px(a, 50, 60)),
                $"Identity + Anchor Center: center at the origin, a quarter visible ({Px(a, 10, 10)}, {Px(a, 90, 40)}, {Px(a, 110, 25)}, {Px(a, 50, 60)})");

        // 8) Translate + Rotate + Scale, expectation = Skia (v - anchor) · M, computed independently of QtNative:
        //    M = Scale(1.5) · RotationZ(90°) · Translate(4, 2.5 units), the rectangle is 2 x 1 units.
        //    p = ((v - anchor) · 1.5) rotated (x, y) → (-y, x) + (400, 250).
        foreach (var anchor in new[] { RectangleAnchor.Center, RectangleAnchor.TopLeft })
        {
            var m = Matrix.Scaling(1.5f) * Rot90 * Matrix.Translation(4, 2.5f, 0);
            Frame(red.Update(new Vector2(2, 1), m, anchor, Red, 0, new Color4(0, 0, 0, 1), 0, 1, false, false,
                null, null, out _, out _), $"srt {anchor}", clear: true);
            Vector2 A() => anchor == RectangleAnchor.Center ? new Vector2(100, 50) : Vector2.Zero;
            Vector2 Map(Vector2 v)
            {
                var d = (v - A()) * 1.5f;
                return new Vector2(-d.Y + 400, d.X + 250);
            }
            var corners = new[] { new Vector2(0, 0), new Vector2(200, 0), new Vector2(200, 100), new Vector2(0, 100) };
            var centroid = Map(new Vector2(100, 50));
            using var g = s.Grab(w, $"88_srt_{anchor}.png");
            bool inside = true, outside = true;
            string what = "";
            foreach (var c in corners)
            {
                var p = Map(c);
                var pin = p + (centroid - p) * 0.15f;
                var pout = p - (centroid - p) * 0.1f;
                var ci = Px(g, (int)MathF.Round(pin.X), (int)MathF.Round(pin.Y));
                var co = Px(g, (int)MathF.Round(pout.X), (int)MathF.Round(pout.Y));
                inside &= IsRed(ci);
                outside &= IsBg(co);
                what += $" [{p.X:F0},{p.Y:F0}: in {ci.R:X2}{ci.G:X2}{ci.B:X2}, out {co.R:X2}{co.G:X2}{co.B:X2}]";
            }
            s.Check(inside && outside, $"Skia semantics, Anchor {anchor}, Translate + Rotate + Scale: corners at (v - anchor) · M{what}");
        }

        // 9) Row child: the anchor is only the pivot. Row at (100, 100) (Anchor TopLeft), red 1 x 1 units, green 2 x 1 units
        //    rotated by 90° with Anchor Center → cells red x 100..200, green x 200..400 / y 100..200 (center (300, 150)),
        //    green rotated around that center: x 250..350 / y 50..250, the layout cell is not left by the anchor.
        {
            var greenRot = green.Update(new Vector2(2, 1), Rot90, RectangleAnchor.Center, Green, 0, new Color4(0, 0, 0, 1), 0, 1, false, false,
                null, null, out _, out _);
            Frame(row.Update(Spread.Create<IElement?>(Rect(red, Red), greenRot), new Vector2(0, 0), true, Matrix.Translation(1, 1, 0),
                RectangleAnchor.TopLeft, 0, 0, VerticalAlignment.Top, 1, false, false, null, null, out _, out _), "row child anchor", clear: true);
            using var a = s.Grab(w, "88_row_child_anchor.png");
            s.Check(IsRed(Px(a, 150, 150)) && IsGreen(Px(a, 300, 60)) && IsGreen(Px(a, 300, 240)) && IsBg(Px(a, 220, 150)) && IsBg(Px(a, 380, 150)),
                $"Row child: anchor Center = pivot only, rotated in its cell ({Px(a, 150, 150)}, {Px(a, 300, 60)}, {Px(a, 300, 240)}, {Px(a, 220, 150)}, {Px(a, 380, 150)})");
        }

        foreach (var n in new IDisposable[] { red, green, row, group })
            n.Dispose();
        cmd.Reset();
        rec.Clear();

        // 10) ANCHOR errors (raw): one horizontal and one vertical Qt::AlignmentFlag, i32 only. Valid on every type.
        void Expect(int expected, string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == expected, $"{what} -> {expected} ({rc}: {Native.LastError()})");
            cmd.Reset();
        }
        const uint Raw = 900;
        cmd.Reset();
        cmd.Create(Raw, Protocol.Type.Rectangle);
        cmd.SetI32(Raw, Protocol.Prop.Anchor, 0x42);
        Expect(0, "ANCHOR BottomRight on a Rectangle");
        cmd.SetI32(Raw, Protocol.Prop.Anchor, 0x03);
        Expect(-7, "ANCHOR Left|Right without vertical flag");
        cmd.SetI32(Raw, Protocol.Prop.Anchor, 0x84 | 0x100);
        Expect(-7, "ANCHOR with a flag outside left/right/hcenter/top/bottom/vcenter");
        cmd.SetF32(Raw, Protocol.Prop.Anchor, 0.5f);
        Expect(-7, "ANCHOR as f32");
        cmd.SetF32(Raw, Protocol.Prop.Size, 1);
        Expect(-7, "SIZE as f32 (vec2 only)");
        cmd.Destroy(Raw);
        Expect(0, "destroy");

        Native.vlqt_window_destroy(w);
        s.Pump(2);
    }
}