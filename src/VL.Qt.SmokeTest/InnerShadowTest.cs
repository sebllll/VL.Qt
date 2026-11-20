using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// InnerShadow (A4): the blur pyramid of Glow (pyramid_down / pyramid_up), pass templates in protocol.h.
// innershadow_prefilter (1/2: inverted alpha, shifted) -> pyramid_down (1/4 .. 1/2^n)
// -> pyramid_up (.. 1/2) -> innershadow_composite (full size, element + shadow inside its alpha). The pass graph is
// fixed, radius / offset / spread only change uniforms.
// Source: transparent 800 x 600 group (room for the shadow) with an orange rectangle 200 x 120 at (300, 240) and the
// white text "Hg" (160, bold), shown one at a time. Raw commands (DIP). Grabs: <outDir>/innershadow_*.png.
[TestFixture, NonParallelizable]
public static class InnerShadowTest
{
    [Test, Category("innershadow")]
    public static void InnerShadow() => Scenario.Run(Run);

    private const uint Fx = 1, Src = 2, Rect = 3, Txt = 4;
    private const uint Down = 100, Up = 200; // + step k (Down + k is 1 / 2^k of the effect size)
    private const int Left = 300, Top = 240, Right = 500, Bottom = 360, Cy = 300; // the rectangle, DIP

    private static bool Near(Color c, int r, int g, int b, int tol = 14) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    // Shadow strength at (x, y) on the orange rectangle (R 255): mean red loss over (2 half + 1)^2 samples (1 DIP apart).
    private static float Shade(Bitmap a, int x, int y, int half = 2)
    {
        float sum = 0;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
                sum += 255 - Px(a, x + dx, y + dy).R;
        return sum / ((2 * half + 1) * (2 * half + 1));
    }

    // Shade d DIP inside the left / right / top / bottom edge of the rectangle, measured the same way on all four sides:
    // in device pixels, from the pixel at the edge (the first inside pixel is d = 0 on every side, so a fractional device
    // pixel ratio samples left and right alike), (2 half + 1)^2 pixels around the one d DIP inside.
    private static float ShadePx(Bitmap a, int cx, int cy, int half)
    {
        float sum = 0;
        for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
                sum += 255 - a.GetPixel(cx + dx, cy + dy).R;
        return sum / ((2 * half + 1) * (2 * half + 1));
    }
    private static int Dev(Bitmap a, int dip) => (int)MathF.Round(dip * a.Width / 800f);
    private static float EdgeL(Bitmap a, int d, int half = 2) => ShadePx(a, Dev(a, Left) + Dev(a, d), Dev(a, Cy), half);
    private static float EdgeR(Bitmap a, int d, int half = 2) => ShadePx(a, Dev(a, Right) - 1 - Dev(a, d), Dev(a, Cy), half);
    private static float EdgeT(Bitmap a, int d, int half = 2) => ShadePx(a, Dev(a, 400), Dev(a, Top) + Dev(a, d), half);
    private static float EdgeB(Bitmap a, int d, int half = 2) => ShadePx(a, Dev(a, 400), Dev(a, Bottom) - 1 - Dev(a, d), half);

    // Mirrored edges read alike: the passes sample on half-resolution texel centres, a shifted pass shows as one side
    // stronger than the other (the passes once ran at full size, 179 against 130 of 255 at radius 4).
    private static bool Symmetric(float a, float b) => Math.Abs(a - b) <= 0.08f * Math.Max(a, b) + 2;

    // Sum of red over the text box (white text on #202020): drops as the shadow darkens the glyphs.
    private static long Ink(Bitmap a)
    {
        long sum = 0;
        for (int y = 200 * a.Height / 600; y < 400 * a.Height / 600; y++)
            for (int x = 200 * a.Width / 800; x < 600 * a.Width / 800; x++)
                sum += a.GetPixel(x, y).R;
        return sum;
    }

    // Banding of a smooth gradient = long runs of the same value (rings); a dither breaks them up into runs of a few
    // pixels. Takes one channel on a few rows and returns the mean run length, weighted by pixels (the run a pixel
    // sits in). Undithered 8-bit layers: ~10 - 19 px in the gradients of the tests, dithered ~5 - 6 px.
    internal static float Banding(Bitmap a, int x0, int x1, int[] rows, Func<Color, int> channel)
    {
        long sum = 0, total = 0;
        foreach (var y in rows)
        {
            int run = 1, last = channel(a.GetPixel(x0, y));
            for (int x = x0 + 1; x <= x1; x++)
            {
                var v = x < x1 ? channel(a.GetPixel(x, y)) : -1;
                if (v == last)
                {
                    run++;
                    continue;
                }
                sum += (long)run * run;
                total += run;
                run = 1;
                last = v;
            }
        }
        return (float)sum / total;
    }

