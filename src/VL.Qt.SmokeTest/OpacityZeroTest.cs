using System;
using System.Drawing;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using NUnit.Framework;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using static Pixels;

// there is no Visible pin any more. Opacity 0 = not drawn (Qt blocks the subtree in the scene graph, also the
// effect layer: QQuickItemLayer gives the MultiEffect the item's opacity), but the element keeps its place in a Row
// and keeps receiving input (invisible hit areas). Row: A (blue) | B (red, Opacity 0, Blur effect, Hover) | C (green).
// Then Enabled off on B (Qt's visible): not drawn, no input and no space in the Row, C moves up; on again: back.
// Grabs: <outDir>/opacity_zero*.png.
[TestFixture, NonParallelizable]
public static class OpacityZeroTest
{
    [Test, Category("opacity-zero")]
    public static void OpacityZero() => Scenario.Run(Run);

    private static bool Near(Color c, int r, int g, int b, int tol = 12) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt opacity zero test");
        s.Check(w != SceneHandle.Null, "opacity-zero: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        var buf = new byte[1 << 16];
        using var rectA = new RectangleNode(new NodeLog().Errors);
        using var rectB = new RectangleNode(new NodeLog().Errors);
        using var rectC = new RectangleNode(new NodeLog().Errors);
        using var row = new RowNode(new NodeLog().Errors);
        using var hover = new HoverNode(new NodeLog().Errors);
        var blur = new BlurNode();
        var hovered = Channel.Create(false);
        float opacityB = 0;
        bool enabledB = true;
        bool clear = true;

        Element Rect(RectangleNode n, Color4 fill, float opacity, Interaction? interaction, Effect? effect, bool enabled = true)
            => (Element)n.Update(new Vector2(2, 2), Matrix.Identity, RectangleAnchor.TopLeft, fill, 0, new Color4(0, 0, 0, 1), 0, opacity, false, false,
                interaction, effect, enabled, out _, out _);

        // One vvvv frame: nodes update (pull the state of the last pump), reconcile + apply, Qt pumps, poll + dispatch.
        void Frame()
        {
            var chain = hover.Update(null, hovered);
            var a = Rect(rectA, new Color4(0, 0, 1, 1), 1, null, null);
            var b = Rect(rectB, new Color4(1, 0, 0, 1), opacityB, chain, blur.Update(null, 0.6f, 16, 0, true, default, true), enabledB);
            var c = Rect(rectC, new Color4(0, 1, 0, 1), 1, null, null);
            // Row at (50, 100) DIP, spacing 0.5: A 50..250, B 300..500, C 550..750 (y 100..300).
            var e = row.Update(Spread.Create<IElement?>(a, b, c), true, new Vector2(0, 0), Matrix.Translation(0.5f, 1, 0), RectangleAnchor.TopLeft,
                0.5f, 0, VerticalAlignment.Top, 1, false, false, null, null, null, true, out _, out _);
            cmd.Clear();
            if (clear)
            {
                cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
                clear = false;
            }
            rec.Update((Element)e, SpaceMapping.Default);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"opacity-zero: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            s.Pump(1);
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"opacity-zero: poll ({n})");
            var res = rec.FeedbackRouter.Dispatch(buf.AsSpan(0, Math.Max(n, 0)));
            s.Check(res.Error is null, $"opacity-zero: dispatch ({res.Error})");
        }
        void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }
        void Mouse(int type, float x, float y) => NativeTest.vlqt_test_send_mouse(w, type, x, y);

        Frames(4);
        using (var img = s.Grab(w, "opacity_zero.png"))
        {
            s.Check(Near(Px(img, 150, 200), 0, 0, 255), $"opacity-zero: A drawn ({Px(img, 150, 200)})");
            // Blur would bleed red around B's edges if the effect layer were still drawn.
            s.Check(IsBg(Px(img, 400, 200)) && IsBg(Px(img, 300, 200)) && IsBg(Px(img, 290, 200)) && IsBg(Px(img, 510, 200)),
                $"opacity-zero: B with Opacity 0 + Blur not drawn ({Px(img, 400, 200)}, {Px(img, 300, 200)}, {Px(img, 290, 200)}, {Px(img, 510, 200)})");
            s.Check(IsBg(Px(img, 520, 200)) && Near(Px(img, 650, 200), 0, 255, 0),
                $"opacity-zero: C after B's place, B keeps its space in the Row ({Px(img, 520, 200)}, {Px(img, 650, 200)})");
        }

        Mouse(TestProtocol.Mouse.Move, 700, 500);
        Frames(2);
        s.Check(!hovered.Value, "opacity-zero: not hovered outside");
        Mouse(TestProtocol.Mouse.Move, 400, 200);
        Frames(2); // event in frame 1, pulled by the node in frame 2
        s.Check(hovered.Value, "opacity-zero: B with Opacity 0 still receives input (Is Hovered)");
        Mouse(TestProtocol.Mouse.Move, 700, 500);
        Frames(2);
        s.Check(!hovered.Value, "opacity-zero: hover out");

        // Back to Opacity 1: drawn again (blurred red at B's center). B sits in its effect layer, which the software
        // backend does not draw.
        opacityB = 1;
        Frames(4);
        using (var img = s.Grab(w, "opacity_zero_back.png"))
            s.NeedsRhi(Px(img, 400, 200).R > 150,
                $"opacity-zero: B drawn again with Opacity 1 ({Px(img, 400, 200)})", "MultiEffect needs a QRhi scenegraph");

        // Enabled off: B and its effect layer are gone, C takes B's place (Row skips the invisible item), no input.
        enabledB = false;
        Frames(4);
        using (var img = s.Grab(w, "opacity_zero_disabled.png"))
        {
            s.Check(Near(Px(img, 150, 200), 0, 0, 255), $"opacity-zero: A drawn with B disabled ({Px(img, 150, 200)})");
            s.Check(Near(Px(img, 400, 200), 0, 255, 0) && IsBg(Px(img, 290, 200)),
                $"opacity-zero: B disabled not drawn, C moved up into its place ({Px(img, 400, 200)}, {Px(img, 290, 200)})");
            s.Check(IsBg(Px(img, 650, 200)), $"opacity-zero: C's old place empty ({Px(img, 650, 200)})");
        }
        Mouse(TestProtocol.Mouse.Move, 400, 200);
        Frames(2);
        s.Check(!hovered.Value, "opacity-zero: B disabled receives no input");
        Mouse(TestProtocol.Mouse.Move, 700, 500);
        Frames(2);

        // Enabled on again: B back in the Row, C back at its place.
        enabledB = true;
        Frames(4);
        using (var img = s.Grab(w, "opacity_zero_enabled.png"))
            s.Check(Near(Px(img, 650, 200), 0, 255, 0) && !Near(Px(img, 400, 200), 0, 255, 0),
                $"opacity-zero: B enabled again, C back after it ({Px(img, 650, 200)}, {Px(img, 400, 200)})");

        cmd.Clear();
        rec.Update(null, SpaceMapping.Default);
        if (cmd.Length > 0)
            s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "opacity-zero: clear");
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }
}
