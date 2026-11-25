using NUnit.Framework;
using System;
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

// One element instance at several places of one tree through the real QtNative: the same Rectangle and Button
// description in two Groups at different positions. Both places are drawn, clicks from both places reach the shared
// feedback, state values (Measured, SceneBounds) come from the first occurrence only and move to the remaining one when
// the first is removed. Grabs go to <outDir>/455_*.png. Node inputs are units (DIPTopLeft, 1 unit = 100 DIP).
[TestFixture, NonParallelizable]
public static class MultiPlacementTest
{
    /// <summary>Element placed several times.</summary>
    [Test, Category("multiplacement")]
    public static void MultiPlacement() => Scenario.Run(Run);

    private static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    private static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    private static bool IsBg(Color c) => Math.Abs(c.R - 0x20) < 8 && Math.Abs(c.G - 0x20) < 8 && Math.Abs(c.B - 0x20) < 8;

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt multi placement test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        var buf = new byte[1 << 16];
        int warnings = 0;
        IDisposable Warn(string _) { warnings++; return new NoopDisposable(); }

        var rectNode = new RectangleNode(Warn);
        var buttonNode = new ButtonNode(Warn);
        var groupA = new GroupNode(Warn);
        var groupB = new GroupNode(Warn);
        var root = new GroupNode(Warn);

        bool both = true;
        int clicks = 0;
        Vector2 measured = default;
        RectangleF sceneBounds = default;

        // One vvvv frame: nodes update (button outputs are pulled from the last poll), renderer reconciles + applies, Qt pumps, poll.
        void Frame()
        {
            var rect = rectNode.Update(new Vector2(1, 1), Matrix.Translation(0, 0, 0), RectangleAnchor.TopLeft, new Color4(1, 0, 0, 1), 0,
                new Color4(0, 0, 0, 1), 0, 1, false, false, null, null, true, out _, out _);
            var button = buttonNode.Update(new Vector2(1, 0.4f), true, Matrix.Translation(0, 1.5f, 0), RectangleAnchor.TopLeft, "Click", true, false,
                null, 1, false, false, null, null, null, out var bang, out var m, out var sb);
            if (bang) clicks++;
            measured = m;
            sceneBounds = sb;
            var children = Spread.Create<IElement?>(rect, button);
            var a = groupA.Update(children, new Vector2(0, 0), true, Matrix.Translation(1, 1, 0), RectangleAnchor.TopLeft, 1, false, false,
                null, null, null, true, out _, out _);
            var b = groupB.Update(children, new Vector2(0, 0), true, Matrix.Translation(4, 3, 0), RectangleAnchor.TopLeft, 1, false, false,
                null, null, null, true, out _, out _);
            var e = root.Update(both ? Spread.Create<IElement?>(a, b) : Spread.Create<IElement?>(b), default, true, Matrix.Identity,
                RectangleAnchor.TopLeft, 1, false, false, null, null, null, true, out _, out _);
            cmd.Clear();
            cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update((Element)e, Space.DIPTopLeft);
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            s.Pump(1);
            int n = NativeCalls.Poll(w, buf);
            s.Check(n >= 0, $"poll ({n})");
            var res = rec.FeedbackRouter.Dispatch(buf.AsSpan(0, Math.Max(n, 0)));
            s.Check(res.Error is null, $"dispatch ({res.Error})");
        }
        void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }
        void Mouse(int type, float x, float y) => NativeTest.vlqt_test_send_mouse(w, type, x, y);
        void Click(float x, float y)
        {
            Mouse(TestProtocol.Mouse.Move, x, y);
            Mouse(TestProtocol.Mouse.Press, x, y);
            Frames(2);
            Mouse(TestProtocol.Mouse.Release, x, y);
            Frames(3);
        }
        static bool Close(float a, float b) => Math.Abs(a - b) < 0.02f;

        Frames(5);

        // 1) Both places are drawn (red 100 x 100 DIP at (100, 100) and (400, 300)).
        using (var a = s.Grab(w, "455_multi.png"))
            s.Check(IsRed(Px(a, 150, 150)) && IsRed(Px(a, 450, 350)) && IsBg(Px(a, 250, 150)) && IsBg(Px(a, 350, 350)),
                $"the same rectangle is drawn at both places ({Px(a, 150, 150)}, {Px(a, 450, 350)}, {Px(a, 250, 150)}, {Px(a, 350, 350)})");
        s.Check(rec.FeedbackRouter.Count >= 4, $"one feedback id per occurrence ({rec.FeedbackRouter.Count})");

        // 2) State values come from the first occurrence (group A at (1, 1), button at y + 1.5).
        s.Check(measured.X > 0.2f && measured.Y > 0.1f, $"measured size reported ({measured})");
        s.Check(Close(sceneBounds.X, 1) && Close(sceneBounds.Y, 2.5f),
            $"SceneBounds of the first occurrence ({sceneBounds})");

        // 3) Clicks on each place arrive in the shared feedback.
        Click(120, 260);
        s.Check(clicks == 1, $"click at the first place ({clicks})");
        Click(420, 460);
        s.Check(clicks == 2, $"click at the second place ({clicks})");
        s.Check(Close(sceneBounds.X, 1) && Close(sceneBounds.Y, 2.5f),
            $"SceneBounds stay with the first occurrence ({sceneBounds})");

        // 4) First occurrence removed: the remaining one is primary now and reports its values.
        both = false;
        Frames(6);
        s.Check(Close(sceneBounds.X, 4) && Close(sceneBounds.Y, 4.5f),
            $"SceneBounds of the remaining occurrence after the primary change ({sceneBounds})");
        s.Check(measured.X > 0.2f && measured.Y > 0.1f, $"measured size reported after the primary change ({measured})");
        using (var a = s.Grab(w, "455_multi_one.png"))
            s.Check(IsBg(Px(a, 150, 150)) && IsRed(Px(a, 450, 350)),
                $"only the second place is drawn ({Px(a, 150, 150)}, {Px(a, 450, 350)})");
        Click(420, 460);
        s.Check(clicks == 3, $"the remaining occurrence still sends clicks ({clicks})");

        s.Check(warnings == 0, $"no duplicate warning ({warnings})");

        rec.Clear();
        Native.vlqt_scene_destroy(w);
        s.Pump(1);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
