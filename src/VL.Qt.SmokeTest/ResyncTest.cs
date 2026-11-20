using System;
using NUnit.Framework;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using QtRect = VL.Qt.Elements.Rectangle;
using static Pixels;

// Resync after a failed apply through the real QtNative: an invalid command (SET Color with an f32 value → -7)
// is injected in the middle of a frame's batch, the commands appended after it are dropped by QtNative.
// The next frame must rebuild the scene completely; a permanently failing input must not loop.
// Grabs go to <OutDir>/76_*.png.
[TestFixture, NonParallelizable]
public static class ResyncTest
{

    /// <summary>Resync of the scene.</summary>
    [Test, Category("resync")]
    public static void Resync() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var scene = new SceneRegistry();
        var clear = new Color4(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        var bounds = new RectangleF(100, 100, 800, 600);
        var r = new WindowRenderer(scene, "VL.Qt resync test", bounds);
        s.Check(r.Window != IntPtr.Zero, "window created " + r.Error);
        if (r.Window == IntPtr.Zero)
            return;

        Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);

        void Frame(Element? input)
        {
            r.Update(input, Space.DIPTopLeft, clear, true, true, "VL.Qt resync test");
            r.ApplyCommands();
            s.Pump(3);
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
        s.Check(r.Error.Length == 0 && r.Resyncs == 0, "first tree applies without error " + r.Error);

        // Frame with the invalid command in the middle: red moves (before the bad command, applied), blue moves
        // (after it, dropped by QtNative); the mirror already believes both.
        var t2 = Tree(5, 6);
        int redId = (int)r.Reconciler.Root!.Children[0].Id;
        r.Update(Tree(5, 4), Space.DIPTopLeft, clear, true, true, "VL.Qt resync test");
        r.Commands.SetF32((uint)redId, Protocol.Prop.Color, 1f); // wrong value type for a color property → -7
        r.Update(t2, Space.DIPTopLeft, clear, true, true, "VL.Qt resync test");
        r.ApplyCommands();
        s.Pump(3);
        r.AfterEvents();
        s.Check(r.Error.Length > 0, "invalid command reports an apply error: " + r.Error);
        s.Check(r.Resyncs == 1, $"one resync after the failure ({r.Resyncs})");
        s.Check(r.Reconciler.Root is null, "mirror dropped");

        // Next frame: same input, complete rebuild, scene matches, error gone.
        Frame(t2);
        s.Check(r.Error.Length == 0, "rebuild applies without error " + r.Error);
        s.Check(r.Resyncs == 1, "no second resync");
        using (var a = s.Grab(r.Window, "76_rebuilt.png"))
        {
            s.Check(Near(Px(a, 550, 150), 255, 0, 0), "red rect at its new position (5,1) after the rebuild");
            s.Check(Near(Px(a, 150, 150), 0x20, 0x20, 0x20), "no leftover of the old red rect");
            s.Check(Near(Px(a, 650, 350), 0, 0, 255), "blue rect at (6,3), the command QtNative had dropped");
            s.Check(Near(Px(a, 500, 350), 0x20, 0x20, 0x20), "no leftover of the old blue rect");
            s.Check(Near(Px(a, 50, 50), 0x20, 0x20, 0x20), "clear color sent again");
        }
        s.Check(scene.Count == 3, $"registry owns group + 2 rects once ({scene.Count})");

        // Steady state afterwards: unchanged tree sends nothing, a change is applied normally.
        Frame(t2);
        s.Check(r.Error.Length == 0 && r.Commands.Length == 0, "steady state after the resync");
        Frame(Tree(1, 6));
        using (var a = s.Grab(r.Window, "76_after.png"))
            s.Check(Near(Px(a, 150, 150), 255, 0, 0) && Near(Px(a, 550, 150), 0x20, 0x20, 0x20), "normal diff updates work again");

        // a failing command on an element with a feedback goes to that element (feedback), not to the renderer's Error.
        var feedback = new ElementFeedback();
        QtRect Bound(float x) => new() { Size = new Vector2(2, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(x, 1, 0), Fill = new Color4(0, 1, 0, 1), Feedback = new FeedbackSlot(feedback, 0) };
        var t3 = new Group { Size = new Vector2(8, 6), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0, 0, 0), Children = ImmutableArray.Create<Element>(Bound(1)) };
        Frame(t3);
        uint boundId = r.Reconciler.Root!.Children[0].Id;
        r.Commands.SetF32(boundId, Protocol.Prop.Color, 1f); // → -7 on the bound rect
        Frame(t3);
        s.Check(r.Error.Length == 0, "apply error of a bound element is not at the renderer: " + r.Error);
        s.Check(feedback.Error is { } se && se.StartsWith("Qt rejected Color:"), "apply error at the element feedback: " + feedback.Error);
        s.Check(r.Resyncs == 2, $"resync as before ({r.Resyncs})");
        Frame(t3); // rebuild with the unchanged input succeeds (the bad command was injected once)
        s.Check(r.Error.Length == 0 && feedback.Error is null, "successful rebuild clears the element's apply error: " + feedback.Error);

        r.Dispose();
        s.Check(scene.Count == 0, "Dispose frees the registry");
        s.Pump(2);
    }
}