    // Pyramid step of a radius (DIP): sigma = radius / 2 in pixels; n steps down and up blur with sigma^2 = 7/6 (4^n - 1) px^2.
    private static float Level(float radius, float dpr)
    {
        var s = radius * dpr / 2;
        return 0.5f * MathF.Log2(1 + s * s * 6 / 7);
    }

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect needs a QRhi scenegraph");
        ulong Shader(string path)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader(id, path, Protocol.ShaderStage.Fragment);
            s.Check(Native.vlqt_res_wait(id, 10000) == Protocol.ResState.Ready, $"shader {path} Ready");
            return id;
        }
        var prefilter = Shader("builtin:innershadow_prefilter");
        var down = Shader("builtin:pyramid_down");
        var up = Shader("builtin:pyramid_up");
        var composite = Shader("builtin:innershadow_composite");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt innershadow test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(4);
        float dpr;
        using (var probe = s.Grab(w, "innershadow_probe.png"))
            dpr = Math.Max(1f, probe.Width / 800f);

        // Step sizes in pixels (each the ceiling of half the one above); steps up to the one radius 256 needs, at least 2 px.
        var px = new List<(int W, int H)> { ((int)MathF.Round(800 * dpr), (int)MathF.Round(600 * dpr)) };
        var n = (int)MathF.Ceiling(Level(256, dpr));
        for (int k = 1; k <= n && px[k - 1].H >= 4; k++)
            px.Add(((px[k - 1].W + 1) / 2, (px[k - 1].H + 1) / 2));
        n = px.Count - 1;
        s.Info($"innershadow: device pixel ratio {dpr:F2}, {n} steps, smallest {px[n].W} x {px[n].H} px");

        var cmd = new CommandWriter();
        var evBuf = new byte[1 << 16];
        var errors = new List<string>();
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void DrainErrors()
        {
            int len = Native.Poll(w, evBuf);
            for (int at = 0; at < len;)
            {
                ushort ev = BitConverter.ToUInt16(evBuf, at), l = BitConverter.ToUInt16(evBuf, at + 2);
                if (ev == Protocol.Ev.Error)
                    errors.Add($"{BitConverter.ToUInt32(evBuf, at + 4)}: {Encoding.UTF8.GetString(evBuf, at + 12, l - 8)}");
                at += (4 + l + 3) & ~3;
            }
        }
        void Pass(uint id, int k, ulong shader, uint source)
        {
            cmd.Create(id, Protocol.Type.ShaderEffect);
            cmd.SetPlace(id, 0, 0, px[k].W / dpr, px[k].H / dpr);
            cmd.SetBool(id, Protocol.Prop.AutoSize, false); // SIZE counts: with Input the size would follow the Input (full size)
            cmd.SetU64(id, Protocol.Prop.FragmentShader, shader);
            cmd.SetRef(id, Protocol.Prop.Source, source);
            cmd.Bind(id, id, 0);
        }
        uint UpOrDown(int k) => k == n ? Down + (uint)k : Up + (uint)k; // the top of the chain at step k

        // Shadow color black with the given alpha; offset in DIP goes to the passes as a share of the effect area.
        void SetShadow(float radius, float ox = 0, float oy = 0, float spread = 0, float intensity = 1, float alpha = 1)
        {
            var level = Level(radius, dpr);
            cmd.BeginUniforms(Down + 1, 0);
            cmd.UniformVec2("offset", ox / 800, oy / 600);
            cmd.UniformColor("color", 0, 0, 0, alpha);
            cmd.EndUniforms();
            cmd.BeginUniforms(Fx, Protocol.UniformSet.Clear);
            cmd.UniformVec2("texelSize", 1f / px[1].W, 1f / px[1].H);
            cmd.UniformVec2("offset", ox / 800, oy / 600);
            cmd.UniformF32("weight", Math.Clamp(level, 0, 1));
            cmd.UniformF32("spread", spread);
            cmd.UniformF32("intensity", intensity);
            cmd.UniformColor("color", 0, 0, 0, alpha);
            cmd.UniformRef("shadow", UpOrDown(1));
            cmd.EndUniforms();
            for (int k = 1; k < n; k++)
            {
                cmd.BeginUniforms(Up + (uint)k, 0);
                cmd.UniformF32("weight", Math.Clamp(level - k, 0, 1));
                cmd.EndUniforms();
            }
        }
        void Show(bool text)
        {
            cmd.SetF32(Rect, Protocol.Prop.Opacity, text ? 0 : 1);
            cmd.SetF32(Txt, Protocol.Prop.Opacity, text ? 1 : 0);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Src, Protocol.Type.Rectangle);
        cmd.SetColor(Src, Protocol.Prop.Color, 0, 0, 0, 0);
        cmd.SetPlace(Src, 0, 0, 800, 600);
        cmd.Create(Rect, Protocol.Type.Rectangle);
        cmd.SetColor(Rect, Protocol.Prop.Color, 1, 0.6f, 0, 1);
        cmd.SetPlace(Rect, Left, Top, Right - Left, Bottom - Top);
        cmd.Create(Txt, Protocol.Type.Text);
        cmd.SetPlace(Txt, 200, 200, 400, 200);
        cmd.SetString(Txt, Protocol.Prop.Text, "Hg");
        cmd.SetF32(Txt, Protocol.Prop.FontSize, 160);
        cmd.SetI32(Txt, Protocol.Prop.FontWeight, 900);
        cmd.SetColor(Txt, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.SetI32(Txt, Protocol.Prop.HorizontalAlignment, 4);
        cmd.SetI32(Txt, Protocol.Prop.VerticalAlignment, 128);
        cmd.Reparent(Rect, Src, 0);
        cmd.Reparent(Txt, Src, 1);
        // Down chain: Down+1 = prefilter of the element, Down+k from Down+k-1.
        for (int k = 1; k <= n; k++)
        {
            Pass(Down + (uint)k, k, k == 1 ? prefilter : down, k == 1 ? Src : Down + (uint)k - 1);
            cmd.BeginUniforms(Down + (uint)k, Protocol.UniformSet.Clear);
            cmd.UniformVec2("texelSize", 1f / px[k - 1].W, 1f / px[k - 1].H);
            if (k == 1)
            {
                cmd.UniformVec2("offset", 0, 0);
                cmd.UniformColor("color", 0, 0, 0, 1);
            }
            cmd.EndUniforms();
        }
        // Up chain: Up+k from the step below (Up+k+1, at the bottom Down+n), base Down+k.
        for (int k = n - 1; k >= 1; k--)
        {
            Pass(Up + (uint)k, k, up, UpOrDown(k + 1));
            cmd.BeginUniforms(Up + (uint)k, Protocol.UniformSet.Clear);
            cmd.UniformVec2("texelSize", 1f / px[k + 1].W, 1f / px[k + 1].H);
            cmd.UniformF32("weight", 0);
            cmd.UniformRef("base", Down + (uint)k);
            cmd.EndUniforms();
        }
        cmd.Create(Fx, Protocol.Type.ShaderEffect);
        cmd.SetPlace(Fx, 0, 0, 800, 600);
        cmd.SetU64(Fx, Protocol.Prop.FragmentShader, composite);
        cmd.SetRef(Fx, Protocol.Prop.Source, Src);
        cmd.Bind(Fx, Fx, 0);
        // Tree (ownership only, the effects draw their children through layers): Fx -> Src, top of the up chain ->
        // ... -> Down+n -> ... -> Down+1.
        cmd.Reparent(Src, Fx, 0);
        cmd.Reparent(UpOrDown(1), Fx, 1);
        for (int k = 1; k < n; k++)
            cmd.Reparent(UpOrDown(k + 1), Up + (uint)k, 0);
        for (int k = 1; k < n; k++)
            cmd.Reparent(Down + (uint)k, Down + (uint)k + 1, 0);
        cmd.Reparent(Fx, 0, 1000);
        Show(false);
        SetShadow(4);
        CheckApply("innershadow scene");
        s.Pump(8);

        Bitmap Shot(string name, bool text, float radius, float ox = 0, float oy = 0, float spread = 0, float intensity = 1, float alpha = 1)
        {
            cmd.Reset();
            Show(text);
            SetShadow(radius, ox, oy, spread, intensity, alpha);
            CheckApply($"innershadow {name}");
            s.Pump(4);
            var a = s.Grab(w, $"innershadow_{name}.png");
            if (!text)
                s.Info($"innershadow {name}: left d3 {EdgeL(a, 3):F1}, d10 {EdgeL(a, 10):F1}, d25 {EdgeL(a, 25):F1}, " +
                    $"d60 {EdgeL(a, 60):F1}, centre {Shade(a, 400, Cy):F1}, right d3 {EdgeR(a, 3):F1}, " +
                    $"top d3 {EdgeT(a, 3):F1}, bottom d3 {EdgeB(a, 3):F1}");
            return a;
        }
        bool OutsideUnchanged(Bitmap a) => Near(Px(a, Left - 6, Cy), 32, 32, 32, 4) && Near(Px(a, Right + 6, Cy), 32, 32, 32, 4)
            && Near(Px(a, 400, Top - 6), 32, 32, 32, 4) && Near(Px(a, 400, Bottom + 6), 32, 32, 32, 4);

        // Rectangle, offset 0, radius 4 / 32 / 128.
        using (var a = Shot("rect_r4", false, 4))
        {
            // Radius 4 DIP is sigma 2 px at dpr 1: the shadow is gone ~4 px in, so the edge is measured on the first DIP
            // (3 x 3 window at d1; d3 reads ~10 at dpr 1 and ~17 at dpr 1.5).
            gpu(EdgeL(a, 1, 1) > 20 && Shade(a, Left + 10, Cy) < 3 && Shade(a, Left + 25, Cy) < 1,
                $"radius 4: shadow at the edge only ({EdgeL(a, 1, 1):F1}, {Shade(a, Left + 10, Cy):F1}, {Shade(a, Left + 25, Cy):F1})");
            gpu(Near(Px(a, 400, Cy), 255, 153, 0), $"radius 4: centre of the element unchanged ({Px(a, 400, Cy)})");
            gpu(OutsideUnchanged(a), "radius 4: nothing outside the element");
            gpu(EdgeR(a, 1, 1) > 20 && EdgeT(a, 1, 1) > 20 && EdgeB(a, 1, 1) > 20,
                $"radius 4: all four edges ({EdgeR(a, 1, 1):F1}, {EdgeT(a, 1, 1):F1}, {EdgeB(a, 1, 1):F1})");
            gpu(Symmetric(EdgeL(a, 1, 1), EdgeR(a, 1, 1)) && Symmetric(EdgeT(a, 1, 1), EdgeB(a, 1, 1)) && Symmetric(EdgeL(a, 1, 1), EdgeT(a, 1, 1)),
                $"radius 4: edges symmetric, left/right {EdgeL(a, 1, 1):F1} / {EdgeR(a, 1, 1):F1}, top/bottom {EdgeT(a, 1, 1):F1} / {EdgeB(a, 1, 1):F1}");
        }
        using (var a = Shot("rect_r32", false, 32))
        {
            gpu(Shade(a, Left + 3, Cy) > 60 && Shade(a, Left + 25, Cy) > 5 && Shade(a, Left + 60, Cy) < 3,
                $"radius 32: shadow reaches ~30 DIP inwards, not 60 ({Shade(a, Left + 3, Cy):F1}, {Shade(a, Left + 25, Cy):F1}, {Shade(a, Left + 60, Cy):F1})");
            gpu(OutsideUnchanged(a), "radius 32: nothing outside the element");
            gpu(Symmetric(EdgeL(a, 3), EdgeR(a, 3)) && Symmetric(EdgeT(a, 3), EdgeB(a, 3)) && Symmetric(EdgeL(a, 10), EdgeR(a, 10)) && Symmetric(EdgeT(a, 10), EdgeB(a, 10)),
                $"radius 32: edges symmetric, d3 {EdgeL(a, 3):F1} / {EdgeR(a, 3):F1} and {EdgeT(a, 3):F1} / {EdgeB(a, 3):F1}, d10 {EdgeL(a, 10):F1} / {EdgeR(a, 10):F1} and {EdgeT(a, 10):F1} / {EdgeB(a, 10):F1}");
        }
        using (var a = Shot("rect_r128", false, 128))
        {
            var centre = Shade(a, 400, Cy);
            gpu(centre > 40 && Shade(a, Left + 3, Cy) > centre,
                $"radius 128: shadow reaches the centre ({centre:F1}), strongest at the edge ({Shade(a, Left + 3, Cy):F1})");
            gpu(OutsideUnchanged(a), "radius 128: nothing outside the element");
        }
        using (var a = Shot("rect_r0", false, 0))
            gpu(Shade(a, Left + 3, Cy) < 1 && Near(Px(a, Left + 1, Cy), 255, 153, 0, 40), $"radius 0: nothing inside ({Shade(a, Left + 3, Cy):F1})");

        // Offset (8, 8): the shadow moves with the hole, left and top edges get it, right and bottom do not.
        using (var a = Shot("rect_r4_o8", false, 4, 8, 8))
        {
            gpu(Shade(a, Left + 3, Cy) > 150 && Shade(a, 400, Top + 3) > 150 && Shade(a, Right - 3, Cy) < 3 && Shade(a, 400, Bottom - 3) < 3,
                $"offset (8, 8): left/top inside ({Shade(a, Left + 3, Cy):F1}, {Shade(a, 400, Top + 3):F1}), right/bottom not ({Shade(a, Right - 3, Cy):F1}, {Shade(a, 400, Bottom - 3):F1})");
            gpu(OutsideUnchanged(a), "offset (8, 8): nothing outside the element");
        }
        using (var a = Shot("rect_r32_o8", false, 32, 8, 8))
        {
            gpu(Shade(a, Left + 3, Cy) > Shade(a, Right - 3, Cy) + 40 && Shade(a, 400, Top + 3) > Shade(a, 400, Bottom - 3) + 40,
                $"radius 32 offset (8, 8): left/top stronger than right/bottom ({Shade(a, Left + 3, Cy):F1} / {Shade(a, Right - 3, Cy):F1}, {Shade(a, 400, Top + 3):F1} / {Shade(a, 400, Bottom - 3):F1})");
            gpu(OutsideUnchanged(a), "radius 32 offset (8, 8): nothing outside the element");
        }
        using (var a = Shot("rect_r128_o8", false, 128, 8, 8))
            gpu(OutsideUnchanged(a), "radius 128 offset (8, 8): nothing outside the element");

        // Spread and intensity at radius 32.
        float s25, s10;
        using (var a = Shot("rect_r32_i1", false, 32))
        {
            s25 = Shade(a, Left + 25, Cy);
            s10 = Shade(a, Left + 10, Cy);
        }
        using (var a = Shot("rect_r32_spread", false, 32, spread: 0.6f))
            gpu(Shade(a, Left + 25, Cy) > s25 * 1.5f + 3, $"spread 0.6 makes the shadow denser ({s25:F1} -> {Shade(a, Left + 25, Cy):F1})");
        using (var a = Shot("rect_r32_i05", false, 32, intensity: 0.5f))
            gpu(Shade(a, Left + 10, Cy) < s10 * 0.7f, $"intensity 0.5 weaker ({s10:F1} -> {Shade(a, Left + 10, Cy):F1})");

        // Text, radius 0 / 4 / 32 / 128, offset 0 and (8, 8): the glyphs get darker with the radius, outside stays empty.
        long inkPrev;
        using (var a = Shot("text_r0", true, 0))
        {
            inkPrev = Ink(a);
            s.Info($"innershadow text: ink r0 {inkPrev}");
        }
        foreach (var r in new[] { 4, 32, 128 })
        {
            using var a = Shot($"text_r{r}", true, r);
            var ink = Ink(a);
            s.Info($"innershadow text: ink r{r} {ink}");
            gpu(ink < inkPrev, $"text radius {r}: glyphs darker than before ({inkPrev} -> {ink})");
            gpu(Near(Px(a, 100, 100), 32, 32, 32, 4) && Near(Px(a, 700, 500), 32, 32, 32, 4), $"text radius {r}: background clean");
            inkPrev = ink;
        }
        foreach (var r in new[] { 4, 32, 128 })
        {
            using var a = Shot($"text_r{r}_o8", true, r, 8, 8);
            gpu(Near(Px(a, 100, 100), 32, 32, 32, 4) && Near(Px(a, 700, 500), 32, 32, 32, 4), $"text radius {r} offset (8, 8): background clean");
        }

        // Banding: the soft shadow (alpha 0.2) on the rectangle is a flat gradient.
        using (var a = Shot("rect_r128_soft", false, 128, alpha: 0.2f))
        {
            var cy = Cy * a.Height / 600;
            var band = Banding(a, (Left + 4) * a.Width / 800, (Left + 96) * a.Width / 800, new[] { cy - 10, cy, cy + 10 }, c => c.R);
            s.Info($"innershadow banding: soft shadow, mean run {band:F1} px");
            gpu(band < 8, $"soft shadow without rings (mean run of equal values {band:F1} px)");
        }

        DrainErrors();
        s.Check(errors.Count == 0, $"no EV_ERROR from the passes ({string.Join("; ", errors)})");

        cmd.Reset();
        cmd.Destroy(Fx);
        CheckApply("destroy");
        s.Pump(3);
        Native.vlqt_window_destroy(w);
        foreach (var id in new[] { prefilter, down, up, composite }) Native.vlqt_res_release(id);
        s.Pump(2);
    }
}
