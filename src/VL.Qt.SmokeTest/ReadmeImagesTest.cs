using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// Effect chain pictures (plan effects/08 Task 8a): renders reproducible grabs of effect chains (raw SET_EFFECT_CHAIN)
// and compares them with src/VL.Qt.SmokeTest/references/ (ImageCompare, on WARP in a helper process; TEST-03); the
// explicit test `update-refs` writes the references. docs/images is not touched. Dark ground, the text "VL.Qt", one cell of
// 410 x 230 DIP per picture (scaled by s, the label at the bottom), same values in the collage and in the single pictures.
//   effect-chain.png            Blur -> RGBSplit -> DropShadow (left) against the reverse order (right); RGBSplit baked from
//                               help/Qt/qtshaders/RGBSplit.frag, padding = its amount (DIP)
//   effect-glow / -directional-blur / -zoom-blur / -radial-blur / -innershadow / -displace .png
//                               one effect each, as links with the builtin shaders (values as in EffectPassesTest/MotionBlurTest)
//   effects-collage.png         all of them as a 4 x 2 grid, 1640 px wide
// Grabs: <outDir>/readme-images_*.png (a failed compare also writes readme-images_<name>_diff.png). The pictures are scaled
// to their target width before the compare (the grab is in device pixels). Text uses the bundled DejaVu Sans.
[TestFixture, NonParallelizable]
public static class ReadmeImagesTest
{
    /// <summary>Effect chain pictures against the references (WARP, own process).</summary>
    [Test, Category("readme-images"), Category("image-compare"), Category("own-process")]
    public static void ReadmeImages() => Scenario.RunLocal(s => ImageCompare.Run(s, new[] { "readme-images" }));

    /// <summary>Writes the README pictures into docs/images on the normal renderer (not the references).</summary>
    [Test, Explicit("writes docs/images"), Category("readme-docs"), Category("own-process")]
    public static void ReadmeImagesDocs() => Scenario.RunLocal(s => ImageCompare.RunDocs(s, new[] { "readme-images" }));

    private const float CellW = 410, CellH = 230;
    private const float TextW = 300, TextH = 120, FontSize = 90;
    private const float Bg = 0x16 / 255f;

