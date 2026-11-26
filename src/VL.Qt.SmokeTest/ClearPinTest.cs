using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using Rectangle = VL.Qt.Elements.Rectangle;

// Acceptance scenario of the Clear pin: drives the feedback layer (src/VL.Qt.Native/feedback.h) through the
// SET_CLEAR command, in a window and in an offscreen scene (QtTexture). Clear off with a transparent Clear Color (alpha 0):
// a moving rectangle leaves a trail, a semi-transparent full-size rectangle fades it, effect layers, snapshots and the debug
// overlay stay correct, a resize clears once, MSAA edges match Clear on. The alpha of Clear Color while Clear is off
// is the fade strength: 0.25 fades older positions away, 1 equals Clear on, a change at runtime keeps the layer. Clear on
// again: trail gone, and cost and idle behaviour as in the baseline measured before the first switch. Prints the numbers
// the decision in docs/design/clear-and-msaa.md is based on. Grabs: <OutDir>/clear_pin_*.png (window)
// and clear_pin_offscreen_*.png.
public static class ClearPinTest
{
    // The commands go in a buffer of their own (the scenario's writer belongs to the reconciler). 0 = ok.
    // Clear Color (grey, alpha) alone: the layer stays, as when vvvv changes the pin at runtime.
    private static Native.Result SetClearColor(SceneHandle w, float grey, float alpha)
    {
        var cmd = new CommandWriter();
        cmd.Clear();
        cmd.ClearColor(grey, grey, grey, alpha);
        s_bg = ExpectedBg(alpha);
        return Native.Apply(w, cmd.EndFrame());
    }

    // Clear Color and Clear in one buffer. Clear off needs alpha < 1 to show a trail (alpha 1 is Clear on), 0 = pure accumulation.
    private static Native.Result SetMode(SceneHandle w, float grey, bool clear, float alpha)
    {
        var cmd = new CommandWriter();
        cmd.Clear();
        cmd.ClearColor(grey, grey, grey, alpha);
        cmd.SetClear(clear);
        s_bg = ExpectedBg(alpha);
        return Native.Apply(w, cmd.EndFrame());
    }

    // Background of a grab with Clear Color alpha 0. The QRhi OpenGL window (Linux) has no alpha buffer (alphaBufferSize 0), so
    // its grab keeps the RGB of the clear colour. D3D11 clears to transparent black, and the software backend renders the grab
    // into an opaque pixmap whose transparent areas come out black (vlqt.h); the backend is known after the first frame.
    private static int ExpectedBg(float alpha) => alpha > 0 || (!OperatingSystem.IsWindows() && !QtHost.QuickSoftware) ? 0x20 : 0;

    private const float WindowGrey = 0x20 / 255f;
    private const float OffscreenGrey = 0.125f;

