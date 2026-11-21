using System;
using NUnit.Framework;
using System.Drawing;
using System.IO;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// Shape in QtNative: open polyline, closed filled polygon with a thick miter stroke, cubic path, trimmed polyline,
// dashed line; pixel checks on stroke, fill, closed join, trim and dash. Then invalid PATH / F32_ARRAY payloads: rejected, no crash.
// Grab goes to <OutDir>/shapes.png. Coordinates are logical pixels relative to the item origin (the Shape item has size 0).
[TestFixture, NonParallelizable]
public static class ShapeTest
{
    private static System.Drawing.Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    private static bool Near(System.Drawing.Color c, int r, int g, int b) => Math.Abs(c.R - r) < 12 && Math.Abs(c.G - g) < 12 && Math.Abs(c.B - b) < 12;
    private static bool IsBg(System.Drawing.Color c) => Near(c, 0x20, 0x20, 0x20);

    private const uint Polyline = 1, Polygon = 2, Cubic = 3, Trimmed = 4, Dashed = 5, Rect = 6;

    /// <summary>Shape elements.</summary>
    [Test, Category("shapes")]
    public static void Shapes() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt shapes test");
        s.Check(w != SceneHandle.Null, "shapes: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        Native.Result Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"shapes: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void NewShape(uint id, float x, float y)
        {
            cmd.Create(id, Protocol.ElementType.Shape);
            cmd.SetPlace(id, x, y, 0, 0);
            cmd.Reparent(id, 0, 1000);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);

        // Open polyline (50,50) - (150,110) - (250,50), red stroke 10; no closing segment, no fill.
        NewShape(Polyline, 50, 50);
        cmd.SetF32Array(Polyline, Protocol.PropertyId.ShapePoints, [0, 0, 100, 60, 200, 0]);
        cmd.SetColor(Polyline, Protocol.PropertyId.ShapeStrokeColor, 1, 0, 0, 1);
        cmd.SetF32(Polyline, Protocol.PropertyId.ShapeStrokeWidth, 10);

        // Closed rectangle 150 x 120 at (400,50): green fill, blue stroke 20 with miter joins.
        NewShape(Polygon, 400, 50);
        cmd.SetF32Array(Polygon, Protocol.PropertyId.ShapePoints, [0, 0, 150, 0, 150, 120, 0, 120]);
        cmd.SetBool(Polygon, Protocol.PropertyId.ShapeClosed, true);
        cmd.SetColor(Polygon, Protocol.PropertyId.ShapeFill, 0, 1, 0, 1);
        cmd.SetColor(Polygon, Protocol.PropertyId.ShapeStrokeColor, 0, 0, 1, 1);
        cmd.SetF32(Polygon, Protocol.PropertyId.ShapeStrokeWidth, 20);
        cmd.SetI32(Polygon, Protocol.PropertyId.ShapeJoin, 0);

        // Cubic S-curve at (50,250): (0,100) -> (200,100) with controls (0,0), (200,200); yellow stroke 8.
        NewShape(Cubic, 50, 250);
        cmd.SetPath(Cubic, Protocol.PropertyId.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Cubic], [0, 100, 0, 0, 200, 200, 200, 100]);
        cmd.SetColor(Cubic, Protocol.PropertyId.ShapeStrokeColor, 1, 1, 0, 1);
        cmd.SetF32(Cubic, Protocol.PropertyId.ShapeStrokeWidth, 8);

        // Trimmed line (400,300) - (600,300), white stroke 12, only 0.25 .. 0.75 is drawn (x 450 .. 550).
        NewShape(Trimmed, 400, 250);
        cmd.SetF32Array(Trimmed, Protocol.PropertyId.ShapePoints, [0, 50, 200, 50]);
        cmd.SetColor(Trimmed, Protocol.PropertyId.ShapeStrokeColor, 1, 1, 1, 1);
        cmd.SetF32(Trimmed, Protocol.PropertyId.ShapeStrokeWidth, 12);
        cmd.SetF32(Trimmed, Protocol.PropertyId.ShapeTrimStart, 0.25f);
        cmd.SetF32(Trimmed, Protocol.PropertyId.ShapeTrimEnd, 0.75f);

