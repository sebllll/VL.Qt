using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using VL.Qt;

// README pictures of the Effect chain (#489, plan effects/08 Task 8a), not a test of behaviour: renders reproducible grabs
// of effect chains (raw SET_EFFECT_CHAIN) and copies them to docs/images/. Dark ground, the text "VL.Qt", one cell of
// 410 x 230 DIP per picture (scaled by s, the label at the bottom), same values in the collage and in the single pictures.
//   effect-chain.png            Blur -> RGBSplit -> DropShadow (left) against the reverse order (right); RGBSplit baked from
//                               help/Qt/qtshaders/RGBSplit.frag, padding from its "@padding" line
//   effect-glow / -directional-blur / -zoom-blur / -radial-blur / -innershadow / -displace .png
//                               one effect each, as links with the builtin shaders (values as in EffectPassesTest/MotionBlurTest)
//   effects-collage.png         all of them as a 4 x 2 grid, 1640 px wide
// Checks only that the picture is not empty. Grabs: <outDir>/readme-images_*.png. On Windows the pictures are scaled to
// their target width (the grab is in device pixels), elsewhere copied as they are.
static class ReadmeImagesTest
{
    const float CellW = 410, CellH = 230;
    const float TextW = 300, TextH = 120, FontSize = 90;
    const float Bg = 0x16 / 255f;

    const string WaveMap =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; };\n" +
        "void main() {\n" +
        "    vec2 uv = qt_TexCoord0;\n" +
        "    fragColor = vec4(0.5 + 0.5 * sin(uv.y * 12.566), 0.5 + 0.5 * sin(uv.x * 12.566), 0.5, 1.0) * qt_Opacity;\n" +
        "}\n";

    internal static string? FindUp(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p) || Directory.Exists(p))
                return p;
        }
        return null;
    }

    // Copies a grab to its target; on Windows scaled to the target width (the grab is in device pixels).
    internal static string Save(string grabPath, string target, int width)
    {
#if WINDOWS
        using (var a = new Bitmap(grabPath))
        {
            var height = (int)MathF.Round(a.Height * width / (float)a.Width);
            using var scaled = new Bitmap(width, height);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(a, 0, 0, width, height);
            }
            scaled.Save(target, System.Drawing.Imaging.ImageFormat.Png);
        }
#else
        File.Copy(grabPath, target, true);
