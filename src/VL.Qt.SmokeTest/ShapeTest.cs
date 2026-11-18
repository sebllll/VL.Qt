using System;
using System.Drawing;
using System.IO;
using VL.Qt;

// Shape in QtNative (#154): open polyline, closed filled polygon with a thick miter stroke, cubic path, trimmed polyline,
// dashed line; pixel checks on stroke, fill, closed join, trim and dash. Then invalid PATH / F32_ARRAY payloads: rejected, no crash.
// Grab goes to <outDir>/shapes.png. Coordinates are logical pixels relative to the item origin (the Shape item has size 0).
static class ShapeTest
{
    static System.Drawing.Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    static bool Near(System.Drawing.Color c, int r, int g, int b) => Math.Abs(c.R - r) < 12 && Math.Abs(c.G - g) < 12 && Math.Abs(c.B - b) < 12;
    static bool IsBg(System.Drawing.Color c) => Near(c, 0x20, 0x20, 0x20);

    const uint Polyline = 1, Polygon = 2, Cubic = 3, Trimmed = 4, Dashed = 5, Rect = 6;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt shapes test");
        check(w != IntPtr.Zero, "shapes: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        int Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            check(rc == 0, $"shapes: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void NewShape(uint id, float x, float y)
        {
            cmd.Create(id, Protocol.Type.Shape);
            cmd.SetPlace(id, x, y, 0, 0);
            cmd.Reparent(id, 0, 1000);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);

        // Open polyline (50,50) - (150,110) - (250,50), red stroke 10; no closing segment, no fill.
        NewShape(Polyline, 50, 50);
        cmd.SetF32Array(Polyline, Protocol.Prop.ShapePoints, [0, 0, 100, 60, 200, 0]);
        cmd.SetColor(Polyline, Protocol.Prop.ShapeStrokeColor, 1, 0, 0, 1);
        cmd.SetF32(Polyline, Protocol.Prop.ShapeStrokeWidth, 10);

        // Closed rectangle 150 x 120 at (400,50): green fill, blue stroke 20 with miter joins.
        NewShape(Polygon, 400, 50);
        cmd.SetF32Array(Polygon, Protocol.Prop.ShapePoints, [0, 0, 150, 0, 150, 120, 0, 120]);
        cmd.SetBool(Polygon, Protocol.Prop.ShapeClosed, true);
        cmd.SetColor(Polygon, Protocol.Prop.ShapeFill, 0, 1, 0, 1);
        cmd.SetColor(Polygon, Protocol.Prop.ShapeStrokeColor, 0, 0, 1, 1);
        cmd.SetF32(Polygon, Protocol.Prop.ShapeStrokeWidth, 20);
        cmd.SetI32(Polygon, Protocol.Prop.ShapeJoin, 0);

        // Cubic S-curve at (50,250): (0,100) -> (200,100) with controls (0,0), (200,200); yellow stroke 8.
        NewShape(Cubic, 50, 250);
        cmd.SetPath(Cubic, Protocol.Prop.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Cubic], [0, 100, 0, 0, 200, 200, 200, 100]);
        cmd.SetColor(Cubic, Protocol.Prop.ShapeStrokeColor, 1, 1, 0, 1);
        cmd.SetF32(Cubic, Protocol.Prop.ShapeStrokeWidth, 8);

        // Trimmed line (400,300) - (600,300), white stroke 12, only 0.25 .. 0.75 is drawn (x 450 .. 550).
        NewShape(Trimmed, 400, 250);
        cmd.SetF32Array(Trimmed, Protocol.Prop.ShapePoints, [0, 50, 200, 50]);
        cmd.SetColor(Trimmed, Protocol.Prop.ShapeStrokeColor, 1, 1, 1, 1);
        cmd.SetF32(Trimmed, Protocol.Prop.ShapeStrokeWidth, 12);
        cmd.SetF32(Trimmed, Protocol.Prop.ShapeTrimStart, 0.25f);
        cmd.SetF32(Trimmed, Protocol.Prop.ShapeTrimEnd, 0.75f);