        // Dashed line (50,450) - (350,450), magenta stroke 10, dash 2 on / 2 off in stroke widths = 20 px (x 50-70 on, 70-90 off, ...).
        NewShape(Dashed, 50, 450);
        cmd.SetF32Array(Dashed, Protocol.PropertyId.ShapePoints, [0, 0, 300, 0]);
        cmd.SetColor(Dashed, Protocol.PropertyId.ShapeStrokeColor, 1, 0, 1, 1);
        cmd.SetF32(Dashed, Protocol.PropertyId.ShapeStrokeWidth, 10);
        cmd.SetF32Array(Dashed, Protocol.PropertyId.ShapeDash, [2, 2]);

        // Rectangle for the "property of another type" error case.
        cmd.Create(Rect, Protocol.ElementType.Rectangle);
        cmd.SetPlace(Rect, 600, 450, 100, 100);
        cmd.Reparent(Rect, 0, 1000);

        CheckApply("shapes scene");
        s.Pump(3);
        using (var a = s.Grab(w, "shapes.png"))
        {
            // Open polyline: stroke on the first segment, nothing above it or on the closing line.
            s.Check(Near(Px(a, 100, 80), 255, 0, 0) && Near(Px(a, 200, 80), 255, 0, 0) && Near(Px(a, 150, 110), 255, 0, 0),
                $"shapes: polyline stroke red ({Px(a, 100, 80)}, {Px(a, 200, 80)}, {Px(a, 150, 110)})");
            s.Check(IsBg(Px(a, 100, 50)) && IsBg(Px(a, 150, 50)) && IsBg(Px(a, 150, 85)),
                $"shapes: open polyline has no closing segment and no fill ({Px(a, 100, 50)}, {Px(a, 150, 50)}, {Px(a, 150, 85)})");

            // Closed polygon: fill, stroke on the edge, miter corner at the start point (0,0) (a notch would show background).
            s.Check(Near(Px(a, 475, 110), 0, 255, 0) && Near(Px(a, 430, 80), 0, 255, 0),
                $"shapes: polygon fill green ({Px(a, 475, 110)}, {Px(a, 430, 80)})");
            s.Check(Near(Px(a, 475, 50), 0, 0, 255) && Near(Px(a, 400, 110), 0, 0, 255) && Near(Px(a, 550, 110), 0, 0, 255) && Near(Px(a, 475, 170), 0, 0, 255),
                $"shapes: polygon stroke blue on all four edges ({Px(a, 475, 50)}, {Px(a, 400, 110)}, {Px(a, 550, 110)}, {Px(a, 475, 170)})");
            s.Check(Near(Px(a, 392, 42), 0, 0, 255) && Near(Px(a, 408, 42), 0, 0, 255) && Near(Px(a, 392, 58), 0, 0, 255),
                $"shapes: closed join at the start corner without notch ({Px(a, 392, 42)}, {Px(a, 408, 42)}, {Px(a, 392, 58)})");
            s.Check(Near(Px(a, 558, 42), 0, 0, 255) && Near(Px(a, 558, 178), 0, 0, 255) && Near(Px(a, 392, 178), 0, 0, 255) && IsBg(Px(a, 385, 35)),
                $"shapes: miter corners ({Px(a, 558, 42)}, {Px(a, 558, 178)}, {Px(a, 392, 178)}, {Px(a, 385, 35)})");

            // Cubic: on the curve at t = 0.5 and t = 0.25, not on the straight chord.
            s.Check(Near(Px(a, 150, 350), 255, 255, 0) && Near(Px(a, 81, 322), 255, 255, 0),
                $"shapes: cubic stroke yellow ({Px(a, 150, 350)}, {Px(a, 81, 322)})");
            s.Check(IsBg(Px(a, 81, 350)) && IsBg(Px(a, 100, 380)),
                $"shapes: cubic is a curve, not the chord ({Px(a, 81, 350)}, {Px(a, 100, 380)})");

            // Trim 0.25 .. 0.75: start empty, middle filled, end empty.
            s.Check(IsBg(Px(a, 425, 300)) && Near(Px(a, 500, 300), 255, 255, 255) && IsBg(Px(a, 575, 300)),
                $"shapes: trim start empty, middle drawn, end empty ({Px(a, 425, 300)}, {Px(a, 500, 300)}, {Px(a, 575, 300)})");
            s.Check(Near(Px(a, 460, 300), 255, 255, 255) && Near(Px(a, 540, 300), 255, 255, 255) && IsBg(Px(a, 440, 300)) && IsBg(Px(a, 560, 300)),
                $"shapes: trim edges at 450 and 550 ({Px(a, 460, 300)}, {Px(a, 540, 300)}, {Px(a, 440, 300)}, {Px(a, 560, 300)})");

            // Dash 2/2 stroke widths: stroke at 50-70, gap at 70-90, stroke at 90-110.
            s.Check(Near(Px(a, 60, 450), 255, 0, 255) && IsBg(Px(a, 80, 450)) && Near(Px(a, 100, 450), 255, 0, 255) && IsBg(Px(a, 120, 450)),
                $"shapes: dash stroke / gap / stroke / gap ({Px(a, 60, 450)}, {Px(a, 80, 450)}, {Px(a, 100, 450)}, {Px(a, 120, 450)})");
        }