#endif
        return target;
    }

    // Share of the sampled pixels that differ from the ground: > 0 = something is drawn.
    internal static double Ink(Bitmap a)
    {
        int n = 0, ink = 0;
        for (var y = 0; y < a.Height; y += 6)
            for (var x = 0; x < a.Width; x += 6, n++)
            {
                var c = a.GetPixel(x, y);
                if (Math.Abs(c.R - 0x16) + Math.Abs(c.G - 0x16) + Math.Abs(c.B - 0x16) > 60)
                    ink++;
            }
        return ink / (double)Math.Max(n, 1);
    }

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        Program.NeedsRhi(check, "the effects are ShaderEffects");
        Directory.CreateDirectory(outDir);
        var imagesDir = FindUp("docs", "images");
        check(imagesDir != null, $"readme-images: docs/images found ({imagesDir})");
        if (imagesDir == null) return;

        ulong Builtin(string name)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader(id, "builtin:" + name, Protocol.ShaderStage.Fragment);
            check(Native.vlqt_res_wait(id, 10000) == Protocol.ResState.Ready, $"readme-images: shader builtin:{name} Ready");
            return id;
        }
        ulong Baked(string code, string what)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader_source(id, code, Protocol.ShaderStage.Fragment);
            check(Native.vlqt_res_wait(id, 20000) == Protocol.ResState.Ready, $"readme-images: {what} baked, Ready");
            return id;
        }
        var glowPre = Builtin("glow_prefilter");
        var glowComp = Builtin("glow_composite");
        var isPre = Builtin("innershadow_prefilter");
        var isComp = Builtin("innershadow_composite");
        var directional = Builtin("directional_blur");
        var zoom = Builtin("zoom_blur");
        var radial = Builtin("radial_blur");
        var displace = Builtin("displace");
        var waveMap = Baked(WaveMap, "wave map");
        var splitFile = FindUp("help", "Qt", "qtshaders", "RGBSplit.frag");
        check(splitFile != null, $"readme-images: RGBSplit.frag found ({splitFile})");
        if (splitFile == null) return;
        var splitCode = File.ReadAllText(splitFile);
        var split = Baked(splitCode, "RGBSplit.frag");
        var splitPad = float.Parse(Regex.Match(splitCode, @"//\s*@padding\s+([\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        var cmd = new CommandWriter();
        uint next = 1;

        // Links (DIP values scaled by s)
        static void Blur(CommandWriter c, float s)
        {
            c.BeginMultiLink();
            c.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
            c.EffectF32(Protocol.Prop.EffectBlur, 0.22f);
            c.EffectI32(Protocol.Prop.EffectBlurMax, (int)MathF.Round(16 * s));
            c.EndLink();
        }
        static void Shadow(CommandWriter c, float s)
        {
            c.BeginMultiLink();
            c.EffectBool(Protocol.Prop.EffectShadowEnabled, true);
            c.EffectColor(Protocol.Prop.EffectShadowColor, 1, 0.55f, 0.2f, 0.9f);
            c.EffectF32(Protocol.Prop.EffectShadowHorizontalOffset, 16 * s);
            c.EffectF32(Protocol.Prop.EffectShadowVerticalOffset, 16 * s);
            c.EffectF32(Protocol.Prop.EffectShadowBlur, 0.15f);
            c.EndLink();
        }
        void Split(CommandWriter c, float s)
        {
            var p = splitPad * s;
            c.BeginShaderLink(Protocol.EffectTemplate.Single, [split], p, p, p, p, 0, "");
            c.UniformF32("amount", 0.022f);
            c.UniformVec2("direction", 1, 0);
            c.UniformColor("tint", 1, 1, 1, 1);
            c.UniformBool("scanlines", false);
            c.EndLink();
        }

        // One cell at (ox, oy): ground, label, the text "VL.Qt" with the effect chain of the kind.
        void Cell(string kind, string label, float ox, float oy, float s, uint order)
        {
            uint ground = next++, text = next++, labelId = next++, map = next++;
            cmd.Create(ground, Protocol.Type.Rectangle);
            cmd.SetPlace(ground, ox + 5 * s, oy + 5 * s, (CellW - 10) * s, (CellH - 10) * s);
            cmd.SetColor(ground, Protocol.Prop.Color, 0.105f, 0.115f, 0.14f, 1);
            cmd.SetF32(ground, Protocol.Prop.Radius, 10 * s);
            cmd.Reparent(ground, 0, order * 3);

            cmd.Create(labelId, Protocol.Type.Text);
            cmd.SetPlace(labelId, ox + 22 * s, oy + (CellH - 42) * s, (CellW - 44) * s, 26 * s);
            cmd.SetString(labelId, Protocol.Prop.Text, label);
            cmd.SetF32(labelId, Protocol.Prop.FontSize, 15 * s);
            cmd.SetColor(labelId, Protocol.Prop.Color, 0.52f, 0.57f, 0.66f, 1);
            cmd.SetI32(labelId, Protocol.Prop.HorizontalAlignment, 1);
            cmd.SetI32(labelId, Protocol.Prop.VerticalAlignment, 128);
            cmd.Reparent(labelId, 0, order * 3 + 1);

            cmd.Create(text, Protocol.Type.Text);
            cmd.SetPlace(text, ox + (CellW - TextW) / 2 * s, oy + 28 * s, TextW * s, TextH * s);
            cmd.SetString(text, Protocol.Prop.Text, "VL.Qt");
            cmd.SetF32(text, Protocol.Prop.FontSize, FontSize * s);
            cmd.SetI32(text, Protocol.Prop.FontWeight, 700);
            cmd.SetI32(text, Protocol.Prop.HorizontalAlignment, 4);
            cmd.SetI32(text, Protocol.Prop.VerticalAlignment, 128);
            cmd.SetColor(text, Protocol.Prop.Color, 0.94f, 0.93f, 0.9f, 1);
            cmd.Reparent(text, 0, order * 3 + 2);

            void Pyramid(ulong pre, ulong comp, float radius, Action<CommandWriter, float> uniforms)
            {
                var p = MathF.Ceiling(1.5f * radius * s);
                cmd.BeginEffectChain(text);
                cmd.BeginShaderLink(Protocol.EffectTemplate.Pyramid, [pre, comp], p, p, p, p, radius * s, "");
                uniforms(cmd, p);
                cmd.EndLink();
                cmd.EndEffectChain();
            }
            switch (kind)
            {
                case "chain-forward":
                case "chain-reverse":
                    cmd.BeginEffectChain(text);
                    if (kind == "chain-forward") { Blur(cmd, s); Split(cmd, s); Shadow(cmd, s); }
                    else { Shadow(cmd, s); Split(cmd, s); Blur(cmd, s); }
                    cmd.EndEffectChain();
                    break;
                case "glow":
                    Pyramid(glowPre, glowComp, 26, (c, p) =>
                    {
                        c.UniformColor("color", 0.15f, 0.78f, 1, 1);
                        c.UniformF32("spread", 0.15f);
                        c.UniformF32("intensity", 1.6f);
                        c.UniformBool("additive", false);
                    });
                    break;
                case "innershadow":
                    cmd.SetColor(text, Protocol.Prop.Color, 1, 0.66f, 0.3f, 1);
                    Pyramid(isPre, isComp, 9, (c, p) =>
                    {
                        c.UniformVec2("offset", 0, 7 * s / (TextH * s + 2 * p));
                        c.UniformColor("color", 0.25f, 0.05f, 0, 1);
                        c.UniformF32("spread", 0.3f);
                        c.UniformF32("intensity", 1.4f);
                    });
                    break;
                case "directional":
                {
                    const float L = 14;
                    var pad = (L / 2 + L / 64) * s;
                    cmd.BeginEffectChain(text);
                    cmd.BeginShaderLink(Protocol.EffectTemplate.TwoPass, [directional], pad, 0, pad, 0, L * s, "length");
                    cmd.UniformF32("angle", 0);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
                }
                case "zoom":
                {
                    const float L = 40;
                    var pad = L * 0.6f * s;
                    cmd.BeginEffectChain(text);
                    cmd.BeginShaderLink(Protocol.EffectTemplate.TwoPass, [zoom], pad, pad, pad, pad, L * s, "length");
                    cmd.UniformVec2("center", 0.5f, 0.5f);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
                }
                case "radial":
                {
                    var pad = 30 * s;
                    cmd.BeginEffectChain(text);
                    cmd.BeginShaderLink(Protocol.EffectTemplate.TwoPass, [radial], pad, pad, pad, pad, 8, "angle");
                    cmd.UniformVec2("center", 0.5f, 0.5f);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
                }
                case "displace":
                    // The map is never reparented: it only serves as texture of the link.
                    cmd.Create(map, Protocol.Type.ShaderEffect);
                    cmd.SetU64(map, Protocol.Prop.FragmentShader, waveMap);
                    cmd.SetPlace(map, 0, 0, TextW * s, TextH * s);
                    cmd.BeginEffectChain(text);
                    cmd.BeginShaderLink(Protocol.EffectTemplate.Single, [displace], 0, 0, 0, 0, 0, "");
                    cmd.UniformF32("displacement", 7 * s);
                    cmd.UniformRef("displacementSource", map);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
            }
        }

        void Render(string name, int width, float winW, float winH, params (string kind, string label)[] cells)
        {
            cmd.Reset();
            next = 1;
            var w = Native.vlqt_window_create((int)MathF.Round(winW), (int)MathF.Round(winH), "VL.Qt readme images");
            check(w != IntPtr.Zero, $"readme-images {name}: window_create");
            Native.vlqt_window_show(w);
            pump(4);
            cmd.ClearColor(Bg, Bg, Bg, 1);
            var cols = cells.Length > 2 ? 4 : cells.Length;
            var s = winW / (cols * CellW);
            uint order = 0;
            foreach (var (kind, label) in cells)
            {
                Cell(kind, label, order % (uint)cols * CellW * s, order / (uint)cols * CellH * s, s, order);
                order++;
            }
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"readme-images {name}: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
            pump(10);
            var grabPath = Path.Combine(outDir, $"readme-images_{name}.png");
            using (var a = grab(w, grabPath))
            {
                var ink = Ink(a);
                check(ink > 0.02, $"readme-images {name}: picture not empty (ink {ink:P1}, {a.Width} x {a.Height})");
            }
            var target = Save(grabPath, Path.Combine(imagesDir!, name + ".png"), width);
            Console.WriteLine($"INFO readme-images: {target} ({new FileInfo(target).Length / 1024} KB)");
            Native.vlqt_window_destroy(w);
        }

        const string forward = "Blur → RGBSplit → DropShadow", reverse = "DropShadow → RGBSplit → Blur";
        var glow = ("glow", "Glow");
        var dir = ("directional", "Directional Blur");
        var zm = ("zoom", "Zoom Blur");
        var rad = ("radial", "Radial Blur");
        var inner = ("innershadow", "InnerShadow");
        var disp = ("displace", "Displace");

        Render("effect-chain", 1000, 1000, CellH * 1000 / (2 * CellW), ("chain-forward", forward), ("chain-reverse", reverse));
        Render("effect-glow", 820, 820, CellH * 2, glow);
        Render("effect-directional-blur", 820, 820, CellH * 2, dir);
        Render("effect-zoom-blur", 820, 820, CellH * 2, zm);
        Render("effect-radial-blur", 820, 820, CellH * 2, rad);
        Render("effect-innershadow", 820, 820, CellH * 2, inner);
        Render("effect-displace", 820, 820, CellH * 2, disp);
        Render("effects-collage", 1640, 4 * CellW * 0.8f, 2 * CellH * 0.8f, ("chain-forward", forward), ("chain-reverse", reverse), glow, dir, zm, rad, inner, disp);
    }
}