    // Pixels of a and b (same size) that differ by more than tol in a channel, every second row and column.
    private static int Diff(Bitmap a, Bitmap b, int tol)
    {
        int n = 0;
        for (int y = 0; y < Math.Min(a.Height, b.Height); y += 2)
            for (int x = 0; x < Math.Min(a.Width, b.Width); x += 2)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.R - q.R) > tol || Math.Abs(p.G - q.G) > tol || Math.Abs(p.B - q.B) > tol)
                    n++;
            }
        return n;
    }

    private static Color Px(Bitmap a, int x, int y, int w = 800, int h = 600) => a.GetPixel(x * a.Width / w, y * a.Height / h);
    private static bool Near(Color c, int r, int g, int b, int tol = 10) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;
    private static bool IsOrange(Color c) => Near(c, 255, 153, 0);
    // The grabbed window shows the clear colour (0x20) while its alpha is > 0; with alpha 0 Qt clears to transparent black (premultiplied).
    private static int s_bg = 0x20;
    private static bool IsBg(Color c) => Near(c, s_bg, s_bg, s_bg);

    // Pixels that are neither background nor orange (garbage check).
    private static int Odd(Bitmap a)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y += 4)
            for (int x = 0; x < a.Width; x += 4)
            {
                var c = a.GetPixel(x, y);
                // Edges of the rectangles lie between their colour and the background (R >= G >= B, within rounding).
                if (!IsBg(c) && !IsOrange(c) && !(c.R >= c.G - 2 && c.G >= c.B - 10))
                    n++;
            }
        return n;
    }

    // Test method: ZzLastTest.ClearPin (it leaves two VSync windows open and must run last).
    internal static void Run(Scenario s)
    {
        Window(s);
        // Offscreen scenes need Direct3D 11 (QRhi), as in SharedDeviceTest.
        if (!OperatingSystem.IsWindows() || s.QuickSoftware)
            s.Skip("clear-pin offscreen: offscreen scenes need Direct3D 11");
        else
            Offscreen(s);
    }

    private static void Window(Scenario s)
    {
        var client = new ResourceClient(new NativeResourceApi(), Environment.CurrentManagedThreadId, static () => null);
        client.SetPumping(true);
        void Pump(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                client.BeforeEvents();
                s.Pump(1);
                client.AfterEvents();
            }
        }
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt clear spike");
        s.Check(w != SceneHandle.Null, "clear-pin: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var scene = new OccurrenceRegistry();
        var interactions = new InteractionRegistry();
        var rec = new Reconciler(cmd, new IdAllocator(), scene, interactions) { Window = w };
        string P(string name) => Path.Combine(s.OutDir, $"clear_pin_{name}.png");

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
            cmd.Clear();
            if (first) cmd.ClearColor(WindowGrey, WindowGrey, WindowGrey, 1);
            first = false;
            rec.Update(Tree(), SpaceMapping.Default);
            if (cmd.Length > 0)
                s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"clear-pin: apply {Native.LastError()}");
            Pump(1);
        }
        // Mean GPU / render ms of n frames with the rectangle moving back and forth.
        (double Gpu, double Render, long Changes) Measure(int n)
        {
            double gpu = 0, render = 0;
            int samples = 0;
            long c0 = Native.vlqt_scene_get_change_count(w);
            for (int i = 0; i < n; i++)
            {
                x = 50 + (i % 20) * 25;
                Frame();
                if (Native.vlqt_scene_get_perf_sample(w, out var s) == 0)
                {
                    gpu += s.gpu;
                    render += s.render;
                    samples++;
                }
            }
            return (gpu / Math.Max(samples, 1), render / Math.Max(samples, 1), Native.vlqt_scene_get_change_count(w) - c0);
        }

        // Best of 3 rounds: the GPU clock varies between runs, a regression is above the limit in every round.
        (double Gpu, double Render, long Changes) Best(int rounds, int n)
        {
            var best = Measure(n);
            for (int r = 1; r < rounds; r++)
            {
                var m = Measure(n);
                best = (Math.Min(best.Gpu, m.Gpu), Math.Min(best.Render, m.Render), best.Changes);
            }
            return best;
        }

        for (int i = 0; i < 4; i++) Frame();
        var on = Best(3, 60);
        Console.WriteLine($"clear-pin: window Clear on (baseline): gpu {on.Gpu:F3} ms, render {on.Render:F3} ms, {on.Changes} changes / 60");
        // Static scene with Clear on: no change, no render.
        long c0 = Native.vlqt_scene_get_change_count(w);
        Pump(30);
        long idleBase = Native.vlqt_scene_get_change_count(w) - c0;
        s.Check(idleBase == 0, $"clear-pin: idle with Clear on renders nothing ({idleBase} changes / 30 pumps)");

        // Checks of the feedback layer: QtNative ignores Clear off on the software backend (no ShaderEffectSource there),
        // the frame then renders as with Clear on; the checks without "layer" hold on both backends.
        Action<bool, string> layer = (ok, what) => s.NeedsRhi(ok, what, "Clear off needs a QRhi scenegraph");

        // 1) Off: the first frame starts from the clear colour (no garbage), the moving rectangle leaves a trail.
        x = 50;
        Frame();
        // Alpha 0: nothing fades, pure accumulation (an opaque Clear Color with Clear off is Clear on).
        s.Check(SetMode(w, WindowGrey, false, 0) == 0, "clear-pin: set_clear 0 (alpha 0)");
        Frame();
        using (var img = s.Grab(w, P("first")))
        {
            int odd = Odd(img);
            s.Check(odd < 20 && IsOrange(Px(img, 75, 125)) && IsBg(Px(img, 600, 300)),
                $"clear-pin: first frame after Clear off = clear colour + scene ({odd} odd pixels, {Px(img, 75, 125)}, {Px(img, 600, 300)})");
        }
        for (int i = 1; i <= 10; i++)
        {
            x = 50 + i * 40;
            Frame();
        }
        using (var img = s.Grab(w, P("trail")))
        {
            s.Check(IsOrange(Px(img, 475, 125)) && IsBg(Px(img, 600, 300)),
                $"clear-pin: Clear off does not blank the frame ({Px(img, 475, 125)}, {Px(img, 600, 300)})");
            layer(IsOrange(Px(img, 75, 125)) && IsOrange(Px(img, 300, 125)) && IsOrange(Px(img, 470, 125)),
                $"clear-pin: trail of the moving rectangle ({Px(img, 75, 125)}, {Px(img, 300, 125)}, {Px(img, 470, 125)})");
            s.Check(IsBg(Px(img, 600, 300)) && IsBg(Px(img, 75, 60)), $"clear-pin: background outside the trail ({Px(img, 600, 300)}, {Px(img, 75, 60)})");
            layer(Px(img, 350, 450).R > 150, $"clear-pin: effect layer (blur) drawn with Clear off ({Px(img, 350, 450)})");
            s.Check(IsBg(Px(img, 75, 475)), $"clear-pin: nothing at the mirrored position ({Px(img, 75, 475)})");
        }

        // 2) Idle: no scene change. Does the recursive live layer keep Qt rendering (continuous frames)?
        long c1 = Native.vlqt_scene_get_change_count(w);
        Pump(30);
        long idle = Native.vlqt_scene_get_change_count(w) - c1;
        Console.WriteLine($"clear-pin: idle with Clear off: {idle} changes / 30 pumps");
        // The recursive layer requests frames itself: a fade keeps fading without a scene change (as Skia/Stride with Clear off).
        layer(idle > 0, $"clear-pin: Clear off keeps rendering without a scene change ({idle} changes / 30 pumps)");

        // 3) Fade with alpha 0: a full-size black rectangle at 25 % alpha fades old content (premultiplied "over").
        fade = 0.25f;
        for (int i = 0; i < 30; i++) Frame();
        using (var img = s.Grab(w, P("fade")))
        {
            layer(Px(img, 75, 125).R < 20 && Px(img, 600, 300).R < 20 && IsOrange(Px(img, 475, 125)),
                $"clear-pin: fade: old trail and clear colour fade to black, current position stays orange ({Px(img, 75, 125)}, {Px(img, 600, 300)}, {Px(img, 475, 125)})");
        }
        fade = 0;
        Frame();

        // 4) Debug overlay on while moving, then off: its outlines are outside the root, never accumulated.
        s.Check(Native.vlqt_scene_set_debug(w, 1) == 0, "clear-pin: debug on");
        for (int i = 0; i < 6; i++) { x = 100 + i * 50; Frame(); }
        s.Grab(w, P("debug_on")).Dispose();
        s.Check(Native.vlqt_scene_set_debug(w, 0) == 0, "clear-pin: debug off");
        Frame();
        Frame();
        s.Grab(w, P("debug_off")).Dispose();

        // 5) Snapshot of the blurred rectangle with Clear off: only the element, no feedback in it.
        var snapLog = new NodeLog();
        var snap = new SnapshotNode(client, scene, snapLog.Errors);
        var image = snap.Update(blurred, 1, true, out var ready);
        var error = snapLog.Take();
        for (int i = 0; i < 60 && !ready && error.Length == 0; i++)
        {
            Pump(1);
            image = snap.Update(blurred, 1, false, out ready);
            error = snapLog.Take();
        }
        s.Check(ready && image != null, $"clear-pin: snapshot ready with Clear off ('{error}')");
        if (image != null)
        {
            SnapshotTest.WritePng(image, P("snapshot"));
            var (b, g, r, a) = SnapshotTest.At(image, 50, 50, 100);
            layer(r > 150 && g < 60 && a > 200, $"clear-pin: snapshot shows the element once (B{b} G{g} R{r} A{a})");
        }
        snap.Dispose();

        // 6) Resize: the source texture is recreated, the trail is gone, the first frame shows no garbage.
        for (int i = 0; i < 6; i++) { x = 50 + i * 50; Frame(); }
        s.Check(Native.vlqt_window_resize(w, 700, 500) == 0, "clear-pin: resize");
        x = 500;
        Frame();
        using (var img = s.Grab(w, P("resize")))
        {
            int odd = Odd(img);
            s.Check(odd < 40, $"clear-pin: resize: no garbage ({odd} odd pixels)");
            s.Check(IsBg(Px(img, 75, 125, 700, 500)) && IsOrange(Px(img, 525, 125, 700, 500)),
                $"clear-pin: resize clears the trail ({Px(img, 75, 125, 700, 500)}, {Px(img, 525, 125, 700, 500)})");
        }
        // The layer settles within two frames (second texture of the recursive source); from then on the trail accumulates.
        x = 500;
        Frame();
        x = 575;
        Frame();
        using (var img = s.Grab(w, P("resize_after")))
            layer(IsOrange(Px(img, 525, 125, 700, 500)) && IsOrange(Px(img, 600, 125, 700, 500)) && IsBg(Px(img, 75, 125, 700, 500)),
                $"clear-pin: trail accumulates again after the resize ({Px(img, 525, 125, 700, 500)}, {Px(img, 600, 125, 700, 500)})");
        s.Check(Native.vlqt_window_resize(w, 800, 600) == 0, "clear-pin: resize back");
        Frame();

        var off = Best(3, 60);
        Console.WriteLine($"clear-pin: window Clear off: gpu {off.Gpu:F3} ms, render {off.Render:F3} ms, {off.Changes} changes / 60");

        // 7) Clear on again: the trail goes at once.
        s.Check(SetMode(w, WindowGrey, true, 1) == 0, "clear-pin: set_clear 1");
        x = 500;
        Frame();
        using (var img = s.Grab(w, P("on_again")))
            s.Check(IsBg(Px(img, 75, 125)) && IsOrange(Px(img, 525, 125)), $"clear-pin: Clear on removes the trail ({Px(img, 75, 125)}, {Px(img, 525, 125)})");
        Frame();
        var on2 = Best(3, 60);
        Console.WriteLine($"clear-pin: window Clear on again: gpu {on2.Gpu:F3} ms, render {on2.Render:F3} ms, {on2.Changes} changes / 60");
        // Clear on costs nothing extra, also after Clear was off: GPU and CPU render time as in the baseline (noise margin).
        PerfLimits.Below(s, on2.Gpu, on.Gpu * 1.5 + 0.5, $"clear-pin: Clear on again GPU {on2.Gpu:F3} ms vs baseline {on.Gpu:F3} ms (limit 1.5 x + 0.5 ms)");
        PerfLimits.Below(s, on2.Render, on.Render * 2 + 0.1, $"clear-pin: Clear on again CPU render {on2.Render:F3} ms vs baseline {on.Render:F3} ms (limit 2 x + 0.1 ms)", cpuBound: true);
        c0 = Native.vlqt_scene_get_change_count(w);
        Pump(30);
        long idleAgain = Native.vlqt_scene_get_change_count(w) - c0;
        s.Check(idleAgain == 0, $"clear-pin: idle with Clear on again renders nothing ({idleAgain} changes / 30 pumps)");

        // 7b) Alpha of Clear Color while Clear is off: the previous frame fades toward the clear colour by that alpha
        // per frame. 25 %: the first frame starts from the clear colour, older positions are darker than newer ones, and the
        // trail is gone after enough frames (up to the 8-bit residue).
        s.Check(SetMode(w, WindowGrey, false, 0.25f) == 0, "clear-pin: set_clear 0 (alpha 0.25)");
        x = 50;
        Frame();
        using (var img = s.Grab(w, P("alpha_first")))
        {
            int odd = Odd(img);
            s.Check(odd < 20 && IsOrange(Px(img, 75, 125)) && IsBg(Px(img, 600, 300)),
                $"clear-pin: alpha 0.25: first frame = clear colour + scene ({odd} odd pixels, {Px(img, 75, 125)}, {Px(img, 600, 300)})");
        }
        for (int i = 1; i <= 10; i++)
        {
            x = 50 + i * 40;
            Frame();
        }
        using (var img = s.Grab(w, P("alpha_025")))
        {
            int rOld = Px(img, 75, 125).R, rMid = Px(img, 275, 125).R, rNew = Px(img, 475, 125).R;
            Console.WriteLine($"clear-pin: alpha 0.25: old {rOld} / mid {rMid} / new {rNew}, background {Px(img, 600, 300).R}");
            layer(rOld < rMid && rMid < rNew && rNew > 240 && rOld > 32 && rOld < 70 && rMid > 50 && rMid < 140 && IsBg(Px(img, 600, 300)),
                $"clear-pin: alpha 0.25: older positions darker than newer ones (old {rOld} < mid {rMid} < new {rNew})");
        }
        x = 450;
        for (int i = 0; i < 40; i++) Frame();
        using (var img = s.Grab(w, P("alpha_025_gone")))
            layer(IsBg(Px(img, 75, 125)) && IsBg(Px(img, 275, 125)) && IsOrange(Px(img, 475, 125)) && Px(img, 75, 125).R <= 36,
                $"clear-pin: alpha 0.25: the trail is gone, the box stays ({Px(img, 75, 125)}, {Px(img, 275, 125)}, {Px(img, 475, 125)})");

        // Alpha changes at runtime keep the layer (a rebuilt layer would start from the clear colour and lose the trail).
        // 0: full strength.
        s.Check(SetClearColor(w, WindowGrey, 0) == 0, "clear-pin: alpha 0 at runtime");
        for (int i = 0; i <= 10; i++)
        {
            x = 50 + i * 40;
            Frame();
        }
        using (var img = s.Grab(w, P("alpha_0")))
            layer(IsOrange(Px(img, 75, 125)) && IsOrange(Px(img, 275, 125)) && IsOrange(Px(img, 475, 125)),
                $"clear-pin: alpha 0: the trail stays at full strength ({Px(img, 75, 125)}, {Px(img, 275, 125)}, {Px(img, 475, 125)})");
        // 0.25 on the existing trail: darker, not wiped.
        s.Check(SetClearColor(w, WindowGrey, 0.25f) == 0, "clear-pin: alpha 0.25 at runtime");
        Frame();
        using (var img = s.Grab(w, P("alpha_runtime")))
        {
            int rAfter = Px(img, 75, 125).R;
            layer(rAfter > 100 && rAfter < 250 && IsOrange(Px(img, 475, 125)),
                $"clear-pin: alpha 0.25 at runtime fades the existing trail without a rebuild (old {rAfter}, new {Px(img, 475, 125)})");
        }
        // Back to 0: the fade stops.
        s.Check(SetClearColor(w, WindowGrey, 0) == 0, "clear-pin: alpha 0 again");
        Frame();
        int rStop1, rStop2;
        using (var img = s.Grab(w, P("alpha_stop1")))
            rStop1 = Px(img, 75, 125).R;
        for (int i = 0; i < 6; i++) Frame();
        using (var img = s.Grab(w, P("alpha_stop2")))
            rStop2 = Px(img, 75, 125).R;
        layer(Math.Abs(rStop1 - rStop2) <= 2 && rStop2 > 32, $"clear-pin: alpha 0 again stops the fade (old {rStop1} -> {rStop2})");
        // 1: same pixels as Clear on, also with a moving box.
        s.Check(SetClearColor(w, WindowGrey, 1) == 0, "clear-pin: alpha 1 at runtime");
        for (int i = 0; i <= 10; i++)
        {
            x = 50 + i * 40;
            Frame();
        }
        using (var offImg = s.Grab(w, P("alpha_1")))
        {
            s.Check(SetMode(w, WindowGrey, true, 1) == 0, "clear-pin: set_clear 1 (after alpha 1)");
            Frame();
            using var onImg = s.Grab(w, P("alpha_1_clear_on"));
            int diff = Diff(offImg, onImg, 3);
            Console.WriteLine($"clear-pin: alpha 1 vs Clear on: {diff} differing pixels (every 2nd row and column)");
            s.Check(IsBg(Px(offImg, 75, 125)) && IsOrange(Px(offImg, 475, 125)) && diff == 0,
                $"clear-pin: alpha 1 gives the pixels of Clear on, no trail ({Px(offImg, 75, 125)}, {diff} differing pixels)");
        }

        // 8) MSAA: windows render with 4 samples, the layer inherits them. An opaque full-size black rectangle
        // under a rotated orange one stops the accumulation, so the edges show only the sampling: pixels between black
        // and orange along the edges, Clear on vs. Clear off.
        float angle = 0;
        Group Msaa()
        {
            var bg = new Rectangle { Size = new Vector2(8, 6), Anchor = RectangleAnchor.TopLeft, Fill = new Color4(0, 0, 0, 1) };
            var rot = new Rectangle { Size = new Vector2(2, 2), Anchor = RectangleAnchor.Center, Transform = Matrix.RotationZ(angle) * Matrix.Translation(4, 3, 0), Fill = new Color4(1, 0.6f, 0, 1) };
            return new Group { Anchor = RectangleAnchor.TopLeft, Children = [bg, rot] };
        }
        void MsaaFrame()
        {
            cmd.Clear();
            rec.Update(Msaa(), SpaceMapping.Default);
            if (cmd.Length > 0)
                s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"clear-pin: apply {Native.LastError()}");
            Pump(1);
        }
        int Edge(string name)
        {
            using var img = s.Grab(w, P(name));
            // Distinct red values between black and orange: n samples give n - 1 coverage steps, vertex AA many.
            var values = new HashSet<int>();
            for (int y = 0; y < img.Height; y += 1)
                for (int x = 0; x < img.Width; x++)
                {
                    var c = img.GetPixel(x, y);
                    if (c.R > 5 && c.R < 250)
                        values.Add(c.R);
                }
            return values.Count;
        }
        angle = 0.5f;
        var edges = new List<string>();
        var edgeValues = new List<int>();
        foreach (var (clear, name) in new[] { (true, "msaa_clear_on"), (false, "msaa_clear_off") })
        {
            SetMode(w, WindowGrey, clear, clear ? 1 : 0);
            for (int i = 0; i < 4; i++) MsaaFrame();
            var n = Edge(name);
            edgeValues.Add(n);
            edges.Add($"{name} {n}");
        }
        Console.WriteLine($"clear-pin: distinct edge values (n samples: n - 1): {string.Join(", ", edges)}");
        if (edgeValues[0] == 0 && !OperatingSystem.IsWindows())
            s.Skip("clear-pin: window MSAA edges with Clear off as with Clear on: Clear on shows no edge values (the GL window gets no MSAA buffer under llvmpipe/GLX although Qt reports 4 samples)");
        else
            layer(edgeValues[0] > 0 && edgeValues[0] == edgeValues[1], $"clear-pin: window MSAA edges with Clear off as with Clear on ({edgeValues[0]} vs {edgeValues[1]} distinct values)");
        SetMode(w, WindowGrey, true, 1);
        Frame();

        // 9) Cost, interleaved (single runs are noisy): 3 rounds of 120 frames each, Clear on / off, two window sizes. Also
        // toggles and resizes with the layer on and off (a resize after switching on crashed: lambda on the deleted view).
        foreach (var (sw, sh) in new[] { (800, 600), (1920, 1080) })
        {
            Native.vlqt_window_resize(w, sw, sh);
            Frame();
            var gpuOn = new List<double>();
            var gpuOff = new List<double>();
            var gpuFade = new List<double>();
            for (int round = 0; round < 3; round++)
            {
                SetMode(w, WindowGrey, true, 1);
                gpuOn.Add(Measure(120).Gpu);
                SetMode(w, WindowGrey, false, 0);
                gpuOff.Add(Measure(120).Gpu);
                // For comparison: alpha 0.25 draws the fill (alpha 0 draws nothing, so it costs no more than Clear off before the Clear pin).
                SetClearColor(w, WindowGrey, 0.25f);
                gpuFade.Add(Measure(120).Gpu);
            }
            string Ms(List<double> v) => string.Join(" / ", v.Select(m => m.ToString("F3")));
            Console.WriteLine($"clear-pin: cost {sw}x{sh}: gpu Clear on {Ms(gpuOn)} ms, off (alpha 0) {Ms(gpuOff)} ms, off (alpha 0.25) {Ms(gpuFade)} ms");
            // Documented cost of the layer: about +0.3 ms at 1920x1080, at worst +1 ms; best of the interleaved rounds.
            PerfLimits.Below(s, gpuOff.Min(), gpuOn.Min() * 1.5 + 1.0, $"clear-pin: cost {sw}x{sh}: Clear off (alpha 0) {gpuOff.Min():F3} ms vs on {gpuOn.Min():F3} ms (limit 1.5 x + 1 ms)");
        }
        SetMode(w, WindowGrey, true, 1);

        cmd.Clear();
        rec.Update(null, SpaceMapping.Default);
        if (cmd.Length > 0)
            Native.Apply(w, cmd.EndFrame());
        Native.vlqt_scene_destroy(w);
        Pump(1);
    }

    // Offscreen scene (QtTexture): 64 x 48, opaque grey clear colour, a 8 x 8 orange box moved along x.
    private static void Offscreen(Scenario s)
    {
        const int B8G8R8A8Typeless = 90, B8G8R8A8Unorm = 87;
        if (!check2(NativeTest.CreateDevice(0, out var device, out var context) == 0, "clear-pin offscreen: test device"))
            return;
        bool check2(bool ok, string what) { s.Check(ok, what); return ok; }
        int w = 64, h = 48;
        var scene = Native.vlqt_offscreen_create(w, h);
        var cmd = new CommandWriter();
        cmd.Clear();
        cmd.ClearColor(OffscreenGrey, OffscreenGrey, OffscreenGrey, 1);
        cmd.Create(1, Protocol.ElementType.Rectangle);
        cmd.SetPlace(1, 0, 4, 8, 8);
        cmd.SetColor(1, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(1, 0, 0);
        s.Check(Native.Apply(scene, cmd.EndFrame()) == 0, $"clear-pin offscreen: apply {Native.LastError()}");
        IntPtr tex = IntPtr.Zero;
        void Bind(int bw, int bh)
        {
            if (tex != IntPtr.Zero)
            {
                Native.vlqt_offscreen_unbind(scene);
                NativeTest.vlqt_test_com_release(tex);
            }
            NativeTest.vlqt_test_d3d11_create_shared_texture(device, bw, bh, B8G8R8A8Typeless, out tex, out var handle);
            var rc = Native.vlqt_offscreen_bind(scene, device, handle, bw, bh, B8G8R8A8Unorm, 1, out _);
            NativeTest.vlqt_test_close_handle(handle);
            s.Check(rc == 0, $"clear-pin offscreen: bind {bw}x{bh} ({rc}) {Native.LastError()}");
            (w, h) = (bw, bh);
        }
        int Render() => Native.OffscreenRenderBatch(context, new[] { scene }, 0);
        byte[] Read()
        {
            var px = new byte[w * h * 4];
            NativeCalls.ReadTexture(tex, px, out _, out _, out _);
            return px;
        }
        (int R, int G, int B, int A) At(byte[] px, int x, int y) { int i = (y * w + x) * 4; return (px[i + 2], px[i + 1], px[i], px[i + 3]); }
        void Move(float x)
        {
            cmd.Clear();
            cmd.SetPlace(1, x, 4, 8, 8);
            Native.Apply(scene, cmd.EndFrame());
        }

        Bind(64, 48);
        int r0 = Render();
        var pxOn = Read();
        Console.WriteLine($"clear-pin offscreen: before Clear off: rendered {r0}, box {At(pxOn, 4, 8)}, background {At(pxOn, 40, 30)}");
        s.Check(SetMode(scene, OffscreenGrey, false, 0) == 0, "clear-pin offscreen: set_clear 0 (alpha 0)");
        int first = Render();
        var px0 = Read();
        s.Check(At(px0, 4, 8).R == 255 && At(px0, 40, 30).R == 32 && At(px0, 40, 30).A == 0,
            $"clear-pin offscreen: first frame after Clear off = clear colour + box (rendered {first}, {At(px0, 4, 8)}, {At(px0, 40, 30)})");
        for (int i = 1; i <= 6; i++)
        {
            Move(i * 8);
            Render();
        }
        var px1 = Read();
        QtTextureTest.WritePng(Path.Combine(s.OutDir, "clear_pin_offscreen_trail.png"), px1, w, h);
        s.Check(At(px1, 4, 8).R == 255 && At(px1, 28, 8).R == 255 && At(px1, 52, 8).R == 255 && At(px1, 40, 30).R == 32,
            $"clear-pin offscreen: trail ({At(px1, 4, 8)}, {At(px1, 28, 8)}, {At(px1, 52, 8)}, background {At(px1, 40, 30)})");
        int idle = 0;
        for (int i = 0; i < 10; i++)
            idle += Render() > 0 ? 1 : 0;
        Console.WriteLine($"clear-pin offscreen: idle with Clear off: {idle} of 10 batches rendered");

        Bind(80, 40);
        Move(60);
        Render();
        var px2 = Read();
        QtTextureTest.WritePng(Path.Combine(s.OutDir, "clear_pin_offscreen_resize.png"), px2, w, h);
        int odd = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = At(px2, x, y);
                if (!(c.R == 32 && c.G == 32) && !(c.R == 255 && c.G > 140 && c.G < 165))
                    odd++;
            }
        s.Check(odd == 0 && At(px2, 4, 8).R == 32 && At(px2, 64, 8).R == 255,
            $"clear-pin offscreen: rebind with a new size clears the trail, no garbage ({odd} odd, {At(px2, 4, 8)}, {At(px2, 64, 8)})");
        // Once: accumulation goes on after the rebind.
        // The recursive layer allocates its second texture on the next frame, so the first frame after the rebind is dropped once
        // more: accumulation is stable from the third frame on (reported once, not a garbage frame).
        Move(60);
        Render();
        Move(68);
        Render();
        var px2b = Read();
        s.Check(At(px2b, 4, 8).R == 32 && At(px2b, 64, 8).R == 255 && At(px2b, 72, 8).R == 255,
            $"clear-pin offscreen: trail accumulates again after the rebind ({At(px2b, 4, 8)}, {At(px2b, 64, 8)}, {At(px2b, 72, 8)})");

        s.Check(SetMode(scene, OffscreenGrey, true, 1) == 0, "clear-pin offscreen: set_clear 1");
        Move(20);
        Render();
        var px3 = Read();
        s.Check(At(px3, 64, 8).R == 32 && At(px3, 24, 8).R == 255, $"clear-pin offscreen: Clear on removes the trail ({At(px3, 64, 8)}, {At(px3, 24, 8)})");

        // Fade: a full-size black rectangle below the box, 25 % alpha, fades trail and clear colour; the box (drawn after it) stays.
        s.Check(SetMode(scene, OffscreenGrey, false, 0) == 0, "clear-pin offscreen: set_clear 0 (fade, alpha 0)");
        Move(8);
        Render();
        Move(40);
        Render();
        cmd.Clear();
        cmd.Create(3, Protocol.ElementType.Rectangle);
        cmd.SetPlace(3, 0, 0, w, h);
        cmd.SetColor(3, Protocol.PropertyId.Color, 0, 0, 0, 0.25f);
        cmd.Reparent(3, 0, 0);
        s.Check(Native.Apply(scene, cmd.EndFrame()) == 0, $"clear-pin offscreen: apply fade {Native.LastError()}");
        for (int i = 0; i < 30; i++) Render();
        var pxFade = Read();
        QtTextureTest.WritePng(Path.Combine(s.OutDir, "clear_pin_offscreen_fade.png"), pxFade, w, h);
        s.Check(At(pxFade, 12, 8).R < 20 && At(pxFade, 70, 30).R < 10 && At(pxFade, 44, 8).R == 255,
            $"clear-pin offscreen: fade: trail and clear colour fade to black, the box stays ({At(pxFade, 12, 8)}, {At(pxFade, 70, 30)}, {At(pxFade, 44, 8)})");
        cmd.Clear();
        cmd.SetColor(3, Protocol.PropertyId.Color, 0, 0, 0, 0);
        Native.Apply(scene, cmd.EndFrame());
        s.Check(SetMode(scene, OffscreenGrey, true, 1) == 0, "clear-pin offscreen: set_clear 1 (after fade)");

        // Alpha of Clear Color while Clear is off, as in the window: 25 % fades older positions (box moved 7 times, 8 px
        // apart: oldest 6 frames, middle 3 frames old), then the trail is gone; changes at runtime keep the layer; alpha 1 = Clear on.
        s.Check(SetMode(scene, OffscreenGrey, false, 0.25f) == 0, "clear-pin offscreen: set_clear 0 (alpha 0.25)");
        Move(0);
        Render();
        var pa0 = Read();
        s.Check(At(pa0, 4, 8).R == 255 && At(pa0, 44, 8).R <= 34 && At(pa0, 40, 30).R is >= 30 and <= 33 && At(pa0, 40, 30).A == 255,
            $"clear-pin offscreen: alpha 0.25: first frame = clear colour + box ({At(pa0, 4, 8)}, {At(pa0, 44, 8)}, {At(pa0, 40, 30)})");
        for (int i = 1; i <= 6; i++)
        {
            Move(i * 8);
            Render();
        }
        var pa = Read();
        QtTextureTest.WritePng(Path.Combine(s.OutDir, "clear_pin_offscreen_alpha_025.png"), pa, w, h);
        int rOld = At(pa, 4, 8).R, rMid = At(pa, 28, 8).R, rNew = At(pa, 52, 8).R;
        Console.WriteLine($"clear-pin offscreen: alpha 0.25: old {rOld} / mid {rMid} / new {rNew}, background {At(pa, 40, 30).R}");
        s.Check(rOld is >= 60 and <= 85 && rMid is >= 110 and <= 140 && rNew == 255 && rOld < rMid,
            $"clear-pin offscreen: alpha 0.25: older positions darker than newer ones (old {rOld} < mid {rMid} < new {rNew})");
        Move(48);
        for (int i = 0; i < 40; i++) Render();
        var pg = Read();
        QtTextureTest.WritePng(Path.Combine(s.OutDir, "clear_pin_offscreen_alpha_025_gone.png"), pg, w, h);
        s.Check(At(pg, 4, 8).R <= 36 && At(pg, 28, 8).R <= 36 && At(pg, 52, 8).R == 255 && At(pg, 40, 30).R is >= 30 and <= 33,
            $"clear-pin offscreen: alpha 0.25: the trail is gone up to the 8-bit residue, the box stays ({At(pg, 4, 8)}, {At(pg, 28, 8)}, {At(pg, 52, 8)}, background {At(pg, 40, 30)})");
        // Runtime: 0 = full strength, 0.25 fades the existing trail without a rebuild, 0 again stops the fade.
        s.Check(SetClearColor(scene, OffscreenGrey, 0) == 0, "clear-pin offscreen: alpha 0 at runtime");
        for (int i = 0; i <= 6; i++)
        {
            Move(i * 8);
            Render();
        }
        var pb = Read();
        QtTextureTest.WritePng(Path.Combine(s.OutDir, "clear_pin_offscreen_alpha_0.png"), pb, w, h);
        s.Check(At(pb, 4, 8).R == 255 && At(pb, 28, 8).R == 255 && At(pb, 52, 8).R == 255,
            $"clear-pin offscreen: alpha 0: the trail stays at full strength ({At(pb, 4, 8)}, {At(pb, 28, 8)}, {At(pb, 52, 8)})");
        s.Check(SetClearColor(scene, OffscreenGrey, 0.25f) == 0, "clear-pin offscreen: alpha 0.25 at runtime");
        Render();
        int rRun = At(Read(), 4, 8).R;
        s.Check(rRun is >= 185 and <= 215, $"clear-pin offscreen: alpha 0.25 at runtime fades the existing trail by one step without a rebuild (old {rRun}, expected about 199)");
        s.Check(SetClearColor(scene, OffscreenGrey, 0) == 0, "clear-pin offscreen: alpha 0 again");
        Render();
        int rStop1 = At(Read(), 4, 8).R;
        for (int i = 0; i < 6; i++) Render();
        int rStop2 = At(Read(), 4, 8).R;
        s.Check(Math.Abs(rStop1 - rStop2) <= 1 && rStop2 > 32, $"clear-pin offscreen: alpha 0 again stops the fade (old {rStop1} -> {rStop2})");
        // 1: the pixels of Clear on, also with a moving box.
        s.Check(SetClearColor(scene, OffscreenGrey, 1) == 0, "clear-pin offscreen: alpha 1 at runtime");
        for (int i = 0; i <= 6; i++)
        {
            Move(i * 8);
            Render();
        }
        var pOne = Read();
        QtTextureTest.WritePng(Path.Combine(s.OutDir, "clear_pin_offscreen_alpha_1.png"), pOne, w, h);
        s.Check(SetMode(scene, OffscreenGrey, true, 1) == 0, "clear-pin offscreen: set_clear 1 (after alpha 1)");
        Move(48);
        Render();
        var pOn = Read();
        int bytesDiff = 0;
        for (int i = 0; i < pOne.Length; i++)
            if (Math.Abs(pOne[i] - pOn[i]) > 1)
                bytesDiff++;
        s.Check(bytesDiff == 0 && At(pOne, 4, 8).R == 32 && At(pOne, 52, 8).R == 255,
            $"clear-pin offscreen: alpha 1 gives the pixels of Clear on, no trail ({bytesDiff} differing bytes, {At(pOne, 4, 8)}, {At(pOne, 52, 8)})");

        // MSAA: the render target of a scene has 1 sample, the layer inherits it. A rotated white square on the grey clear
        // colour: distinct grey values on its edges, Clear on vs. Clear off.
        cmd.Clear();
        cmd.Create(2, Protocol.ElementType.Rectangle);
        cmd.SetPlace(2, 0, 0, 20, 20);
        cmd.SetColor(2, Protocol.PropertyId.Color, 1, 1, 1, 1);
        cmd.SetMat4(2, Protocol.PropertyId.Transform, Matrix.RotationZ(0.5f) * Matrix.Translation(40, 14, 0));
        cmd.Reparent(2, 0, 1);
        s.Check(Native.Apply(scene, cmd.EndFrame()) == 0, $"clear-pin offscreen: apply rotated {Native.LastError()}");
        int Steps()
        {
            var px = Read();
            var values = new HashSet<int>();
            for (int y = 0; y < h; y++)
                for (int x = 32; x < w; x++)
                {
                    var c = At(px, x, y);
                    if (c.R == c.G && c.G == c.B && c.R > 34 && c.R < 253)
                        values.Add(c.R);
                }
            return values.Count;
        }
        var steps = new List<string>();
        var stepValues = new List<int>();
        foreach (var (clear, name) in new[] { (true, "clear on"), (false, "clear off") })
        {
            SetMode(scene, OffscreenGrey, clear, clear ? 1 : 0);
            for (int i = 0; i < 3; i++) { Move(20 + i % 2); Render(); }
            var n = Steps();
            stepValues.Add(n);
            steps.Add($"{name} {n}");
        }
        Console.WriteLine($"clear-pin offscreen: distinct edge values (n samples: n - 1): {string.Join(", ", steps)}");
        s.Check(stepValues[0] == stepValues[1], $"clear-pin offscreen: MSAA edges with Clear off as with Clear on ({stepValues[0]} vs {stepValues[1]} distinct values)");

        Native.vlqt_offscreen_unbind(scene);
        NativeTest.vlqt_test_com_release(tex);
        Native.vlqt_scene_destroy(scene);
        NativeTest.vlqt_test_d3d11_release_device(device, context);
    }
}
