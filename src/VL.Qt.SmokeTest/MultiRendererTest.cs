using NUnit.Framework;
using System;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Lib.Mathematics;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;

// One element tree in two renderers of one OccurrenceRegistry: a WindowScene (registered first, owns the element)
// and an OffscreenScene (D3D11, Windows only). Both draw it, no warning in the feedback; state values (SceneBounds,
// Measured) come from the window; after the window renderer is disposed the offscreen renderer takes over (after its
// next update), state values keep arriving and Snapshot / TryLocate find the element in the offscreen scene.
// The window shows the tree inside a translated Group, the offscreen scene shows it at its origin: SceneBounds tell
// the owner. PNGs: <OutDir>/126_window.png, 126_offscreen.png, 126_snapshot.png.
[TestFixture, NonParallelizable]
public static class MultiRendererTest
{
    private const int WindowW = 480, WindowH = 360;
    private const int TexW = 256, TexH = 192;
    private const int B8G8R8A8Typeless = 90, B8G8R8A8Unorm = 87;
    private const int MaxFrames = 120;

    private static bool IsOrange(int r, int g, int b) => Math.Abs(r - 255) < 8 && Math.Abs(g - 153) < 8 && b < 8;
    private static bool IsBg(int r, int g, int b) => Math.Abs(r - 0x20) < 8 && Math.Abs(g - 0x20) < 8 && Math.Abs(b - 0x20) < 8;
    private static bool Close(float a, float b) => Math.Abs(a - b) < 0.02f;

    /// <summary>One element tree in a window and an offscreen scene.</summary>
    [Test, Category("multirenderer")]
    public static void MultiRenderer() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        bool check(bool ok, string what)
        {
            s.Check(ok, what);
            return ok;
        }
        if (!OperatingSystem.IsWindows())
        {
            s.Skip("multirenderer: offscreen scenes need D3D11");
            return;
        }
        s.Info("multirenderer: #126 one element tree in a window and an offscreen scene");

        if (!check(NativeTest.CreateDevice(0, out var device, out var context) == 0, "create test device"))
            return;
        if (!check(NativeTest.vlqt_test_d3d11_create_shared_texture(device, TexW, TexH, B8G8R8A8Typeless, out var texture, out var handle) == 0, "create shared texture"))
        {
            NativeTest.vlqt_test_d3d11_release_device(device, context);
            return;
        }

        var client = new ResourceClient(new NativeResourceApi(), Environment.CurrentManagedThreadId, static () => null);
        client.SetPumping(true);

        var registry = new OccurrenceRegistry();
        var window = new WindowScene(registry, "VL.Qt multi renderer test", new RectangleF(0, 0, WindowW, WindowH));
        var offscreen = new OffscreenScene(registry, TexW, TexH);
        check(window.Window != SceneHandle.Null && offscreen.Handle != SceneHandle.Null, $"renderers created ({window.Error}{offscreen.Error})");
        var rc = offscreen.Bind(device, handle, new Int2(TexW, TexH), B8G8R8A8Unorm);
        NativeTest.vlqt_test_close_handle(handle);
        check(rc == Native.Result.Ok && offscreen.IsBound, $"offscreen bind ({rc}) {offscreen.Error}");

        // Rectangle (120 x 80 DIP at (20, 20), orange) and Text below it; Feedback slots 0 (rectangle) and 1 (text).
        var feedback = new ElementFeedback();
        var rect = new VL.Qt.Elements.Rectangle
        {
            Size = new Vector2(1.2f, 0.8f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 0.2f, 0),
            Fill = new Color4(1, 0.6f, 0, 1), Feedback = new FeedbackSlot(feedback, 0),
        };
        var text = new Text
        {
            Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(0.2f, 1.1f, 0), Content = "Multi", FontSize = 0.4f,
            Color = new Color4(1, 1, 1, 1), Feedback = new FeedbackSlot(feedback, 1),
        };
        var content = new Group { Anchor = RectangleAnchor.TopLeft, Children = [rect, text] };
        // The window shows the same group one unit right and half a unit down.
        var shifted = new Group { Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(1, 0.5f, 0), Children = [content] };
        var clear = new Color4(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);

