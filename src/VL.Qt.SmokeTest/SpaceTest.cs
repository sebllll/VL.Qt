using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using QtRect = VL.Qt.Elements.Rectangle;
using static Pixels;

// Space: one scene, four spaces, through WindowRenderer (SceneCore → SET_SPACE + SpaceMapping lengths → real QtNative).
// A red rectangle of 1 x 1 unit at (0, 0) and a blue one (0.5 x 0.5 at (1, 0)) as the reference. Expectations in DIP
// (logical pixels), per space, derived here from the Space description alone:
//   DIPTopLeft    origin top left, 100 DIP per unit            DIP      origin = centre, 100 DIP per unit
//   Normalized    origin = centre, height -1 … 1 (H / 2 DIP)   PixelTopLeft origin top left, 100 device pixels per unit
// Window 800 x 600, then resized to 400 x 300: origin and the Normalized scale follow. Per space and size: the pixels of
// the grab (<OutDir>/143_space_*.png), the Scene Bounds of both rectangles in units (round trip through QtNative), the
// position of a synthetic click in units (Point node, local to the rectangle). At the end a resync (invalid command before a
// Space change) must restore origin and scale. The device pixel ratio is read from the scene; another one needs its own run:
// $env:QT_SCALE_FACTOR=1.5; dotnet run --project src/VL.Qt.SmokeTest -c Release -- --only space (DIP: 150 device pixels).
[TestFixture, NonParallelizable]
public static class SpaceTest
{
    private sealed class NoMessage : IDisposable { public void Dispose() { } }

    private static readonly Color4 Bg = new(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
    private const float Eps = 2e-3f;


    /// <summary>Origin (DIP) and DIP per unit of a space for a scene of w x h DIP.</summary>
    private static (Vector2 Origin, float Unit) Layout(Space space, float w, float h, float dpr) => space switch
    {
        Space.Normalized => (new Vector2(w / 2, h / 2), h / 2),
        Space.DIP => (new Vector2(w / 2, h / 2), 100f),
        Space.DIPTopLeft => (Vector2.Zero, 100f),
        _ => (Vector2.Zero, 100f / dpr),
    };

    /// <summary>Coordinate spaces.</summary>
    [Test, Category("space")]
    public static void SpaceScenes() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var scene = new SceneRegistry();
        var r = new WindowRenderer(scene, "VL.Qt space test", new RectangleF(100, 100, 800, 600));
        s.Check(r.Window != IntPtr.Zero, "window created " + r.Error);
        if (r.Window == IntPtr.Zero)
            return;

        using var point = new PointNode(_ => new NoMessage());
        var active = Channel.Create(false);
        var position = Channel.Create(Vector2.Zero);
        var redFeedback = new ElementFeedback();
        var blueFeedback = new ElementFeedback();
        Interaction? interaction = null;
        Element? root = null;
        var space = Space.DIPTopLeft;

        // The tree stays the same object while the interaction does (reference equality, like a patch at rest).
        Element Root()
        {
            var i = point.Update(null, active, position, PointerButtons.Left, PointerDevices.All);
            if (root is null || !ReferenceEquals(i, interaction))
            {
                interaction = i;
                root = new Group
                {
                    Children =
                    [
                        new QtRect { Size = new Vector2(1, 1), Fill = new Color4(1, 0, 0, 1), Feedback = new FeedbackSlot(redFeedback, 0), Interaction = i },
                        new QtRect { Size = new Vector2(0.5f, 0.5f), Transform = Matrix.Translation(1, 0, 0), Fill = new Color4(0, 0, 1, 1), Feedback = new FeedbackSlot(blueFeedback, 0) },
                    ],
                };
            }
            return root;
        }

        void Frame(int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                r.Update(Root(), space, Bg, true, true, "VL.Qt space test");
                r.ApplyCommands();
                s.Pump(3);
                r.AfterEvents();
            }
        }

        (float W, float H, float Dpr) Metrics()
        {
            Native.vlqt_window_get_scene_metrics(r.Window, out var m);
            return (m.width, m.height, m.dpr);
        }

        void Mouse(int type, float x, float y) => Native.vlqt_test_send_mouse(r.Window, type, x, y);

        // Pixel at a DIP position of a w x h scene (the grab is in device pixels).
        static Color At(Bitmap a, float x, float y, float w, float h)
            => a.GetPixel(Math.Clamp((int)(x * a.Width / w), 0, a.Width - 1), Math.Clamp((int)(y * a.Height / h), 0, a.Height - 1));

