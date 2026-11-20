using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using NUnit.Framework;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// Phase A, tasks A2 / A2b: builtin:directional_blur / zoom_blur / radial_blur, checked through ShaderEffect. Each blur is
// two nested ShaderEffects of the same shader (pass templates in protocol.h): pass 1 (inner, id + 5) with
// the length, pass 2 (outer) with length / 64 (the sample spacing of pass 1 at the sample cap), which fills the gaps between
// the samples of pass 1. Raw commands (DIP). Grid of 3 rows (directional, zoom, radial) x 2 columns (small, large parameter
// set), cells 380 x 190 at (10 + 400 c, 5 + 200 r). Source per cell: Group with a transparent 380 x 190 rectangle (the frame
// = the effect area, so the blur has room beyond the content), text "MOTION" at the left, a white rectangle 100..160 right /
// +-25 around the cell centre (190, 95). texelSize = 1 / (380 x dpr, 190 x dpr) and dpr, set here (later by the pass driver of #489).
// Long lengths (directional 400 DIP, zoom 400 DIP, radial 90 degrees): cells 780 x 190, text only; compared with pass 2 off
// (= a single pass), the two passes must be much smoother (second difference along the blur direction).
// Checks: small leaves the surroundings of the shape dark, large smears into them in the direction of the effect (horizontal /
// tangential / along the rays) and not across; length 0 / angle 0 = the unblurred picture; the animated length does not step
// (mean over a block, 1 DIP steps); long lengths show no copies. Grabs: <outDir>/motion-blur_*.png.
[TestFixture, NonParallelizable]
public static class MotionBlurTest
{
    [Test, Category("motion-blur")]
    public static void MotionBlur() => Scenario.Run(Run);

    private const float CellW = 380, CellH = 190, LongW = 780, LongTextX = 410;
    private const float MaxSamples = 64; // maxSamples of the shaders: pass 2 gets length / MaxSamples
    private const uint Dir = 100, Zoom = 120, Radial = 140; // + 10 * column (cell: Fx, +1 Group, +2 Frame, +3 Text, +4 Shape)

    private static uint Fx(uint row, int col) => row + 10u * (uint)col;
    private static float CellX(int col) => 10 + 400 * col;
    private static float CellY(uint row) => 5 + 200 * ((row - Dir) / 20);
    private const uint LongId = 100; // + row: id of the long cell of a row

    private static Color Px(Bitmap a, float x, float y) => a.GetPixel((int)(x * a.Width / 800), (int)(y * a.Height / 600));
    private static bool IsWhite(Color c) => c.R > 235 && c.G > 235 && c.B > 235;
    // Brightness above the background (0 = background, 1 = white).
    private static double Lit(Color c) => (c.R - 0x20) / (255.0 - 0x20);

