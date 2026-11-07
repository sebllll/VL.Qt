using System;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using QtRect = VL.Qt.Elements.Rectangle;

// Resync after a failed apply (#76) through the real QtNative: an invalid command (SET Color with an f32 value → -7)
// is injected in the middle of a frame's batch, the commands appended after it are dropped by QtNative.
// The next frame must rebuild the scene completely; a permanently failing input must not loop.
// Grabs go to <outDir>/76_*.png.
static class ResyncTest
{
    static bool Near(Color c, int r, int g, int b) => Math.Abs(c.R - r) < 8 && Math.Abs(c.G - g) < 8 && Math.Abs(c.B - b) < 8;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var scene = new SceneRegistry();
        var clear = new Color4(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        var bounds = new RectangleF(100, 100, 800, 600);
        var r = new WindowRenderer(scene, "VL.Qt resync test", bounds);
        check(r.Window != IntPtr.Zero, "76: window created " + r.Error);
        if (r.Window == IntPtr.Zero)
            return;

        Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);

        void Frame(Element? input)
        {
            r.Update(input, Space.DIPTopLeft, clear, true, "VL.Qt resync test");
            r.ApplyCommands();
            pump(3);
            r.AfterEvents();
        }

        Group Tree(float redX, float blueX) => new()
        {
            Size = new Vector2(8, 6), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0),
            Children = ImmutableArray.Create<Element>(
                new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(redX, 1, 0), Fill = new Color4(1, 0, 0, 1) },
                new QtRect { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(blueX, 3, 0), Fill = new Color4(0, 0, 1, 1) }),
        };

        var t1 = Tree(1, 4);
        Frame(t1);
        check(r.Error.Length == 0 && r.Resyncs == 0, "76: first tree applies without error " + r.Error);

        // Frame with the invalid command in the middle: red moves (before the bad command, applied), blue moves
        // (after it, dropped by QtNative); the mirror already believes both.
        var t2 = Tree(5, 6);
        int redId = (int)r.Reconciler.Root!.Children[0].Id;
        r.Update(Tree(5, 4), Space.DIPTopLeft, clear, true, "VL.Qt resync test");
        r.Commands.SetF32((uint)redId, Protocol.Prop.Color, 1f); // wrong value type for a color property → -7
        r.Update(t2, Space.DIPTopLeft, clear, true, "VL.Qt resync test");
        r.ApplyCommands();
        pump(3);
        r.AfterEvents();
        check(r.Error.Length > 0, "76: invalid command reports an apply error: " + r.Error);
        check(r.Resyncs == 1, $"76: one resync after the failure ({r.Resyncs})");
        check(r.Reconciler.Root is null, "76: mirror dropped");

        // Next frame: same input, complete rebuild, scene matches, error gone.
        Frame(t2);
        check(r.Error.Length == 0, "76: rebuild applies without error " + r.Error);
        check(r.Resyncs == 1, "76: no second resync");
        using (var a = grab(r.Window, Path.Combine(outDir, "76_rebuilt.png")))
        {
            check(Near(Px(a, 550, 150), 255, 0, 0), "76: red rect at its new position (5,1) after the rebuild");
            check(Near(Px(a, 150, 150), 0x20, 0x20, 0x20), "76: no leftover of the old red rect");
            check(Near(Px(a, 650, 350), 0, 0, 255), "76: blue rect at (6,3), the command QtNative had dropped");
            check(Near(Px(a, 500, 350), 0x20, 0x20, 0x20), "76: no leftover of the old blue rect");
            check(Near(Px(a, 50, 50), 0x20, 0x20, 0x20), "76: clear color sent again");
        }
        check(scene.Count == 3, $"76: registry owns group + 2 rects once ({scene.Count})");

        // Steady state afterwards: unchanged tree sends nothing, a change is applied normally.
        Frame(t2);
        check(r.Error.Length == 0 && r.Commands.Length == 0, "76: steady state after the resync");
        Frame(Tree(1, 6));
        using (var a = grab(r.Window, Path.Combine(outDir, "76_after.png")))
            check(Near(Px(a, 150, 150), 255, 0, 0) && Near(Px(a, 550, 150), 0x20, 0x20, 0x20), "76: normal diff updates work again");

        // #275: a failing command on an element with a sink goes to that element (sink), not to the renderer's Error.
        var sink = new ElementSink();
        QtRect Bound(float x) => new() { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(x, 1, 0), Fill = new Color4(0, 1, 0, 1), Sink = new SinkSlot(sink, 0) };
        var t3 = new Group { Size = new Vector2(8, 6), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Children = ImmutableArray.Create<Element>(Bound(1)) };
        Frame(t3);
        uint boundId = r.Reconciler.Root!.Children[0].Id;
        r.Commands.SetF32(boundId, Protocol.Prop.Color, 1f); // → -7 on the bound rect
        Frame(t3);
        check(r.Error.Length == 0, "275: apply error of a bound element is not at the renderer: " + r.Error);
        check(sink.Error is { } se && se.StartsWith("Qt rejected Color:"), "275: apply error at the element sink: " + sink.Error);
        check(r.Resyncs == 2, $"275: resync as before ({r.Resyncs})");
        Frame(t3); // rebuild with the unchanged input succeeds (the bad command was injected once)
        check(r.Error.Length == 0 && sink.Error is null, "275: successful rebuild clears the element's apply error: " + sink.Error);

        r.Dispose();
        check(scene.Count == 0, "76: Dispose frees the registry");
        pump(2);
    }
}