        // Dashed line (50,450) - (350,450), magenta stroke 10, dash 2 on / 2 off in stroke widths = 20 px (x 50-70 on, 70-90 off, ...).
        NewShape(Dashed, 50, 450);
        cmd.SetF32Array(Dashed, Protocol.Prop.ShapePoints, [0, 0, 300, 0]);
        cmd.SetColor(Dashed, Protocol.Prop.ShapeStrokeColor, 1, 0, 1, 1);
        cmd.SetF32(Dashed, Protocol.Prop.ShapeStrokeWidth, 10);
        cmd.SetF32Array(Dashed, Protocol.Prop.ShapeDash, [2, 2]);

        // Rectangle for the "property of another type" error case.
        cmd.Create(Rect, Protocol.Type.Rectangle);
        cmd.SetPlace(Rect, 600, 450, 100, 100);
        cmd.Reparent(Rect, 0, 1000);

        CheckApply("shapes scene");
        pump(3);
        using (var a = grab(w, Path.Combine(outDir, "shapes.png")))
        {
            // Open polyline: stroke on the first segment, nothing above it or on the closing line.
            check(Near(Px(a, 100, 80), 255, 0, 0) && Near(Px(a, 200, 80), 255, 0, 0) && Near(Px(a, 150, 110), 255, 0, 0),
                $"shapes: polyline stroke red ({Px(a, 100, 80)}, {Px(a, 200, 80)}, {Px(a, 150, 110)})");
            check(IsBg(Px(a, 100, 50)) && IsBg(Px(a, 150, 50)) && IsBg(Px(a, 150, 85)),
                $"shapes: open polyline has no closing segment and no fill ({Px(a, 100, 50)}, {Px(a, 150, 50)}, {Px(a, 150, 85)})");

            // Closed polygon: fill, stroke on the edge, miter corner at the start point (0,0) (a notch would show background).
            check(Near(Px(a, 475, 110), 0, 255, 0) && Near(Px(a, 430, 80), 0, 255, 0),
                $"shapes: polygon fill green ({Px(a, 475, 110)}, {Px(a, 430, 80)})");
            check(Near(Px(a, 475, 50), 0, 0, 255) && Near(Px(a, 400, 110), 0, 0, 255) && Near(Px(a, 550, 110), 0, 0, 255) && Near(Px(a, 475, 170), 0, 0, 255),
                $"shapes: polygon stroke blue on all four edges ({Px(a, 475, 50)}, {Px(a, 400, 110)}, {Px(a, 550, 110)}, {Px(a, 475, 170)})");
            check(Near(Px(a, 392, 42), 0, 0, 255) && Near(Px(a, 408, 42), 0, 0, 255) && Near(Px(a, 392, 58), 0, 0, 255),
                $"shapes: closed join at the start corner without notch ({Px(a, 392, 42)}, {Px(a, 408, 42)}, {Px(a, 392, 58)})");
            check(Near(Px(a, 558, 42), 0, 0, 255) && Near(Px(a, 558, 178), 0, 0, 255) && Near(Px(a, 392, 178), 0, 0, 255) && IsBg(Px(a, 385, 35)),
                $"shapes: miter corners ({Px(a, 558, 42)}, {Px(a, 558, 178)}, {Px(a, 392, 178)}, {Px(a, 385, 35)})");

            // Cubic: on the curve at t = 0.5 and t = 0.25, not on the straight chord.
            check(Near(Px(a, 150, 350), 255, 255, 0) && Near(Px(a, 81, 322), 255, 255, 0),
                $"shapes: cubic stroke yellow ({Px(a, 150, 350)}, {Px(a, 81, 322)})");
            check(IsBg(Px(a, 81, 350)) && IsBg(Px(a, 100, 380)),
                $"shapes: cubic is a curve, not the chord ({Px(a, 81, 350)}, {Px(a, 100, 380)})");

            // Trim 0.25 .. 0.75: start empty, middle filled, end empty.
            check(IsBg(Px(a, 425, 300)) && Near(Px(a, 500, 300), 255, 255, 255) && IsBg(Px(a, 575, 300)),
                $"shapes: trim start empty, middle drawn, end empty ({Px(a, 425, 300)}, {Px(a, 500, 300)}, {Px(a, 575, 300)})");
            check(Near(Px(a, 460, 300), 255, 255, 255) && Near(Px(a, 540, 300), 255, 255, 255) && IsBg(Px(a, 440, 300)) && IsBg(Px(a, 560, 300)),
                $"shapes: trim edges at 450 and 550 ({Px(a, 460, 300)}, {Px(a, 540, 300)}, {Px(a, 440, 300)}, {Px(a, 560, 300)})");

            // Dash 2/2 stroke widths: stroke at 50-70, gap at 70-90, stroke at 90-110.
            check(Near(Px(a, 60, 450), 255, 0, 255) && IsBg(Px(a, 80, 450)) && Near(Px(a, 100, 450), 255, 0, 255) && IsBg(Px(a, 120, 450)),
                $"shapes: dash stroke / gap / stroke / gap ({Px(a, 60, 450)}, {Px(a, 80, 450)}, {Px(a, 100, 450)}, {Px(a, 120, 450)})");
        }