    private const string WaveMap =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; };\n" +
        "void main() {\n" +
        "    vec2 uv = qt_TexCoord0;\n" +
        "    fragColor = vec4(0.5 + 0.5 * sin(uv.y * 12.566), 0.5 + 0.5 * sin(uv.x * 12.566), 0.5, 1.0) * qt_Opacity;\n" +
        "}\n";

    internal static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "the effects are ShaderEffects");
        Directory.CreateDirectory(s.OutDir);
        var family = ImageCompare.LoadFont(s);

        ulong Builtin(string name)
        {
            Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var id);
            Native.vlqt_shader_load(id, "builtin:" + name, Protocol.ShaderStage.Fragment);
            s.Check(Native.vlqt_res_wait(id, 10000) == Protocol.ResourceState.Ready, $"readme-images: shader builtin:{name} Ready");
            return id;
        }
        ulong Baked(string code, string what)
        {
            Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var id);
            Native.vlqt_shader_load_source(id, code, Protocol.ShaderStage.Fragment);
            s.Check(Native.vlqt_res_wait(id, 20000) == Protocol.ResourceState.Ready, $"readme-images: {what} baked, Ready");
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
        var splitFile = ImageCompare.FindUp("help", "Qt", "qtshaders", "RGBSplit.frag");
        s.Check(splitFile != null, $"readme-images: RGBSplit.frag found ({splitFile})");
        if (splitFile == null) return;
        var splitCode = File.ReadAllText(splitFile);
        var split = Baked(splitCode, "RGBSplit.frag");
        s.Check(Regex.IsMatch(splitCode, @"//\s*@padding\s+amount\b"), "readme-images: RGBSplit.frag takes its padding from the uniform amount");
        const float splitAmount = 8; // DIP, the distance of the shifted copies (also the padding, as in the file head)

        var cmd = new CommandWriter();
        uint next = 1;

        // Links (DIP values scaled by s)
        static void Blur(CommandWriter c, float s)
        {
            c.BeginMultiLink();
            c.MultiEffectBool(Protocol.MultiEffectKey.BlurEnabled, true);
            c.MultiEffectF32(Protocol.MultiEffectKey.Blur, 0.22f);
            c.MultiEffectI32(Protocol.MultiEffectKey.BlurMax, (int)MathF.Round(16 * s));
            c.EndLink();
        }
        static void Shadow(CommandWriter c, float s)
        {
            c.BeginMultiLink();
            c.MultiEffectBool(Protocol.MultiEffectKey.ShadowEnabled, true);
            c.MultiEffectColor(Protocol.MultiEffectKey.ShadowColor, 1, 0.55f, 0.2f, 0.9f);
            c.MultiEffectF32(Protocol.MultiEffectKey.ShadowHorizontalOffset, 16 * s);
            c.MultiEffectF32(Protocol.MultiEffectKey.ShadowVerticalOffset, 16 * s);
            c.MultiEffectF32(Protocol.MultiEffectKey.ShadowBlur, 0.15f);
            c.EndLink();
        }
        void Split(CommandWriter c, float s)
        {
            var p = splitAmount * s;
            c.BeginEffectChainLink(Protocol.EffectTemplate.Single, [split], p, p, p, p, 0);
            c.UniformF32("amount", splitAmount * s);
            c.UniformVec2("direction", 1, 0);
            c.UniformColor("tint", 1, 1, 1, 1);
            c.UniformBool("scanlines", false);
            c.EndLink();
        }

        // One cell at (ox, oy): ground, label, the text "VL.Qt" with the effect chain of the kind.
        void Cell(string kind, string label, float ox, float oy, float s, uint order)
        {
            uint ground = next++, text = next++, labelId = next++, map = next++;
            cmd.Create(ground, Protocol.ElementType.Rectangle);
            cmd.SetPlace(ground, ox + 5 * s, oy + 5 * s, (CellW - 10) * s, (CellH - 10) * s);
            cmd.SetColor(ground, Protocol.PropertyId.Color, 0.105f, 0.115f, 0.14f, 1);
            cmd.SetF32(ground, Protocol.PropertyId.Radius, 10 * s);
            cmd.Reparent(ground, 0, order * 3);

            cmd.Create(labelId, Protocol.ElementType.Text);
            cmd.SetPlace(labelId, ox + 22 * s, oy + (CellH - 42) * s, (CellW - 44) * s, 26 * s);
            cmd.SetString(labelId, Protocol.PropertyId.Text, label);
            cmd.SetString(labelId, Protocol.PropertyId.FontFamily, family);
            cmd.SetF32(labelId, Protocol.PropertyId.FontSize, 15 * s);
            cmd.SetColor(labelId, Protocol.PropertyId.Color, 0.52f, 0.57f, 0.66f, 1);
            cmd.SetI32(labelId, Protocol.PropertyId.HorizontalAlignment, 1);
            cmd.SetI32(labelId, Protocol.PropertyId.VerticalAlignment, 128);
            cmd.Reparent(labelId, 0, order * 3 + 1);

            cmd.Create(text, Protocol.ElementType.Text);
            cmd.SetPlace(text, ox + (CellW - TextW) / 2 * s, oy + 28 * s, TextW * s, TextH * s);
            cmd.SetString(text, Protocol.PropertyId.Text, "VL.Qt");
            cmd.SetString(text, Protocol.PropertyId.FontFamily, family);
            cmd.SetF32(text, Protocol.PropertyId.FontSize, FontSize * s);
            cmd.SetI32(text, Protocol.PropertyId.FontWeight, 700);
            cmd.SetI32(text, Protocol.PropertyId.HorizontalAlignment, 4);
            cmd.SetI32(text, Protocol.PropertyId.VerticalAlignment, 128);
            cmd.SetColor(text, Protocol.PropertyId.Color, 0.94f, 0.93f, 0.9f, 1);
            cmd.Reparent(text, 0, order * 3 + 2);

            void Pyramid(ulong pre, ulong comp, float radius, Action<CommandWriter, float> uniforms)
            {
                var p = MathF.Ceiling(1.5f * radius * s);
                cmd.BeginEffectChain(text);
                cmd.BeginEffectChainLink(Protocol.EffectTemplate.Pyramid, [pre, comp], p, p, p, p, radius * s);
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
                    cmd.SetColor(text, Protocol.PropertyId.Color, 1, 0.66f, 0.3f, 1);
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
                    cmd.BeginEffectChainLink(Protocol.EffectTemplate.TwoPass, [directional], pad, 0, pad, 0, 0);
                    cmd.UniformF32("angle", 0);
                    cmd.UniformF32("blurLength", L * s);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
                }
                case "zoom":
                {
                    const float L = 40;
                    var pad = L * 0.6f * s;
                    cmd.BeginEffectChain(text);
                    cmd.BeginEffectChainLink(Protocol.EffectTemplate.TwoPass, [zoom], pad, pad, pad, pad, 0);
                    cmd.UniformVec2("center", 0.5f, 0.5f);
                    cmd.UniformF32("blurLength", L * s);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
                }
                case "radial":
                {
                    var pad = 30 * s;
                    cmd.BeginEffectChain(text);
                    cmd.BeginEffectChainLink(Protocol.EffectTemplate.TwoPass, [radial], pad, pad, pad, pad, 0);
                    cmd.UniformVec2("center", 0.5f, 0.5f);
                    cmd.UniformF32("angle", 8f / 360f);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
                }
                case "displace":
                    // The map is never reparented: it only serves as texture of the link.
                    cmd.Create(map, Protocol.ElementType.Shader);
                    cmd.SetU64(map, Protocol.PropertyId.FragmentShader, waveMap);
                    cmd.SetPlace(map, 0, 0, TextW * s, TextH * s);
                    cmd.BeginEffectChain(text);
                    cmd.BeginEffectChainLink(Protocol.EffectTemplate.Single, [displace], 0, 0, 0, 0, 0);
                    cmd.UniformF32("displacement", 7 * s);
                    cmd.UniformRef("map", map);
                    cmd.EndLink();
                    cmd.EndEffectChain();
                    break;
            }
        }

        void Render(string name, int width, float winW, float winH, params (string kind, string label)[] cells)
        {
            cmd.Clear();
            next = 1;
            var w = NativeCalls.CreateWindow((int)MathF.Round(winW), (int)MathF.Round(winH), "VL.Qt readme images");
            s.Check(w != SceneHandle.Null, $"readme-images {name}: window_create");
            Native.vlqt_window_show(w);
            s.Pump(4);
            cmd.ClearColor(Bg, Bg, Bg, 1);
            var cols = cells.Length > 2 ? 4 : cells.Length;
            var scale = winW / (cols * CellW);
            uint order = 0;
            foreach (var (kind, label) in cells)
            {
                Cell(kind, label, order % (uint)cols * CellW * scale, order / (uint)cols * CellH * scale, scale, order);
                order++;
            }
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"readme-images {name}: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Clear();
            s.Pump(10);
            var grabPath = Path.Combine(s.OutDir, $"readme-images_{name}.png");
            using (var a = s.Grab(w, grabPath))
            {
                var ink = ImageCompare.Ink(a);
                s.Check(ink > 0.02, $"readme-images {name}: picture not empty (ink {ink:P1}, {a.Width} x {a.Height})");
            }
            ImageCompare.Check(s, "readme-images_" + name, grabPath, width);
            Native.vlqt_scene_destroy(w);
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
