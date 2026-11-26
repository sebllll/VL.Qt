using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// Glow (A1): blur pyramid as nested ShaderEffects, pass templates in protocol.h.
// glow_prefilter (1/2) -> pyramid_down (1/4 .. 1/2^n) -> pyramid_up (.. 1/2, base = the down step of that size)
// -> glow_composite (full size, element + glow). The pass graph is fixed, the radius only changes the weights.
// Source: transparent 800 x 600 rectangle (room for the glow) with an orange box 160 x 100 at (320, 250) and a white
// bar 6 x 100 at (200, 250). Raw commands (DIP). Grabs: <outDir>/glow_*.png; the radius sweep goes to the log.
[TestFixture, NonParallelizable]
public static class GlowTest
{
    [Test, Category("glow")]
    public static void Glow() => Scenario.Run(Run);

    private const uint Fx = 1, Src = 2, Box = 3, Bar = 4;
    private const uint Down = 100, Up = 200; // + step k (Down + k is 1 / 2^k of the effect size)

    private static bool Near(Color c, int r, int g, int b, int tol = 14) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    // Glow strength at (x, y): mean green above the background #202020 over 5 x 5 samples (1 DIP apart).
    private static float Glow(Bitmap a, int x, int y)
    {
        float sum = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                sum += Px(a, x + dx, y + dy).G - 0x20;
        return sum / 25;
    }