        bool windowAlive = true;
        // One host frame as the vvvv renderers run it: update both, apply both, render the offscreen batch, s, poll both.
        void Frame()
        {
            if (windowAlive)
                window.Update(shifted, Space.DIPTopLeft, clear, true, true, "VL.Qt multi renderer test");
            offscreen.Update(content, Space.DIPTopLeft, clear, true, true);
            if (windowAlive)
                window.ApplyCommands();
            offscreen.ApplyCommands();
            var rendered = OffscreenScene.RenderBatch(context, new[] { offscreen.Handle }, 0);
            if (rendered < 0)
                check(false, $"render batch ({rendered}) {OffscreenScene.Describe((Native.Result)rendered)}");
            client.BeforeEvents();
            s.Pump(1);
            client.AfterEvents();
            if (windowAlive)
                window.AfterEvents();
            offscreen.PollEvents();
        }
        bool Until(Func<bool> done, int max = MaxFrames)
        {
            for (int i = 0; i < max && !done(); i++)
                Frame();
            return done();
        }

        // Pixels of the shared texture (BGRA8 premultiplied, opaque scene: no alpha issue).
        (int R, int G, int B) Tex(byte[] px, int x, int y)
        {
            int i = (y * TexW + x) * 4;
            return (px[i + 2], px[i + 1], px[i]);
        }
        (int R, int G, int B) Win(Bitmap a, int x, int y) => ToRgb(a.GetPixel(x * a.Width / WindowW, y * a.Height / WindowH));
        static (int R, int G, int B) ToRgb(Color c) => (c.R, c.G, c.B);
        static bool HasInk((int R, int G, int B) c) => c.R > 150 && c.G > 150 && c.B > 150;

        // 1) Both renderers draw the tree; no warning, no error, one feedback for both.
        bool measured = Until(() => feedback[1].MeasuredSize.X > 0.1f && feedback[0].SceneBounds.Width > 0);
        check(measured, $"state values arrive ({feedback[1].MeasuredSize}, {feedback[0].SceneBounds})");
        Until(() => false, 4);
        var pixels = new byte[TexW * TexH * 4];
        check(NativeCalls.ReadTexture(texture, pixels, out _, out _, out _) == pixels.Length, "read back the texture");
        {
            var c = Tex(pixels, 80, 60);
            var bg = Tex(pixels, 200, 60);
            check(IsOrange(c.R, c.G, c.B) && IsBg(bg.R, bg.G, bg.B), $"offscreen draws the rectangle ({c}, background {bg})");
            int ink = 0;
            for (int y = 110; y < 150; y++)
                for (int x = 20; x < 200; x++)
                    if (HasInk(Tex(pixels, x, y)))
                        ink++;
            check(ink > 50, $"offscreen draws the text ({ink} light pixels)");
            QtTextureTest.WritePng(Path.Combine(s.OutDir, "126_offscreen.png"), pixels, TexW, TexH);
        }
        using (var a = s.Grab(window.Window, Path.Combine(s.OutDir, "126_window.png")))
        {
            var c = Win(a, 180, 130); // rectangle at (120, 70) .. (240, 150)
            var bg = Win(a, 300, 130);
            var offBg = Win(a, 60, 130); // where the offscreen scene has the rectangle
            int ink = 0;
            for (int y = 160; y < 200; y++)
                for (int x = 120; x < 300; x++)
                    if (HasInk(Win(a, x, y)))
                        ink++;
            check(IsOrange(c.R, c.G, c.B) && IsBg(bg.R, bg.G, bg.B) && IsBg(offBg.R, offBg.G, offBg.B), $"window draws the rectangle shifted ({c}, {bg}, {offBg})");
            check(ink > 50, $"window draws the text ({ink} light pixels)");
        }
        check(feedback[0].Warning is null && feedback[1].Warning is null, $"no warning in the feedback ({feedback[0].Warning} / {feedback[1].Warning})");
        check(window.Error.Length == 0 && offscreen.Error.Length == 0, $"no renderer error ('{window.Error}' '{offscreen.Error}')");
        check(registry.OccurrencesOf(rect) == 2 && registry.DuplicatesOf(rect) == 0, $"two occurrences of the rectangle, no duplicate ({registry.OccurrencesOf(rect)})");

        // 2) State values come from the primary: the window renderer, which shows the group at (1, 0.5) units.
        check(registry.OwnerReconcilerOf(rect) == window.Reconciler, "the window renderer owns the rectangle");
        check(Close(feedback[0].SceneBounds.X, 1.2f) && Close(feedback[0].SceneBounds.Y, 0.7f) && Close(feedback[0].SceneBounds.Width, 1.2f),
            $"SceneBounds from the window ({feedback[0].SceneBounds}, want 1.2, 0.7, 1.2 x 0.8)");
        check(registry.TryLocate(rect, out var located, out _) && located == window.Window, "TryLocate finds the rectangle in the window");