        // Invalid payloads are rejected with an error code; the scene is untouched and keeps rendering.
        void Reject(string what, Native.Result expected, params Native.Result[] alsoOk)
        {
            var rc = Apply();
            var ok = rc == expected || Array.IndexOf(alsoOk, rc) >= 0;
            s.Check(ok, $"shapes: {what} -> {rc} (expected {expected}) \"{Native.LastError()}\"");
        }

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.PropertyId.ShapePath, [7], [0, 0]);
        Reject("PATH with unknown verb 7", Native.Result.BadArgument);

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.PropertyId.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Line], [0, 0, 10, 10, 20, 20]);
        Reject("PATH with 3 points for MOVE + LINE", Native.Result.BadArgument);

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.PropertyId.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Cubic], [0, 0, 10, 10]);
        Reject("PATH with CUBIC and too few points", Native.Result.BadArgument);

        cmd.Reset();
        cmd.SetF32Array(Polyline, Protocol.PropertyId.ShapePath, [0, 0, 10, 10]);
        Reject("F32_ARRAY on the PATH property", Native.Result.BadArgument);

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.PropertyId.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Line], [0, 0, 10, 10]);
        var good = cmd.EndFrame().ToArray();
        // SET payload after the header: id u32, prop u16, vt u16, then verbCount u32, pointCount u32.
        var badLength = (byte[])good.Clone();
        badLength[Protocol.HeaderSize + 12]++;
        var rcLen = Native.Apply(w, badLength);
        s.Check(rcLen == Native.Result.BadLength, $"shapes: PATH with a point count that does not match the payload length -> {rcLen} (expected -2) \"{Native.LastError()}\"");

        cmd.Reset();
        cmd.SetF32Array(Polyline, Protocol.PropertyId.ShapePoints, [0, 0, 10, 10]);
        var badArray = cmd.EndFrame().ToArray();
        badArray[Protocol.HeaderSize + 8] = 9; // the count follows id, prop and value type
        rcLen = Native.Apply(w, badArray);
        s.Check(rcLen == Native.Result.BadLength, $"shapes: F32_ARRAY with a count that does not match the payload length -> {rcLen} (expected -2) \"{Native.LastError()}\"");

        cmd.Reset();
        cmd.SetF32Array(Rect, Protocol.PropertyId.ShapePoints, [0, 0, 10, 10]);
        Reject("valid F32_ARRAY on a Rectangle (no Shape property)", Native.Result.UnknownProperty, Native.Result.BadArgument);

        cmd.Reset();
        cmd.SetPath(Rect, Protocol.PropertyId.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Line], [0, 0, 10, 10]);
        Reject("valid PATH on a Rectangle (no Shape property)", Native.Result.UnknownProperty, Native.Result.BadArgument);

        // Scene still renders and the shape still shows the old geometry.
        cmd.Reset();
        cmd.SetF32(Polyline, Protocol.PropertyId.ShapeStrokeWidth, 10);
        CheckApply("apply after rejected payloads");
        s.Pump(3);
        using (var a = s.Grab(w, "shapes_after_errors.png"))
            s.Check(Near(Px(a, 100, 80), 255, 0, 0) && Near(Px(a, 500, 300), 255, 255, 255) && Near(Px(a, 475, 110), 0, 255, 0),
                $"shapes: scene unchanged after rejected payloads ({Px(a, 100, 80)}, {Px(a, 500, 300)}, {Px(a, 475, 110)})");

        Native.vlqt_scene_destroy(w);
    }
}
