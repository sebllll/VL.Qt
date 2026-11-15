using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using Rectangle = VL.Qt.Elements.Rectangle;

// TODO: temporary spike of #117 (Clear pin, task 1), replaced by the ClearPin scenario of task 5. Drives the feedback
// layer (src/VL.Qt.Native/feedback.h) through the test hook vlqt_test_set_clear and prints what the decision in
// docs/decisions/render-texture.md ("Clear pin") is based on. Grabs: <outDir>/clear_spike_*.png.
static class ClearSpikeTest
{
    [DllImport("vlqt", CallingConvention = CallingConvention.Cdecl)]
    static extern int vlqt_test_set_clear(IntPtr w, int on);

    static Color Px(Bitmap a, int x, int y, int w = 800, int h = 600) => a.GetPixel(x * a.Width / w, y * a.Height / h);
    static bool Near(Color c, int r, int g, int b, int tol = 10) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;
    static bool IsOrange(Color c) => Near(c, 255, 153, 0);
    static bool IsBg(Color c) => Near(c, 0x20, 0x20, 0x20);

    // Pixels that are neither background nor orange (garbage check).
    static int Odd(Bitmap a)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y += 4)
            for (int x = 0; x < a.Width; x += 4)
            {
                var c = a.GetPixel(x, y);
                // Blur edges of the red rectangle lie between red and the background (G == B, R >= G).
                if (!IsBg(c) && !IsOrange(c) && !(Math.Abs(c.G - c.B) < 10 && c.R >= c.G))
                    n++;
            }
        return n;
    }

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        Window(outDir, check, pump, grab);
        Offscreen(check);
    }

    static void Window(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var client = new ResourceClient(new NativeResourceApi(), Environment.CurrentManagedThreadId, static () => null);
        client.SetPumping(true);
        void Pump(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                client.BeforeEvents();
                pump(1);
                client.AfterEvents();
            }
        }
        var w = Native.vlqt_window_create(800, 600, "VL.Qt clear spike");
        check(w != IntPtr.Zero, "clear-spike: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var scene = new SceneRegistry();
        var rec = new Reconciler(cmd, new IdAllocator(), scene) { Window = w };
        string P(string name) => Path.Combine(outDir, $"clear_spike_{name}.png");

        // Orange 50 x 50 DIP at (x, 100); blurred red rectangle at (300, 400) (effect layer); optional black fade.
        float x = 50;
        float fade = 0;
        var blurred = new Rectangle { Size = new Vector2(1, 1), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(3, 4, 0),
            Fill = new Color4(1, 0, 0, 1), Effect = new BlurEffect(null, 0.6f, 16, 0) };
        Group Tree()
        {
            var moving = new Rectangle { Size = new Vector2(0.5f, 0.5f), Anchor = RectangleAnchor.TopLeft, Transform = Matrix.Translation(x / 100, 1, 0), Fill = new Color4(1, 0.6f, 0, 1) };
            var children = new List<Element>();
            if (fade > 0)
                children.Add(new Rectangle { Size = new Vector2(8, 6), Anchor = RectangleAnchor.TopLeft, Fill = new Color4(0, 0, 0, fade) });
            children.Add(moving);
            children.Add(blurred);
            return new Group { Anchor = RectangleAnchor.TopLeft, Children = [.. children] };
        }
        bool first = true;
        void Frame()
        {
            cmd.Reset();
            if (first) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            first = false;
            rec.Update(Tree(), Space.DIPTopLeft);
            if (cmd.Length > 0)
                check(Native.Apply(w, cmd.EndFrame()) == 0, $"clear-spike: apply {Native.LastError()}");
            Pump(1);
        }
        // Mean GPU / render ms of n frames with the rectangle moving back and forth.
        (double Gpu, double Render, long Changes) Measure(int n)
        {
            double gpu = 0, render = 0;
            int samples = 0;
            long c0 = Native.vlqt_window_change_count(w);
            for (int i = 0; i < n; i++)
            {
                x = 50 + (i % 20) * 25;
                Frame();
                if (Native.vlqt_window_get_perf_sample(w, out var s) == 0)
                {
                    gpu += s.gpu;
                    render += s.render;
                    samples++;
                }
            }
            return (gpu / Math.Max(samples, 1), render / Math.Max(samples, 1), Native.vlqt_window_change_count(w) - c0);
        }

        for (int i = 0; i < 4; i++) Frame();
        var on = Measure(120);
        Console.WriteLine($"clear-spike: window Clear on: gpu {on.Gpu:F3} ms, render {on.Render:F3} ms, {on.Changes} changes / 120");

        // 1) Off: the first frame starts from the clear colour (no garbage), the moving rectangle leaves a trail.
        x = 50;
        Frame();
        check(vlqt_test_set_clear(w, 0) == 0, "clear-spike: set_clear 0");
        Frame();
        using (var img = grab(w, P("first")))
        {
            int odd = Odd(img);
            check(odd < 20 && IsOrange(Px(img, 75, 125)) && IsBg(Px(img, 600, 300)),
                $"clear-spike: first frame after Clear off = clear colour + scene ({odd} odd pixels, {Px(img, 75, 125)}, {Px(img, 600, 300)})");
        }
        for (int i = 1; i <= 10; i++)
        {
            x = 50 + i * 40;
            Frame();
        }
        using (var img = grab(w, P("trail")))
        {
            check(IsOrange(Px(img, 75, 125)) && IsOrange(Px(img, 300, 125)) && IsOrange(Px(img, 470, 125)),
                $"clear-spike: trail of the moving rectangle ({Px(img, 75, 125)}, {Px(img, 300, 125)}, {Px(img, 470, 125)})");
            check(IsBg(Px(img, 600, 300)) && IsBg(Px(img, 75, 60)), $"clear-spike: background outside the trail ({Px(img, 600, 300)}, {Px(img, 75, 60)})");
            check(Px(img, 350, 450).R > 150, $"clear-spike: effect layer (blur) drawn with Clear off ({Px(img, 350, 450)})");
            check(IsBg(Px(img, 75, 475)), $"clear-spike: nothing at the mirrored position ({Px(img, 75, 475)})");
        }

        // 2) Idle: no scene change. Does the recursive live layer keep Qt rendering (continuous frames)?
        long c1 = Native.vlqt_window_change_count(w);
        Pump(30);
        long idle = Native.vlqt_window_change_count(w) - c1;
        Console.WriteLine($"clear-spike: idle with Clear off: {idle} changes / 30 pumps");

        // 3) Fade: a full-size black rectangle at 25 % alpha fades old content (premultiplied "over").
        fade = 0.25f;
        for (int i = 0; i < 30; i++) Frame();
        using (var img = grab(w, P("fade")))
        {
            check(Px(img, 75, 125).R < 20 && Px(img, 600, 300).R < 20 && IsOrange(Px(img, 475, 125)),
                $"clear-spike: fade: old trail and clear colour fade to black, current position stays orange ({Px(img, 75, 125)}, {Px(img, 600, 300)}, {Px(img, 475, 125)})");
        }
        fade = 0;
        Frame();

        // 4) Debug overlay on while moving, then off: its outlines are outside the root, never accumulated.
        check(Native.vlqt_window_set_debug(w, 1) == 0, "clear-spike: debug on");
        for (int i = 0; i < 6; i++) { x = 100 + i * 50; Frame(); }
        grab(w, P("debug_on")).Dispose();
        check(Native.vlqt_window_set_debug(w, 0) == 0, "clear-spike: debug off");
        Frame();
        Frame();
        grab(w, P("debug_off")).Dispose();

        // 5) Snapshot of the blurred rectangle with Clear off: only the element, no feedback in it.
        var snap = new SnapshotNode(client, scene);
        var image = snap.Update(blurred, out var ready, out var error, 1, true);
        for (int i = 0; i < 60 && !ready && error.Length == 0; i++)
        {
            Pump(1);
            image = snap.Update(blurred, out ready, out error, 1);
        }
        check(ready && image != null, $"clear-spike: snapshot ready with Clear off ('{error}')");
        if (image != null)
        {
            SnapshotTest.WritePng(image, P("snapshot"));
            var (b, g, r, a) = SnapshotTest.At(image, 50, 50, 100);
            check(r > 150 && g < 60 && a > 200, $"clear-spike: snapshot shows the element once (B{b} G{g} R{r} A{a})");
        }
        snap.Dispose();

        // 6) Resize: the source texture is recreated, the trail is gone, the first frame shows no garbage.
        for (int i = 0; i < 6; i++) { x = 50 + i * 50; Frame(); }
        check(Native.vlqt_window_resize(w, 700, 500) == 0, "clear-spike: resize");
        x = 500;
        Frame();
        using (var img = grab(w, P("resize")))
        {
            int odd = Odd(img);
            check(odd < 40, $"clear-spike: resize: no garbage ({odd} odd pixels)");
            Console.WriteLine($"clear-spike: after resize: old trail at (75,125) = {Px(img, 75, 125, 700, 500)}");
        }
        check(Native.vlqt_window_resize(w, 800, 600) == 0, "clear-spike: resize back");
        Frame();

        var off = Measure(120);
        Console.WriteLine($"clear-spike: window Clear off: gpu {off.Gpu:F3} ms, render {off.Render:F3} ms, {off.Changes} changes / 120");

        // 7) Clear on again: the trail goes at once.
        check(vlqt_test_set_clear(w, 1) == 0, "clear-spike: set_clear 1");
        x = 500;
        Frame();
        using (var img = grab(w, P("on_again")))
            check(IsBg(Px(img, 75, 125)) && IsOrange(Px(img, 525, 125)), $"clear-spike: Clear on removes the trail ({Px(img, 75, 125)}, {Px(img, 525, 125)})");
        var on2 = Measure(120);
        Console.WriteLine($"clear-spike: window Clear on again: gpu {on2.Gpu:F3} ms, render {on2.Render:F3} ms, {on2.Changes} changes / 120");

        // 8) Cost, interleaved (single runs are noisy): 3 rounds of 120 frames each, Clear on / off, two window sizes. Also
        // toggles and resizes with the layer on and off (a resize after switching on crashed: lambda on the deleted view).
        foreach (var (sw, sh) in new[] { (800, 600), (1920, 1080) })
        {
            Native.vlqt_window_resize(w, sw, sh);
            Frame();
            var gpuOn = new List<double>();
            var gpuOff = new List<double>();
            for (int round = 0; round < 3; round++)
            {
                vlqt_test_set_clear(w, 1);
                gpuOn.Add(Measure(120).Gpu);
                vlqt_test_set_clear(w, 0);
                gpuOff.Add(Measure(120).Gpu);
            }
            Console.WriteLine($"clear-spike: cost {sw}x{sh}: gpu Clear on {string.Join(" / ", gpuOn.Select(v => v.ToString("F3")))} ms, off {string.Join(" / ", gpuOff.Select(v => v.ToString("F3")))} ms");
        }
        vlqt_test_set_clear(w, 1);

        cmd.Reset();
        rec.Update(null, Space.DIPTopLeft);
        if (cmd.Length > 0)
            Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_destroy(w);
        Pump(1);
    }

    // Offscreen scene (QtTexture): 64 x 48, opaque grey clear colour, a 8 x 8 orange box moved along x.
    static void Offscreen(Action<bool, string> check)
    {
        const int B8G8R8A8Typeless = 90, B8G8R8A8Unorm = 87;
        if (!check2(Native.vlqt_test_d3d11_create_device(0, out var device, out var context) == 0, "clear-spike offscreen: test device"))
            return;
        bool check2(bool ok, string what) { check(ok, what); return ok; }
        int w = 64, h = 48;
        var scene = Native.vlqt_offscreen_create(w, h);
        var cmd = new CommandWriter();
        cmd.Reset();
        cmd.ClearColor(0.125f, 0.125f, 0.125f, 1);
        cmd.Create(1, Protocol.Type.Rectangle);
        cmd.SetPlace(1, 0, 4, 8, 8);
        cmd.SetColor(1, Protocol.Prop.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(1, 0, 0);
        check(Native.Apply(scene, cmd.EndFrame()) == 0, $"clear-spike offscreen: apply {Native.LastError()}");
        IntPtr tex = IntPtr.Zero;
        void Bind(int bw, int bh)
        {
            if (tex != IntPtr.Zero)
            {
                Native.vlqt_offscreen_unbind(scene);
                Native.vlqt_test_com_release(tex);
            }
            Native.vlqt_test_d3d11_create_shared_texture(device, bw, bh, B8G8R8A8Typeless, out tex, out var handle);
            int rc = Native.vlqt_offscreen_bind(scene, device, handle, bw, bh, B8G8R8A8Unorm);
            Native.vlqt_test_close_handle(handle);
            check(rc == 0, $"clear-spike offscreen: bind {bw}x{bh} ({rc}) {Native.LastError()}");
            (w, h) = (bw, bh);
        }
        int Render() => Native.OffscreenRenderBatch(context, new[] { scene }, 0);
        byte[] Read()
        {
            var px = new byte[w * h * 4];
            Native.TestReadTexture(tex, px, out _, out _, out _);
            return px;
        }
        (int R, int G, int B, int A) At(byte[] px, int x, int y) { int i = (y * w + x) * 4; return (px[i + 2], px[i + 1], px[i], px[i + 3]); }
        void Move(float x)
        {
            cmd.Reset();
            cmd.SetPlace(1, x, 4, 8, 8);
            Native.Apply(scene, cmd.EndFrame());
        }

        Bind(64, 48);
        int r0 = Render();
        var pxOn = Read();
        Console.WriteLine($"clear-spike offscreen: before Clear off: rendered {r0}, box {At(pxOn, 4, 8)}, background {At(pxOn, 40, 30)}");
        check(vlqt_test_set_clear(scene, 0) == 0, "clear-spike offscreen: set_clear 0");
        int first = Render();
        var px0 = Read();
        check(At(px0, 4, 8).R == 255 && At(px0, 40, 30).R == 32 && At(px0, 40, 30).A == 255,
            $"clear-spike offscreen: first frame after Clear off = clear colour + box (rendered {first}, {At(px0, 4, 8)}, {At(px0, 40, 30)})");
        for (int i = 1; i <= 6; i++)
        {
            Move(i * 8);
            Render();
        }
        var px1 = Read();
        check(At(px1, 4, 8).R == 255 && At(px1, 28, 8).R == 255 && At(px1, 52, 8).R == 255 && At(px1, 40, 30).R == 32,
            $"clear-spike offscreen: trail ({At(px1, 4, 8)}, {At(px1, 28, 8)}, {At(px1, 52, 8)}, background {At(px1, 40, 30)})");
        int idle = 0;
        for (int i = 0; i < 10; i++)
            idle += Render() > 0 ? 1 : 0;
        Console.WriteLine($"clear-spike offscreen: idle with Clear off: {idle} of 10 batches rendered");

        Bind(80, 40);
        Move(60);
        Render();
        var px2 = Read();
        int odd = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = At(px2, x, y);
                if (!(c.R == 32 && c.G == 32) && !(c.R == 255 && c.G > 140 && c.G < 165))
                    odd++;
            }
        check(odd == 0 && At(px2, 4, 8).R == 32 && At(px2, 64, 8).R == 255,
            $"clear-spike offscreen: rebind with a new size clears once, no garbage ({odd} odd, {At(px2, 4, 8)}, {At(px2, 64, 8)})");

        check(vlqt_test_set_clear(scene, 1) == 0, "clear-spike offscreen: set_clear 1");
        Move(20);
        Render();
        var px3 = Read();
        check(At(px3, 64, 8).R == 32 && At(px3, 24, 8).R == 255, $"clear-spike offscreen: Clear on removes the trail ({At(px3, 64, 8)}, {At(px3, 24, 8)})");

        Native.vlqt_offscreen_unbind(scene);
        Native.vlqt_test_com_release(tex);
        Native.vlqt_window_destroy(scene);
        Native.vlqt_test_d3d11_release_device(device, context);
    }
}
