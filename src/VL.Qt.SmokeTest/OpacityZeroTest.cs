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
// Grabs: <outDir>/opacity_zero*.png.
[TestFixture, NonParallelizable]
public static class OpacityZeroTest
{
    [Test, Category("opacity-zero")]
    public static void OpacityZero() => Scenario.Run(Run);

    private sealed class NoMessage : IDisposable { public void Dispose() { } }

    private static bool Near(Color c, int r, int g, int b, int tol = 12) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt opacity zero test");
        s.Check(w != SceneHandle.Null, "opacity-zero: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry());
        var buf = new byte[1 << 16];
        using var rectA = new RectangleNode(_ => new NoMessage());
        using var rectB = new RectangleNode(_ => new NoMessage());
        using var rectC = new RectangleNode(_ => new NoMessage());
        using var row = new RowNode(_ => new NoMessage());
        using var hover = new HoverNode(_ => new NoMessage());
        var blur = new BlurNode();
        var hovered = Channel.Create(false);
        float opacityB = 0;
        bool clear = true;

        Element Rect(RectangleNode n, Color4 fill, float opacity, Interaction? interaction, Effect? effect)
            => (Element)n.Update(new Vector2(2, 2), Matrix.Identity, RectangleAnchor.TopLeft, fill, 0, new Color4(0, 0, 0, 1), 0, opacity, false, false,
                interaction, effect, out _, out _);

        // One vvvv frame: nodes update (pull the state of the last pump), reconcile + apply, Qt pumps, poll + dispatch.
        void Frame()
        {
            var chain = hover.Update(null, hovered);
            var a = Rect(rectA, new Color4(0, 0, 1, 1), 1, null, null);
            var b = Rect(rectB, new Color4(1, 0, 0, 1), opacityB, chain, blur.Update(null, 0.6f, 16, 0, true, default));
            var c = Rect(rectC, new Color4(0, 1, 0, 1), 1, null, null);
            // Row at (50, 100) DIP, spacing 0.5: A 50..250, B 300..500, C 550..750 (y 100..300).
            var e = row.Update(Spread.Create<IElement?>(a, b, c), new Vector2(0, 0), true, Matrix.Translation(0.5f, 1, 0), RectangleAnchor.TopLeft,
                0.5f, 0, VerticalAlignment.Top, 1, false, false, null, null, out _, out _);
            cmd.Reset();
            if (clear)
            {
                cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
                clear = false;
            }
            rec.Update((Element)e, Space.DIPTopLeft);
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

        cmd.Reset();
        rec.Update(null, Space.DIPTopLeft);
        if (cmd.Length > 0)
            s.Check(Native.Apply(w, cmd.EndFrame()) == 0, "opacity-zero: clear");
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }
}