        // Invalid payloads are rejected with an error code; the scene is untouched and keeps rendering.
        void Reject(string what, int expected, params int[] alsoOk)
        {
            var rc = Apply();
            var ok = rc == expected || Array.IndexOf(alsoOk, rc) >= 0;
            check(ok, $"shapes: {what} -> {rc} (expected {expected}) \"{Native.LastError()}\"");
        }

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.Prop.ShapePath, [7], [0, 0]);
        Reject("PATH with unknown verb 7", -7);

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.Prop.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Line], [0, 0, 10, 10, 20, 20]);
        Reject("PATH with 3 points for MOVE + LINE", -7);

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.Prop.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Cubic], [0, 0, 10, 10]);
        Reject("PATH with CUBIC and too few points", -7);

        cmd.Reset();
        cmd.SetF32Array(Polyline, Protocol.Prop.ShapePath, [0, 0, 10, 10]);
        Reject("F32_ARRAY on the PATH property", -7);

        cmd.Reset();
        cmd.SetPath(Polyline, Protocol.Prop.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Line], [0, 0, 10, 10]);
        var good = cmd.EndFrame().ToArray();
        // SET payload: id u32, prop u16, vt u16, then verbCount u32 (offset 12 with the 4-byte header), pointCount u32 (offset 16).
        var badLength = (byte[])good.Clone();
        badLength[16]++;
        var rcLen = Native.Apply(w, badLength);
        check(rcLen == -2, $"shapes: PATH with a point count that does not match the payload length -> {rcLen} (expected -2) \"{Native.LastError()}\"");

        cmd.Reset();
        cmd.SetF32Array(Polyline, Protocol.Prop.ShapePoints, [0, 0, 10, 10]);
        var badArray = cmd.EndFrame().ToArray();
        badArray[12] = 9;
        rcLen = Native.Apply(w, badArray);
        check(rcLen == -2, $"shapes: F32_ARRAY with a count that does not match the payload length -> {rcLen} (expected -2) \"{Native.LastError()}\"");

        cmd.Reset();
        cmd.SetF32Array(Rect, Protocol.Prop.ShapePoints, [0, 0, 10, 10]);
        Reject("valid F32_ARRAY on a Rectangle (no Shape property)", -6, -7);

        cmd.Reset();
        cmd.SetPath(Rect, Protocol.Prop.ShapePath, [Protocol.Verb.Move, Protocol.Verb.Line], [0, 0, 10, 10]);
        Reject("valid PATH on a Rectangle (no Shape property)", -6, -7);

        // Scene still renders and the shape still shows the old geometry.
        cmd.Reset();
        cmd.SetF32(Polyline, Protocol.Prop.ShapeStrokeWidth, 10);
        CheckApply("apply after rejected payloads");
        pump(3);
        using (var a = grab(w, Path.Combine(outDir, "shapes_after_errors.png")))
            check(Near(Px(a, 100, 80), 255, 0, 0) && Near(Px(a, 500, 300), 255, 255, 255) && Near(Px(a, 475, 110), 0, 255, 0),
                $"shapes: scene unchanged after rejected payloads ({Px(a, 100, 80)}, {Px(a, 500, 300)}, {Px(a, 475, 110)})");

        Native.vlqt_window_destroy(w);
    }
}
