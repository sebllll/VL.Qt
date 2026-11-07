using System;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;

// One element instance at several places of one tree (#455) through the real QtNative: the same Rectangle and Button
// description in two Groups at different positions. Both places are drawn, clicks from both places reach the shared
// sink, state values (Measured, SceneBounds) come from the first occurrence only and move to the remaining one when
// the first is removed. Grabs go to <outDir>/455_*.png. Node inputs are units (DIPTopLeft, 1 unit = 100 DIP).
static class MultiPlacementTest
{
    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    static bool IsRed(Color c) => c.R > 200 && c.G < 40 && c.B < 40;
    static bool IsBg(Color c) => Math.Abs(c.R - 0x20) < 8 && Math.Abs(c.G - 0x20) < 8 && Math.Abs(c.B - 0x20) < 8;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt multi placement test");
        check(w != IntPtr.Zero, "455: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
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
                new Color4(0, 0, 0, 1), 0, 1, false, false, null, null, out _, out _);
            var button = buttonNode.Update(new Vector2(1, 0.4f), true, Matrix.Translation(0, 1.5f, 0), RectangleAnchor.TopLeft, "Click", true, false,
                null, 1, false, false, null, null, null, out var bang, out var m, out var sb);
            if (bang) clicks++;
            measured = m;
            sceneBounds = sb;
            var children = Spread.Create<IElement?>(rect, button);
            var a = groupA.Update(children, new Vector2(0, 0), true, Matrix.Translation(1, 1, 0), RectangleAnchor.TopLeft, 1, false, false,
                null, null, null, out _, out _);
            var b = groupB.Update(children, new Vector2(0, 0), true, Matrix.Translation(4, 3, 0), RectangleAnchor.TopLeft, 1, false, false,
                null, null, null, out _, out _);
            var e = root.Update(both ? Spread.Create<IElement?>(a, b) : Spread.Create<IElement?>(b), default, true, Matrix.Identity,
                RectangleAnchor.TopLeft, 1, false, false, null, null, null, out _, out _);
            cmd.Reset();
            cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(e, Space.DIPTopLeft);
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"455: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            pump(1);
            int n = Native.Poll(w, buf);
            check(n >= 0, $"455: poll ({n})");
            var res = rec.Sinks.Dispatch(buf.AsSpan(0, Math.Max(n, 0)));
            check(res.Error is null, $"455: dispatch ({res.Error})");
        }
        void Frames(int count) { for (int i = 0; i < count; i++) Frame(); }
        void Mouse(int type, float x, float y) => Native.vlqt_test_send_mouse(w, type, x, y);
        void Click(float x, float y)
        {
            Mouse(Protocol.Mouse.Move, x, y);
            Mouse(Protocol.Mouse.Press, x, y);
            Frames(2);
            Mouse(Protocol.Mouse.Release, x, y);
            Frames(3);
        }
        static bool Close(float a, float b) => Math.Abs(a - b) < 0.02f;

        Frames(5);

        // 1) Both places are drawn (red 100 x 100 DIP at (100, 100) and (400, 300)).
        using (var a = grab(w, Path.Combine(outDir, "455_multi.png")))
            check(IsRed(Px(a, 150, 150)) && IsRed(Px(a, 450, 350)) && IsBg(Px(a, 250, 150)) && IsBg(Px(a, 350, 350)),
                $"455: the same rectangle is drawn at both places ({Px(a, 150, 150)}, {Px(a, 450, 350)}, {Px(a, 250, 150)}, {Px(a, 350, 350)})");
        check(rec.Sinks.Count >= 4, $"455: one sink handle per occurrence ({rec.Sinks.Count})");

        // 2) State values come from the first occurrence (group A at (1, 1), button at y + 1.5).
        check(measured.X > 0.2f && measured.Y > 0.1f, $"455: measured size reported ({measured})");
        check(Close(sceneBounds.X, 1) && Close(sceneBounds.Y, 2.5f),
            $"455: SceneBounds of the first occurrence ({sceneBounds})");

        // 3) Clicks on each place arrive in the shared sink.
        Click(120, 260);
        check(clicks == 1, $"455: click at the first place ({clicks})");
        Click(420, 460);
        check(clicks == 2, $"455: click at the second place ({clicks})");
        check(Close(sceneBounds.X, 1) && Close(sceneBounds.Y, 2.5f),
            $"455: SceneBounds stay with the first occurrence ({sceneBounds})");

        // 4) First occurrence removed: the remaining one is primary now and reports its values.
        both = false;
        Frames(6);
        check(Close(sceneBounds.X, 4) && Close(sceneBounds.Y, 4.5f),
            $"455: SceneBounds of the remaining occurrence after the primary change ({sceneBounds})");
        check(measured.X > 0.2f && measured.Y > 0.1f, $"455: measured size reported after the primary change ({measured})");
        using (var a = grab(w, Path.Combine(outDir, "455_multi_one.png")))
            check(IsBg(Px(a, 150, 150)) && IsRed(Px(a, 450, 350)),
                $"455: only the second place is drawn ({Px(a, 150, 150)}, {Px(a, 450, 350)})");
        Click(420, 460);
        check(clicks == 3, $"455: the remaining occurrence still sends clicks ({clicks})");

        check(warnings == 0, $"455: no duplicate warning ({warnings})");

        rec.Clear();
        Native.vlqt_window_destroy(w);
        pump(1);
    }

    sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