    // Pyramid step of a radius (DIP): sigma = radius / 2 in pixels; n steps down and up blur with sigma^2 = 7/6 (4^n - 1) px^2.
    private static float Level(float radius, float dpr)
    {
        var s = radius * dpr / 2;
        return 0.5f * MathF.Log2(1 + s * s * 6 / 7);
    }
    private static float RadiusOf(float level, float dpr) => 2 / dpr * MathF.Sqrt((MathF.Pow(4, level) - 1) * 7 / 6);

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "Shader needs a QRhi scenegraph");
        ulong Shader(string path)
        {
            Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var id);
            Native.vlqt_shader_load(id, path, Protocol.ShaderStage.Fragment);
            s.Check(Native.vlqt_res_wait(id, 10000) == Protocol.ResourceState.Ready, $"shader {path} Ready");
            return id;
        }
        var prefilter = Shader("builtin:glow_prefilter");
        var down = Shader("builtin:pyramid_down");
        var up = Shader("builtin:pyramid_up");
        var composite = Shader("builtin:glow_composite");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt glow test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(4);
        float dpr;
        using (var probe = s.Grab(w, "glow_probe.png"))
            dpr = Math.Max(1f, probe.Width / 800f);

        // Step sizes in pixels (each the ceiling of half the one above); steps up to the one radius 256 needs, at least 2 px.
        var px = new List<(int W, int H)> { ((int)MathF.Round(800 * dpr), (int)MathF.Round(600 * dpr)) };
        var n = (int)MathF.Ceiling(Level(256, dpr));
        for (int k = 1; k <= n && px[k - 1].H >= 4; k++)
            px.Add(((px[k - 1].W + 1) / 2, (px[k - 1].H + 1) / 2));
        n = px.Count - 1;
        s.Info($"glow: device pixel ratio {dpr:F2}, {n} steps, smallest {px[n].W} x {px[n].H} px");

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
            int len = NativeCalls.Poll(w, evBuf);
            for (int at = 0; at < len;)
            {
                ushort ev = BitConverter.ToUInt16(evBuf, at), l = (ushort)BitConverter.ToUInt32(evBuf, at + 4);
                if (ev == Protocol.EventId.Error)
                    errors.Add($"{BitConverter.ToUInt32(evBuf, at + 8)}: {Encoding.UTF8.GetString(evBuf, at + 12, l - 4)}");
                at += (8 + l + 3) & ~3;
            }
        }
        void Rect(uint id, float x, float y, float rw, float rh, float r, float g, float b, float a)
        {
            cmd.Create(id, Protocol.ElementType.Rectangle);
            cmd.SetColor(id, Protocol.PropertyId.Color, r, g, b, a);
            cmd.SetPlace(id, x, y, rw, rh);
        }
        void Pass(uint id, int k, ulong shader, uint source)
        {
            cmd.Create(id, Protocol.ElementType.Shader);
            cmd.SetPlace(id, 0, 0, px[k].W / dpr, px[k].H / dpr);
            cmd.SetBool(id, Protocol.PropertyId.AutoSize, false); // SIZE counts: with Input the size would follow the Input (full size)
            cmd.SetU64(id, Protocol.PropertyId.FragmentShader, shader);
            cmd.SetRef(id, Protocol.PropertyId.Source, source);
            cmd.Bind(id, id);
        }
        uint UpOrDown(int k) => k == n ? Down + (uint)k : Up + (uint)k; // the top of the chain at step k

        // Glow color cyan; the whole parameter set goes to the composite, the up steps get their weight.
        const float cr = 0.1f, cg = 0.8f, cb = 1f;
        void SetGlow(float radius, float spread = 0, float intensity = 1.5f, bool additive = false)
        {
            var level = Level(radius, dpr);
            cmd.BeginUniforms(Fx, Protocol.UniformSet.Clear);
            cmd.UniformVec2("texelSize", 1f / px[1].W, 1f / px[1].H);
            cmd.UniformF32("weight", Math.Clamp(level, 0, 1));
            cmd.UniformF32("spread", spread);
            cmd.UniformF32("intensity", intensity);
            cmd.UniformBool("additive", additive);
            cmd.UniformColor("color", cr, cg, cb, 1);
            cmd.UniformRef("glow", UpOrDown(1));
            cmd.EndUniforms();
            for (int k = 1; k < n; k++)
            {
                cmd.BeginUniforms(Up + (uint)k, 0);
                cmd.UniformF32("weight", Math.Clamp(level - k, 0, 1));
                cmd.EndUniforms();
            }
        }

        cmd.Clear();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        Rect(Src, 0, 0, 800, 600, 0, 0, 0, 0);
        Rect(Box, 320, 250, 160, 100, 1, 0.6f, 0, 1);
        Rect(Bar, 200, 250, 6, 100, 1, 1, 1, 1);
        cmd.Reparent(Box, Src, 0);
        cmd.Reparent(Bar, Src, 1);
        // Down chain: Down+1 = prefilter of the element, Down+k from Down+k-1.
        for (int k = 1; k <= n; k++)
        {
            Pass(Down + (uint)k, k, k == 1 ? prefilter : down, k == 1 ? Src : Down + (uint)k - 1);
            cmd.BeginUniforms(Down + (uint)k, Protocol.UniformSet.Clear);
            cmd.UniformVec2("texelSize", 1f / px[k - 1].W, 1f / px[k - 1].H);
            if (k == 1) cmd.UniformColor("color", cr, cg, cb, 1);
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
        cmd.Create(Fx, Protocol.ElementType.Shader);
        cmd.SetPlace(Fx, 0, 0, 800, 600);
        cmd.SetU64(Fx, Protocol.PropertyId.FragmentShader, composite);
        cmd.SetRef(Fx, Protocol.PropertyId.Source, Src);
        cmd.Bind(Fx, Fx);
        // Tree (ownership only, the effects draw their children through layers): Fx -> Src, top of the up chain ->
        // ... -> Down+n -> ... -> Down+1.
        cmd.Reparent(Src, Fx, 0);
        cmd.Reparent(UpOrDown(1), Fx, 1);
        for (int k = 1; k < n; k++)
            cmd.Reparent(UpOrDown(k + 1), Up + (uint)k, 0);
        for (int k = 1; k < n; k++)
            cmd.Reparent(Down + (uint)k, Down + (uint)k + 1, 0);
        cmd.Reparent(Fx, 0, 1000);
        SetGlow(4);
        CheckApply("glow scene");
        s.Pump(8);

        // Points right of the box (edge x = 480, y = 300) at a distance of 3 / 10 / 25 / 40 / 100 / 200 DIP, and its centre.
        const int y0 = 300;
        Bitmap Shot(string name, float radius, float spread = 0, float intensity = 1.5f, bool additive = false)
        {
            cmd.Clear();
            SetGlow(radius, spread, intensity, additive);
            CheckApply($"glow {name}");
            s.Pump(4);
            var a = s.Grab(w, $"glow_{name}.png");
            s.Info($"glow {name}: d3 {Glow(a, 483, y0):F1}, d10 {Glow(a, 490, y0):F1}, d25 {Glow(a, 505, y0):F1}, d40 {Glow(a, 520, y0):F1}, " +
                $"d100 {Glow(a, 580, y0):F1}, d200 {Glow(a, 680, y0):F1}, box {Px(a, 400, y0)}");
            return a;
        }

        using (var a = Shot("r4", 4))
        {
            gpu(Glow(a, 483, y0) > 6 && Glow(a, 490, y0) < 3 && Glow(a, 520, y0) < 1,
                $"radius 4: glow at the edge only ({Glow(a, 483, y0):F1}, {Glow(a, 490, y0):F1}, {Glow(a, 520, y0):F1})");
            gpu(Near(Px(a, 400, y0), 255, 153, 0), $"glow behind the element: box unchanged ({Px(a, 400, y0)})");
            gpu(Glow(a, 209, y0) > 6, $"thin bar glows too ({Glow(a, 209, y0):F1})");
        }
        using (var a = Shot("r32", 32))
            gpu(Glow(a, 490, y0) > 20 && Glow(a, 505, y0) > 5 && Glow(a, 580, y0) < 1,
                $"radius 32: glow reaches ~30 DIP, not 100 ({Glow(a, 490, y0):F1}, {Glow(a, 505, y0):F1}, {Glow(a, 580, y0):F1})");
        using (var a = Shot("r256", 256))
        {
            gpu(Glow(a, 580, y0) > 8 && Glow(a, 680, y0) > 2,
                $"radius 256: glow reaches 200 DIP ({Glow(a, 580, y0):F1}, {Glow(a, 680, y0):F1})");
            gpu(Near(Px(a, 400, y0), 255, 153, 0), $"radius 256: box unchanged ({Px(a, 400, y0)})");
            // Rings in the far field (x 520 .. 780 DIP): runs of equal values, 18.9 px without the dither of the passes, ~6 with.
            var cy = y0 * a.Height / 600;
            var band = InnerShadowTest.Banding(a, 520 * a.Width / 800, 780 * a.Width / 800, new[] { cy - 10, cy, cy + 10 }, c => c.G);
            s.Info($"glow banding: radius 256 far field, mean run {band:F1} px");
            gpu(band < 10, $"radius 256: no rings in the glow (mean run of equal values {band:F1} px)");
        }
        using (var a = Shot("r0", 0))
            gpu(Glow(a, 483, y0) < 1, $"radius 0 behind the element: nothing outside ({Glow(a, 483, y0):F1})");

        // Spread, intensity, additive at radius 32.
        float g25, g10;
        using (var a = Shot("r32_i1", 32, intensity: 1))
        {
            g25 = Glow(a, 505, y0);
            g10 = Glow(a, 490, y0);
        }
        using (var a = Shot("r32_spread", 32, spread: 0.6f, intensity: 1))
            gpu(Glow(a, 505, y0) > g25 * 1.5f + 3, $"spread 0.6 makes the glow denser ({g25:F1} -> {Glow(a, 505, y0):F1})");
        using (var a = Shot("r32_i3", 32, intensity: 3))
            gpu(Glow(a, 490, y0) > g10 * 2 + 2, $"intensity 3 brighter than 1 ({g10:F1} -> {Glow(a, 490, y0):F1})");
        using (var a = Shot("r32_additive", 32, additive: true))
        {
            var c = Px(a, 400, y0);
            gpu(c.G > 153 + 40 && c.B > 60, $"additive: glow added on top of the box ({c})");
        }

        // Radius sweep across two step boundaries (step 4.5 .. 6.5 in 0.1): every value changes in small steps, no jump
        // (the ramp of d10 runs ~3.5 per step, a quarter of its range at most).
        var points = new[] { (Name: "d10", X: 490), (Name: "d40", X: 520), (Name: "d100", X: 580) };
        var rows = new List<float[]>();
        for (int i = 0; i <= 20; i++)
        {
            var level = 4.5f + i * 0.1f;
            var radius = RadiusOf(level, dpr);
            cmd.Clear();
            SetGlow(radius);
            CheckApply("glow sweep");
            s.Pump(3);
            using var a = s.Grab(w, "glow_sweep.png");
            var v = points.Select(p => Glow(a, p.X, y0)).ToArray();
            rows.Add(v);
            s.Info($"glow sweep: step {level:F1} radius {radius,6:F1} DIP: " + string.Join(", ", points.Select((p, j) => $"{p.Name} {v[j]:F1}")));
        }
        for (int j = 0; j < points.Length; j++)
        {
            var col = rows.Select(r => r[j]).ToArray();
            var range = col.Max() - col.Min();
            var maxStep = col.Zip(col.Skip(1), (a, b) => Math.Abs(b - a)).Max();
            gpu(maxStep <= Math.Max(0.25f * range, 3), $"sweep {points[j].Name}: no jump (largest step {maxStep:F1} of range {range:F1})");
        }
        var far = rows.Select(r => r[2]).ToArray();
        gpu(far.Zip(far.Skip(1), (a, b) => b - a).All(d => d > -1.5f) && far[^1] > far[0] + 5,
            $"sweep d100 grows with the radius ({far[0]:F1} -> {far[^1]:F1})");

        DrainErrors();
        s.Check(errors.Count == 0, $"no EVENT_ERROR from the passes ({string.Join("; ", errors)})");

        cmd.Clear();
        cmd.Destroy(Fx);
        CheckApply("destroy");
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        foreach (var id in new[] { prefilter, down, up, composite }) Native.vlqt_res_release(id);
        s.Pump(2);
    }
}