    // Mean Lit over a block (logical coordinates, step 2), so the jitter noise of single pixels does not count.
    private static double Mean(Bitmap a, float x0, float y0, float x1, float y1)
    {
        double sum = 0;
        int n = 0;
        for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2, n++)
                sum += Lit(Px(a, x, y));
        return sum / n;
    }

    // Mean second difference of Lit along paths in the direction of the blur (one physical pixel apart): near 0 for a smooth
    // profile, high for a comb of copies or noise. Paths: directional = rows, zoom = rays from the centre, radial = circles
    // around it, in the long cells (centre x = 400, text from x = 410).
    // The path is sampled bilinearly between pixels: with the nearest pixel, a circle or a slanted ray zigzags across the
    // pixel grid, and where it runs along a glyph edge (the arcs of the radial blur) that zigzag alone gives a second
    // difference as large as the copies, in both images, so the gain of pass 2 drowned in it at 1 pixel per DIP.
    private static double Rough(Bitmap a, uint row)
    {
        var scale = a.Width / 800.0; // physical pixels per DIP
        var step = 1 / scale; // 1 physical pixel in DIP
        var cx = 400.0;
        var cy = CellY(row) + CellH / 2;
        double sum = 0;
        int n = 0;
        double LitAt(double x, double y)
        {
            double px = x * scale - 0.5, py = y * scale - 0.5;
            int i = (int)Math.Floor(px), j = (int)Math.Floor(py);
            double fx = px - i, fy = py - j;
            double L(int u, int v) => Lit(a.GetPixel(Math.Clamp(u, 0, a.Width - 1), Math.Clamp(v, 0, a.Height - 1)));
            return (L(i, j) * (1 - fx) + L(i + 1, j) * fx) * (1 - fy) + (L(i, j + 1) * (1 - fx) + L(i + 1, j + 1) * fx) * fy;
        }
        void Along(Func<double, (double X, double Y)> path, double t0, double t1, double dt)
        {
            double Value(double t) { var (x, y) = path(t); return LitAt(x, y); }
            double v0 = Value(t0), v1 = Value(t0 + dt);
            for (var t = t0 + 2 * dt; t < t1; t += dt)
            {
                var v2 = Value(t);
                sum += Math.Abs(v0 - 2 * v1 + v2);
                n++;
                (v0, v1) = (v1, v2);
            }
        }
        if (row == Dir)
            for (var dy = -40; dy <= 40; dy += 8)
                Along(x => (x, cy + dy), 250, 700, step);
        else if (row == Zoom)
            for (var deg = -10.0; deg <= 10; deg += 2.5)
            {
                var d = deg * Math.PI / 180;
                Along(r => (cx + r * Math.Cos(d), cy + r * Math.Sin(d)), 40, 380, step);
            }
        else
            for (var r = 40; r <= 120; r += 20)
                Along(phi => (cx + r * Math.Cos(phi), cy + r * Math.Sin(phi)), -35 * Math.PI / 180, 35 * Math.PI / 180, step / r);
        return sum / n;
    }

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect needs a QRhi scenegraph");
        ulong Shader(string name)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader(id, "builtin:" + name, Protocol.ShaderStage.Fragment);
            s.Check(Native.vlqt_res_wait(id, 10000) == Protocol.ResState.Ready, $"shader builtin:{name} Ready");
            return id;
        }
        var directional = Shader("directional_blur");
        var zoom = Shader("zoom_blur");
        var radial = Shader("radial_blur");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt motion blur test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(4);
        float dpr; // device pixels per DIP: the layers are DIP size x dpr pixels, texelSize = 1 / that, the lengths stay in DIP
        using (var probe = s.Grab(w, "motion-blur_probe.png"))
            dpr = Math.Max(1f, probe.Width / 800f);
        s.Info($"motion-blur: device pixel ratio {dpr:F2}");
        var cmd = new CommandWriter();
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }

        // Uniforms per effect row: Directional (angle, length), Zoom (center, length), Radial (center, angle).
        void PassUniforms(uint id, uint row, float amount, float angle, float width)
        {
            cmd.BeginUniforms(id, Protocol.UniformSet.Clear);
            if (row == Dir)
            {
                cmd.UniformF32("angle", angle);
                cmd.UniformF32("blurLength", amount);
                cmd.UniformF32("dpr", dpr);
            }
            else if (row == Zoom)
            {
                cmd.UniformVec2("center", 0.5f, 0.5f);
                cmd.UniformF32("blurLength", amount);
                cmd.UniformF32("dpr", dpr);
            }
            else
            {
                cmd.UniformVec2("center", 0.5f, 0.5f);
                cmd.UniformF32("angle", amount);
            }
            cmd.UniformVec2("texelSize", 1 / (width * dpr), 1 / (CellH * dpr));
            cmd.EndUniforms();
        }
        // Both passes of a cell: pass 1 (id + 5) with the amount, pass 2 (id) with amount / 64; second = false: pass 2 off.
        void Uniforms(uint id, uint row, float amount, float angle = 0, float width = CellW, bool second = true)
        {
            PassUniforms(id + 5, row, amount, angle, width);
            PassUniforms(id, row, second ? amount / MaxSamples : 0, angle, width);
        }

        // Cell at (x, y): pass 2 (outer ShaderEffect, id) over pass 1 (inner, id + 5) over the Group (id + 1) with the frame
        // (id + 2), the text (id + 3) at textX and, with shape, the white rectangle (id + 4).
        void Cell(uint id, uint row, float x, float y, float width, float textX, ulong shader, float amount, float angle = 0, bool shape = true)
        {
            cmd.Create(id, Protocol.Type.ShaderEffect);
            cmd.SetPlace(id, x, y, width, CellH);
            cmd.SetU64(id, Protocol.Prop.FragmentShader, shader);
            cmd.Reparent(id, 0, id);
            cmd.Create(id + 5, Protocol.Type.ShaderEffect);
            cmd.SetPlace(id + 5, 0, 0, width, CellH);
            cmd.SetU64(id + 5, Protocol.Prop.FragmentShader, shader);
            cmd.Reparent(id + 5, id, 0);
            cmd.Create(id + 1, Protocol.Type.Item);
            cmd.SetI32(id + 1, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
            // transparent frame rectangle: the Group frame (and with it the effect area) is the whole cell
            cmd.Create(id + 2, Protocol.Type.Rectangle);
            cmd.SetColor(id + 2, Protocol.Prop.Color, 0, 0, 0, 0);
            cmd.SetPlace(id + 2, 0, 0, width, CellH);
            cmd.Reparent(id + 2, id + 1, 0);
            cmd.Create(id + 3, Protocol.Type.Text);
            cmd.SetString(id + 3, Protocol.Prop.Text, "MOTION");
            cmd.SetF32(id + 3, Protocol.Prop.FontSize, 44);
            cmd.SetColor(id + 3, Protocol.Prop.Color, 1, 1, 1, 1);
            cmd.SetI32(id + 3, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
            cmd.SetMat4(id + 3, Protocol.Prop.Transform, Stride.Core.Mathematics.Matrix.Translation(textX, 60, 0));
            cmd.Reparent(id + 3, id + 1, 1);
            if (shape)
            {
                cmd.Create(id + 4, Protocol.Type.Rectangle);
                cmd.SetColor(id + 4, Protocol.Prop.Color, 1, 1, 1, 1);
                cmd.SetPlace(id + 4, 290, 70, 60, 50);
                cmd.Reparent(id + 4, id + 1, 2);
            }
            cmd.Reparent(id + 1, id + 5, 0);
            cmd.SetRef(id + 5, Protocol.Prop.Source, id + 1);
            cmd.SetRef(id, Protocol.Prop.Source, id + 5);
            Uniforms(id, row, amount, angle, width);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        foreach (var (row, shader, small, large) in new[] { (Dir, directional, 6f, 60f), (Zoom, zoom, 8f, 60f), (Radial, radial, 4f, 40f) })
        {
            Cell(Fx(row, 0), row, CellX(0), CellY(row), CellW, 15, shader, small);
            Cell(Fx(row, 1), row, CellX(1), CellY(row), CellW, 15, shader, large);
        }
        CheckApply("motion blur scene");
        s.Pump(8);

        // Window coordinates of a point relative to the cell centre.
        float X(int col, float dx) => CellX(col) + CellW / 2 + dx;
        float Y(uint row, float dy) => CellY(row) + CellH / 2 + dy;

        // Rotation / zoom centre: compared with the unblurred picture below (the text may reach the centre, its width depends
        // on the system font).
        Color zoomCentre, radialCentre;
        using (var a = s.Grab(w, "motion-blur_all.png"))
        {
            // The shape itself (cell centre + 100..160, +-25) stays bright (box blur centred on the pixel); the large radial blur
            // smears the 50 DIP high shape over +-47 DIP, so only about half of it is left there.
            foreach (var row in new[] { Dir, Zoom, Radial })
                for (var col = 0; col < 2; col++)
                    gpu(Lit(Px(a, X(col, 130), Y(row, 0))) > (row == Radial && col == 1 ? 0.4 : 0.8),$"row {row} col {col}: shape middle still bright ({Px(a, X(col, 130), Y(row, 0))})");

            // Directional (angle 0): 20 DIP left of the shape (cell centre + 80): large smears in (1/6), small (length 6) does not.
            gpu(IsBg(Px(a, X(0, 80), Y(Dir, 0))), $"directional small: no smear 20 DIP beside the shape ({Px(a, X(0, 80), Y(Dir, 0))})");
            var dirMean = Mean(a, X(1, 76), Y(Dir, -10), X(1, 84), Y(Dir, 10));
            gpu(dirMean is > 0.08 and < 0.30, $"directional large (60): smear 20 DIP beside the shape ~1/6 ({dirMean:F3})");
            // not across the direction: 45 DIP above / below the shape stays dark
            gpu(IsBg(Px(a, X(1, 130), Y(Dir, -45))) && IsBg(Px(a, X(1, 130), Y(Dir, 45))), "directional angle 0: nothing smeared vertically");

            // Zoom (center 0.5/0.5): at cell centre + 175 the streak (60 DIP at the farthest corner) reaches the shape end (+160).
            gpu(IsBg(Px(a, X(0, 175), Y(Zoom, 0))), $"zoom small: no smear beyond the shape end ({Px(a, X(0, 175), Y(Zoom, 0))})");
            var zoomMean = Mean(a, X(1, 172), Y(Zoom, -4), X(1, 178), Y(Zoom, 4));
            gpu(zoomMean is > 0.05 and < 0.5, $"zoom large (60): streak beyond the shape end ({zoomMean:F3})");
            // radial streaks only: above the shape the zoom blur stays dark
            gpu(IsBg(Px(a, X(1, 130), Y(Zoom, -50))), $"zoom: nothing smeared sideways ({Px(a, X(1, 130), Y(Zoom, -50))})");
            zoomCentre = Px(a, X(1, 0), Y(Zoom, 0));

            // Radial: 20 DIP above / below the shape (cell centre + 130, -45 / +45): large (40 deg, +-20 deg ~ +-47 DIP at
            // radius 140) lights it, small (4 deg) not.
            gpu(IsBg(Px(a, X(0, 130), Y(Radial, -45))) && IsBg(Px(a, X(0, 130), Y(Radial, 45))),
                $"radial small: no smear next to the shape ({Px(a, X(0, 130), Y(Radial, -45))})");
            gpu(Lit(Px(a, X(1, 130), Y(Radial, -45))) > 0.1 && Lit(Px(a, X(1, 130), Y(Radial, 45))) > 0.1,
                $"radial large: arcs reach above and below the shape ({Px(a, X(1, 130), Y(Radial, -45))}, {Px(a, X(1, 130), Y(Radial, 45))})");
            radialCentre = Px(a, X(1, 0), Y(Radial, 0));
            // along the ray the radial blur does not smear: beyond the shape end it stays dark
            gpu(IsBg(Px(a, X(1, 180), Y(Radial, 0))), $"radial: nothing smeared along the ray ({Px(a, X(1, 180), Y(Radial, 0))})");
        }

        // Length 0 / angle 0 = unblurred: the shape edge (x = +100) is sharp in all rows.
        cmd.Reset();
        foreach (var row in new[] { Dir, Zoom, Radial })
            Uniforms(Fx(row, 1), row, 0);
        CheckApply("amount 0");
        s.Pump(4);
        using (var a = s.Grab(w, "motion-blur_zero.png"))
        {
            foreach (var row in new[] { Dir, Zoom, Radial })
                gpu(IsBg(Px(a, X(1, 95), Y(row, 0))) && IsWhite(Px(a, X(1, 105), Y(row, 0))),
                    $"row {row}: amount 0 = unblurred edge ({Px(a, X(1, 95), Y(row, 0))} | {Px(a, X(1, 105), Y(row, 0))})");
            // the large zoom / radial blur leaves its centre as it is
            static bool Same(Color p, Color q) => Math.Abs(p.R - q.R) < 8 && Math.Abs(p.G - q.G) < 8 && Math.Abs(p.B - q.B) < 8;
            var zoomZero = Px(a, X(1, 0), Y(Zoom, 0));
            var radialZero = Px(a, X(1, 0), Y(Radial, 0));
            gpu(Same(zoomCentre, zoomZero), $"zoom: centre unchanged ({zoomCentre} vs {zoomZero} unblurred)");
            gpu(Same(radialCentre, radialZero), $"radial: centre unchanged ({radialCentre} vs {radialZero} unblurred)");
        }

        // Animated directional length: block mean 10 DIP beside the shape, 1 DIP steps over 40..52 (sample count 20..26) grows
        // smoothly, no step (a count change must not show).
        var means = new double[13];
        for (var i = 0; i < means.Length; i++)
        {
            cmd.Reset();
            Uniforms(Fx(Dir, 1), Dir, 40 + i);
            CheckApply($"directional length {40 + i}");
            s.Pump(3);
            using var a = s.Grab(w, "motion-blur_anim.png");
            means[i] = Mean(a, X(1, 85), Y(Dir, -10), X(1, 95), Y(Dir, 10));
        }
        double maxStep = 0;
        var monotonic = true;
        for (var i = 1; i < means.Length; i++)
        {
            maxStep = Math.Max(maxStep, Math.Abs(means[i] - means[i - 1]));
            monotonic &= means[i] >= means[i - 1] - 0.015; // one sample more or less moves a block mean by 1 / n at most
        }
        s.Info($"motion-blur: animation means {string.Join(" ", Array.ConvertAll(means, m => m.ToString("F3")))}");
        gpu(maxStep < 0.03 && monotonic, $"animated length 40..52: no stepping (max change {maxStep:F4} per DIP, monotonic {monotonic})");

        // Long lengths: directional 400 DIP, zoom 400 DIP, radial 90 degrees on text, cells 780 x 190 (centre at 400, text from
        // 410). First with pass 2 off (a single pass, 64 samples: copies), then the two passes. The second difference along
        // the rows must drop clearly; the smear reaches far and stays inside the directions of the effects.
        cmd.Reset();
        foreach (var row in new[] { Dir, Zoom, Radial })
            for (var col = 0; col < 2; col++)
                cmd.Destroy(Fx(row, col));
        CheckApply("destroy short cells");
        s.Pump(2);
        var longAmount = new Dictionary<uint, float> { [Dir] = 400, [Zoom] = 400, [Radial] = 90 };
        cmd.Reset();
        foreach (var (row, shader) in new[] { (Dir, directional), (Zoom, zoom), (Radial, radial) })
            Cell(LongId + row, row, 10, CellY(row), LongW, LongTextX, shader, longAmount[row], shape: false);
        CheckApply("long cells");
        cmd.Reset();
        for (var row = Dir; row <= Radial; row += 20) Uniforms(LongId + row, row, longAmount[row], width: LongW, second: false);
        CheckApply("long single pass");
        s.Pump(6);
        var rough = new Dictionary<uint, double>();
        using (var a = s.Grab(w, "motion-blur_long_single.png"))
            foreach (var row in new[] { Dir, Zoom, Radial })
                rough[row] = Rough(a, row);
        cmd.Reset();
        foreach (var row in new[] { Dir, Zoom, Radial })
            Uniforms(LongId + row, row, longAmount[row], width: LongW);
        CheckApply("long two passes");
        s.Pump(6);
        using (var a = s.Grab(w, "motion-blur_long.png"))
        {
            foreach (var row in new[] { Dir, Zoom, Radial })
            {
                var two = Rough(a, row);
                s.Info($"motion-blur: row {row} long: second difference single pass {rough[row]:F4}, two passes {two:F4}");
                // zoom and radial keep the real structure of the glyphs along the path (arcs, edges), and at 1 pixel per DIP
                // the inner radii stay below 64 pixels of arc (no gaps for pass 2 to fill), so the gain is smaller
                var factor = row == Dir ? 3.0 : 1.25;
                gpu(two < rough[row] / factor, $"row {row} long: two passes at least {factor}x as smooth as one ({rough[row]:F4} -> {two:F4})");
            }
            gpu(Lit(Px(a, 10 + 400 - 150, CellY(Dir) + 95)) > 0.005 && IsBg(Px(a, 10 + 100, CellY(Dir) + 95)) && IsBg(Px(a, 10 + 760, CellY(Dir) + 95)),
                $"directional 400: smear reaches far, nothing beyond ({Px(a, 250, CellY(Dir) + 95)}, {Px(a, 110, CellY(Dir) + 95)})");
            var arcMean = Mean(a, 400 + 60, CellY(Radial) + 95 - 70, 400 + 100, CellY(Radial) + 95 - 40);
            gpu(arcMean > 0.01, $"radial 90: arcs reach 35 degrees up from the text ({arcMean:F3})");
        }

        cmd.Reset();
        foreach (var row in new[] { Dir, Zoom, Radial })
            cmd.Destroy(LongId + row);
        CheckApply("destroy");
        s.Pump(3);
        Native.vlqt_window_destroy(w);
        Native.vlqt_res_release(directional);
        Native.vlqt_res_release(zoom);
        Native.vlqt_res_release(radial);
        s.Pump(2);
    }
}