        void CheckScene(Space sp, string size)
        {
            Frame();
            var (w, h, dpr) = Metrics();
            var (o, k) = Layout(sp, w, h, dpr);
            var tag = $"{sp} {size} (dpr {dpr:F2}, {k:F1} DIP/unit)";

            // Pixels: red 1 x 1 unit at the origin, blue 0.5 x 0.5 right of it, nothing around.
            using (var a = s.Grab(r.Window, $"143_space_{sp}_{size}.png"))
            {
                var failed = new List<string>();
                void Expect(string what, float x, float y, int cr, int cg, int cb)
                {
                    // Points outside the window are not part of the picture (Normalized/DIP rectangles reach beyond a small window).
                    if (x < 0 || y < 0 || x >= w || y >= h)
                        return;
                    var c = At(a, x, y, w, h);
                    if (!Near(c, cr, cg, cb))
                        failed.Add($"{what} ({x:F0}, {y:F0}) = {c.R},{c.G},{c.B}");
                }
                void Red(string what, float x, float y) => Expect(what, x, y, 255, 0, 0);
                void Blue(string what, float x, float y) => Expect(what, x, y, 0, 0, 255);
                void Empty(string what, float x, float y) => Expect(what, x, y, 0x20, 0x20, 0x20);

                var x0 = o.X; var y0 = o.Y; var x1 = o.X + k; var y1 = o.Y + k;
                Red("red centre", x0 + k / 2, y0 + k / 2);
                Red("red top left", x0 + 3, y0 + 3);
                Red("red bottom right", x1 - 3, y1 - 3);
                Empty("left of red", x0 - 3, y0 + k / 2);
                Empty("above red", x0 + k / 2, y0 - 3);
                Empty("below red", x0 + k / 2, y1 + 3);
                Blue("blue centre", x1 + k / 4, y0 + k / 4);
                Empty("right of blue", x1 + k / 2 + 3, y0 + k / 4);
                Empty("below blue", x1 + k / 4, y0 + k / 2 + 3);
                Empty("right of red, below blue", x1 + 3, y0 + k * 0.75f);
                s.Check(failed.Count == 0, $"{tag}: pixels{(failed.Count > 0 ? ": " + string.Join("; ", failed) : "")}");
            }

            // Scene Bounds in units, independent of the space.
            var rb = redFeedback.SceneBounds();
            var bb = blueFeedback.SceneBounds();
            bool Close(RectangleF q, float x, float y, float qw, float qh)
                => Math.Abs(q.X - x) < Eps && Math.Abs(q.Y - y) < Eps && Math.Abs(q.Width - qw) < Eps && Math.Abs(q.Height - qh) < Eps;
            s.Check(Close(rb, 0, 0, 1, 1) && Close(bb, 1, 0, 0.5f, 0.5f), $"{tag}: Scene Bounds in units (red {rb}, blue {bb})");

            // Click at (0.3, 0.7) units of the red rectangle: Position in units, local to the rectangle.
            var px = o.X + 0.3f * k;
            var py = o.Y + 0.7f * k;
            if (px < w && py < h)
            {
                Mouse(Protocol.Mouse.Move, px, py);
                Mouse(Protocol.Mouse.Press, px, py);
                Frame();
                s.Check(active.Value && Math.Abs(position.Value.X - 0.3f) < Eps && Math.Abs(position.Value.Y - 0.7f) < Eps,
                    $"{tag}: click at ({px:F0}, {py:F0}) DIP is (0.3, 0.7) units (active {active.Value}, {position.Value})");
                Mouse(Protocol.Mouse.Release, px, py);
                Mouse(Protocol.Mouse.Move, w - 1, h - 1);
                Frame();
                s.Check(!active.Value, $"{tag}: released");
            }
        }

        Frame();
        var (w0, h0, dpr0) = Metrics();
        s.Info($"scene {w0} x {h0} DIP, device pixel ratio {dpr0:F2} (another dpr needs another run, QT_SCALE_FACTOR)");
        s.Check(w0 == 800 && h0 == 600, $"scene 800 x 600 DIP ({w0} x {h0})");

        // 1) Every space at 800 x 600; each change of the Space pin only moves the origin (lengths stay for DIP ↔ DIPTopLeft).
        var all = new[] { Space.DIPTopLeft, Space.DIP, Space.Normalized, Space.PixelTopLeft };
        foreach (var sp in all)
        {
            space = sp;
            CheckScene(sp, "800x600");
        }

        // 2) Resize to 400 x 300 (back through the spaces): origin and Normalized scale follow the scene size.
        Native.vlqt_window_resize(r.Window, 400, 300);
        s.Pump(10);
        var (w1, h1, _) = Metrics();
        s.Check(w1 == 400 && h1 == 300, $"window resized to 400 x 300 DIP ({w1} x {h1})");
        foreach (var sp in new[] { Space.PixelTopLeft, Space.Normalized, Space.DIP, Space.DIPTopLeft })
        {
            space = sp;
            CheckScene(sp, "400x300");
        }

        // 3) Resync: the invalid command comes before the SET_SPACE of the same frame (DIPTopLeft → DIP: same scale, other origin), QtNative drops
        //    the rest of the batch; the rebuild must send the Space again, else the origin stays where DIPTopLeft put it.
        space = Space.DIPTopLeft;
        Frame();
        uint redId = r.Reconciler.Root!.Children[0].Id;
        r.Commands.SetF32(redId, Protocol.Prop.Color, 1f); // wrong value type for a color property → -7
        space = Space.DIP;
        r.Update(Root(), space, Bg, true, true, "VL.Qt space test");
        r.ApplyCommands();
        s.Pump(3);
        r.AfterEvents();
        s.Check(r.Resyncs == 1 && r.Reconciler.Root is null, $"resync after the invalid command ({r.Resyncs})");
        CheckScene(Space.DIP, "400x300_resync");
        s.Check(r.Error.Length == 0, "rebuild without an error: " + r.Error);

        r.Dispose();
        s.Pump(2);
    }
}