        // 3) The window renderer goes away: the offscreen renderer takes over in its next update.
        windowAlive = false;
        window.Dispose();
        bool tookOver = Until(() => registry.OwnerReconcilerOf(rect) == offscreen.Reconciler && Close(feedback[0].SceneBounds.X, 0.2f));
        check(tookOver, $"offscreen renderer owns the rectangle after the dispose ({feedback[0].SceneBounds})");
        check(Close(feedback[0].SceneBounds.X, 0.2f) && Close(feedback[0].SceneBounds.Y, 0.2f) && Close(feedback[0].SceneBounds.Width, 1.2f),
            $"SceneBounds from the offscreen scene ({feedback[0].SceneBounds}, want 0.2, 0.2, 1.2 x 0.8)");
        check(feedback[1].MeasuredSize.X > 0.1f, $"Measured keeps arriving ({feedback[1].MeasuredSize})");
        check(registry.OccurrencesOf(rect) == 1 && feedback[0].Warning is null && offscreen.Error.Length == 0,
            $"one occurrence left, no warning, no error ({registry.OccurrencesOf(rect)}, {feedback[0].Warning}, '{offscreen.Error}')");
        // The measured size changes with the text of a new instance: values still come in after the hand-over.
        var longer = new Text
        {
            Anchor = RectangleAnchor.TopLeft, Transform = text.Transform, Content = "Multi renderer", FontSize = 0.4f,
            Color = text.Color, Feedback = text.Feedback,
        };
        var content2 = new Group { Anchor = RectangleAnchor.TopLeft, Children = [rect, longer] };
        float before = feedback[1].MeasuredSize.X;
        void FrameOffscreen(Element tree)
        {
            offscreen.Update(tree, Space.DIPTopLeft, clear, true, true);
            offscreen.ApplyCommands();
            OffscreenScene.RenderBatch(context, new[] { offscreen.Handle }, 0);
            client.BeforeEvents();
            s.Pump(1);
            client.AfterEvents();
            offscreen.PollEvents();
        }
        int n = 0;
        for (; n < MaxFrames && feedback[1].MeasuredSize.X <= before + 0.05f; n++)
            FrameOffscreen(content2);
        check(feedback[1].MeasuredSize.X > before + 0.05f, $"Measured follows the offscreen scene ({before} -> {feedback[1].MeasuredSize.X} after {n} frames)");

        // 4) TryLocate and Snapshot find the rectangle in the offscreen scene.
        check(registry.TryLocate(rect, out var located2, out _) && located2 == offscreen.Handle, "TryLocate finds the rectangle in the offscreen scene");
        var snapshot = new SnapshotNode(client, registry);
        IImage? image = snapshot.Update(rect, out var ready, out var error, 1, true);
        for (int i = 0; i < MaxFrames && !ready && error.Length == 0; i++)
        {
            FrameOffscreen(content2);
            image = snapshot.Update(rect, out ready, out error);
        }
        check(ready && image is not null && error.Length == 0, $"snapshot of the rectangle is ready ('{error}')");
        if (image is not null && ready)
        {
            SnapshotTest.WritePng(image, Path.Combine(s.OutDir, "126_snapshot.png"));
            var (b, g, r, a) = SnapshotTest.At(image, 60, 40, 120);
            check(image.Info.Width == 120 && image.Info.Height == 80 && a == 255 && IsOrange(r, g, b),
                $"snapshot is the 120 x 80 orange rectangle ({image.Info.Width} x {image.Info.Height}, R{r} G{g} B{b} A{a})");
        }
        snapshot.Dispose();

        offscreen.Dispose();
        check(registry.Count == 0, $"registry empty after both renderers are gone ({registry.Count})");
        s.Pump(2);
        // Flush the release of the snapshot resource: a Ready resource left behind shows in the counts of later scenarios.
        for (int i = 0; i < 3; i++)
        {
            client.BeforeEvents();
            s.Pump(1);
            client.AfterEvents();
        }
        client.SetPumping(false);
        NativeTest.vlqt_test_com_release(texture);
        NativeTest.vlqt_test_d3d11_release_device(device, context);
    }
}
