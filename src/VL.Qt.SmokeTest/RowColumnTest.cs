using System;
using NUnit.Framework;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using Color = System.Drawing.Color;
using RectangleF = System.Drawing.RectangleF;
using static Pixels;

// Row/Column in QtNative (plan 05e): Qt positioners + childAlignment, spacing, padding.
// Grabs go to <OutDir>/05_rowcolumn*.png. Values are DIP. Children get Bounds (0, 0, w, h) like the
// reconciler sends them (04c: Bounds.Position of layout children is ignored).
[TestFixture, NonParallelizable]
public static class RowColumnTest
{
    // Containers
    private const uint ColA = 1, ColB = 2, RowC = 3, ColD = 4;
    // Children: 10 + container * 10 + index
    private static uint Child(uint container, uint index) => 10 + container * 10 + index;

    private static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    private static bool IsGreen(Color c) => c.G > 200 && c.R < 40 && c.B < 40;
    private static bool IsBlue(Color c) => c.B > 200 && c.R < 40 && c.G < 40;
    private static bool IsYellow(Color c) => c.R > 200 && c.G > 200 && c.B < 40;

    /// <summary>Row and Column layout.</summary>
    [Test, Category("rowcolumn")]
    public static void RowColumn() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt row/column test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        Native.Result Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Container(uint id, ushort type, RectangleF bounds, float spacing, float padding, int align)
        {
            cmd.Create(id, type);
            if (bounds.Width > 0) cmd.SetBool(id, Protocol.PropertyId.AutoSize, false); // explicit size (Auto Size would ignore it)
            if (bounds.Width > 0 || bounds.X != 0 || bounds.Y != 0)
                cmd.SetPlace(id, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (spacing != 0) cmd.SetF32(id, Protocol.PropertyId.Spacing, spacing);
            if (padding != 0) cmd.SetF32(id, Protocol.PropertyId.Padding, padding);
            if (align != 0) cmd.SetI32(id, Protocol.PropertyId.ChildAlignment, align);
            cmd.Reparent(id, 0, 1000);
        }
        void Rect(uint id, uint parent, float width, float height, float r, float g, float b)
        {
            cmd.Create(id, Protocol.ElementType.Rectangle);
            cmd.SetPlace(id, 0, 0, width, height);
            cmd.SetColor(id, Protocol.PropertyId.Color, r, g, b, 1);
            cmd.Reparent(id, parent, 1000);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // A: Column without size (implicit), padding 10, spacing 20, Left.
        Container(ColA, Protocol.ElementType.Column, new RectangleF(20, 20, 0, 0), 20, 10, 0);
        Rect(Child(ColA, 0), ColA, 100, 40, 1, 0, 0);
        Rect(Child(ColA, 1), ColA, 60, 40, 0, 1, 0);
        Rect(Child(ColA, 2), ColA, 140, 40, 0, 0, 1);
        // B: Column 200x300, padding 10, spacing 10, HCenter (0x4). Content width 180.
        Container(ColB, Protocol.ElementType.Column, new RectangleF(220, 20, 200, 300), 10, 10, 0x4);
        Rect(Child(ColB, 0), ColB, 100, 40, 1, 0, 0);
        Rect(Child(ColB, 1), ColB, 60, 40, 0, 1, 0);
        Rect(Child(ColB, 2), ColB, 140, 40, 0, 0, 1);
        // C: Row 300x200, padding 10, spacing 10, Bottom (0x40). Content height 180.
        Container(RowC, Protocol.ElementType.Row, new RectangleF(450, 20, 300, 200), 10, 10, 0x40);
        Rect(Child(RowC, 0), RowC, 40, 50, 1, 0, 0);
        Rect(Child(RowC, 1), RowC, 40, 100, 0, 1, 0);
        Rect(Child(RowC, 2), RowC, 40, 30, 0, 0, 1);
        // D: Column, spacing 0; the middle child is rotated 90 deg around its center (visual only).
        Container(ColD, Protocol.ElementType.Column, new RectangleF(20, 300, 0, 0), 0, 0, 0);
        Rect(Child(ColD, 0), ColD, 100, 40, 1, 0, 0);
        Rect(Child(ColD, 1), ColD, 100, 40, 0, 1, 0);
        Rect(Child(ColD, 2), ColD, 100, 40, 0, 0, 1);
        var rot = Matrix.RotationZ(MathUtil.PiOverTwo); // around the default PIVOT (center, resolved by QtNative)
        cmd.SetI32(Child(ColD, 1), Protocol.PropertyId.Anchor, GeometryExtensions.AlignCenter); // pivot only inside a Column
        cmd.SetMat4(Child(ColD, 1), Protocol.PropertyId.Transform, in rot);
        CheckApply("row/column scene");
        s.Pump(3);

        using (var a = s.Grab(w, "05_rowcolumn.png"))
        {
            // A: red y 30..70, green 90..130, blue 150..190, all at x 30 (padding 10).
            s.Check(IsRed(Px(a, 35, 35)) && IsGreen(Px(a, 35, 95)) && IsBlue(Px(a, 35, 155)),
                $"column stacks 3 children ({Px(a, 35, 35)}, {Px(a, 35, 95)}, {Px(a, 35, 155)})");
            s.Check(IsBg(Px(a, 35, 80)) && IsBg(Px(a, 35, 140)), "column spacing 20 leaves gaps");
            s.Check(IsBg(Px(a, 25, 25)) && IsBg(Px(a, 25, 50)) && IsRed(Px(a, 31, 31)), "column padding 10");
            s.Check(IsBg(Px(a, 95, 110)) && IsGreen(Px(a, 85, 110)), "column child keeps its width (60)");
            // B: HCenter in 180 -> red x 270..370, green 290..350, blue 250..390.
            s.Check(IsRed(Px(a, 275, 50)) && IsBg(Px(a, 265, 50)) && IsRed(Px(a, 365, 50)) && IsBg(Px(a, 375, 50)),
                $"column HCenter red ({Px(a, 265, 50)}, {Px(a, 275, 50)})");
            s.Check(IsGreen(Px(a, 295, 100)) && IsBg(Px(a, 285, 100)) && IsBg(Px(a, 355, 100)),
                $"column HCenter green ({Px(a, 285, 100)}, {Px(a, 295, 100)})");
            s.Check(IsBlue(Px(a, 255, 150)) && IsBg(Px(a, 245, 150)), "column HCenter blue");
            // C: Bottom in 180 -> red y 160..210, green 110..210, blue 180..210; x 460/510/560.
            s.Check(IsRed(Px(a, 480, 205)) && IsBg(Px(a, 480, 150)) && IsGreen(Px(a, 530, 115)) && IsBg(Px(a, 530, 105))
                && IsBlue(Px(a, 580, 185)) && IsBg(Px(a, 580, 175)) && IsBg(Px(a, 580, 215)),
                $"row Bottom ({Px(a, 480, 205)}, {Px(a, 530, 115)}, {Px(a, 580, 185)})");
            s.Check(IsBg(Px(a, 505, 205)) && IsBg(Px(a, 455, 205)), "row spacing/padding on x");
            // D: green rotated around its center -> 40x100 at x 50..90, y 310..410; red/blue keep their slots.
            s.Check(IsBlue(Px(a, 25, 405)) && IsRed(Px(a, 25, 305)), "transform of a child does not move siblings");
            s.Check(IsBg(Px(a, 25, 360)) && IsGreen(Px(a, 70, 330)), $"child transform is visual ({Px(a, 25, 360)}, {Px(a, 70, 330)})");
        }

        // Updates: alignment Right, child size change, spacing/padding change, appended child.
        cmd.Reset();
        cmd.SetI32(ColB, Protocol.PropertyId.ChildAlignment, 0x2);
        cmd.SetPlace(Child(ColB, 1), 0, 0, 20, 40);
        cmd.SetPlace(Child(ColA, 1), 0, 0, 120, 40);
        cmd.SetF32(ColA, Protocol.PropertyId.Spacing, 0);
        cmd.SetF32(RowC, Protocol.PropertyId.Padding, 30);
        Rect(Child(ColA, 3), ColA, 80, 20, 1, 1, 0);
        CheckApply("row/column updates");
        s.Pump(2);
        using (var a = s.Grab(w, "05_rowcolumn_update.png"))
        {
            // B Right: red x 310..410, green (20 wide) 390..410.
            s.Check(IsRed(Px(a, 405, 50)) && IsBg(Px(a, 305, 50)), $"column Right ({Px(a, 305, 50)}, {Px(a, 405, 50)})");
            s.Check(IsGreen(Px(a, 400, 100)) && IsBg(Px(a, 385, 100)), "resized child realigned");
            // A spacing 0: red 30..70, green 70..110 (120 wide), blue 110..150, yellow 150..170.
            s.Check(IsGreen(Px(a, 35, 75)) && IsGreen(Px(a, 145, 100)) && IsBlue(Px(a, 35, 115)) && IsYellow(Px(a, 35, 160))
                && IsBg(Px(a, 35, 175)), $"column spacing 0 + appended child ({Px(a, 35, 75)}, {Px(a, 35, 160)})");
            // C padding 30: red y 140..190, x 480..520.
            s.Check(IsRed(Px(a, 500, 185)) && IsBg(Px(a, 500, 195)) && IsBg(Px(a, 470, 185)), "row padding 30");
        }

        // Errors: wrong alignment flag per type -> BadArgument, wrong value type -> BadArgument, Rectangle property -> UnknownProperty.
        void Expect(Native.Result expected, string what)
        {
            var rc = Apply();
            s.Check(rc == expected, $"{what} -> {expected} ({rc}: {Native.LastError()})");
            cmd.Reset();
        }
        cmd.Reset();
        cmd.SetI32(ColA, Protocol.PropertyId.ChildAlignment, 0x20);
        Expect(Native.Result.BadArgument, "column alignment AlignTop");
        cmd.SetI32(RowC, Protocol.PropertyId.ChildAlignment, 0x1);
        Expect(Native.Result.BadArgument, "row alignment AlignLeft");
        cmd.SetI32(ColA, Protocol.PropertyId.Spacing, 3);
        Expect(Native.Result.BadArgument, "spacing as i32");
        cmd.SetF32(ColA, Protocol.PropertyId.Radius, 1);
        Expect(Native.Result.UnknownProperty, "radius on column");

        Native.vlqt_scene_destroy(w);
    }
}
