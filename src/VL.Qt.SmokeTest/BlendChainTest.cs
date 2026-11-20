using System;
using System.Drawing;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// Blend and Displace behind a Blur in the Effect chain (#439, Sebastian's report on PR #528): the chain grows by the padding of
// the Blur (MultiEffect, automatic padding), the layer / map element must still sit exactly on the ELEMENT (QtNative auto uniform
// elementRect), not be stretched over the grown effect area, and the halo of the Blur must survive the Blend pass.
// Scene (one window per case): a 300 x 150 DIP orange rectangle R at (250, 150) on 0x20 grey; the layer is
// a 300 x 150 DIP opaque blue rectangle and the map a constant (red 1, green 0.5) rectangle, both never REPARENTed (texture
// elements). Blend: Mode Normal, opacity 1: where the layer lies the picture is blue, elsewhere the base (orange, blurred)
// stays. Probes sit 3 DIP outside the element edges (inside the blur halo) and 8 DIP inside the corners.
//   (a) Blur -> Blend: the layer lies on the element (inside blue), the halo outside is orange: not blue (= layer stretched over
//       the grown area), not background (= halo cut).
//   (b) Blend -> Blur (counter check): the blended picture is blurred as a whole, blue inside, halo visible.
//   (c) Blend alone (no padding anywhere): blue on the element, background around it.
//   (d) Blur -> Displace: the map (clamped outside the element) shifts the content left, the blurred edge is intact.
//   (e) rebuild-blur: Blur, Blur -> Displace, Displace -> Blur, Displace, Blur in ONE window: the last Blur keeps its halo; and
//       rebuild-blur-gap: Blur, Displace, Blur. #529: a Displace at the first link wrote the element layer's ShaderEffectSource
//       sourceRect past the layer's cached value, so the next Blur's (equal) padded layer.sourceRect never arrived (hard edge).
// Raw commands (DIP). Grabs: <outDir>/blend-chain_*.png.
[TestFixture, NonParallelizable]
public static class BlendChainTest
{
    [Test, Category("blend-chain")]
    public static void BlendChain() => Scenario.Run(Run);

    const uint R = 1, Layer = 2, Map = 3;
    const float RX = 250, RY = 150, RW = 300, RH = 150;

    static Color Px(Bitmap a, float x, float y) => a.GetPixel((int)(x * a.Width / 800), (int)(y * a.Height / 600));
    static bool Blueish(Color c) => c.B > c.R + 40;
    static bool Orangeish(Color c) => c.R > c.B + 12 && c.R > 0x30;
    static bool Lit(Color c) => c.R + c.G + c.B > 3 * 0x20 + 20;

    static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "the passes are ShaderEffects");
        Directory.CreateDirectory(s.OutDir);
        ulong Shader(string name)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader(id, "builtin:" + name, Protocol.ShaderStage.Fragment);
            s.Check(Native.vlqt_res_wait(id, 10000) == Protocol.ResState.Ready, $"chain: shader builtin:{name} Ready");
            return id;
        }
        var blend = Shader("blend");
        var displace = Shader("displace");

        static void Blur(CommandWriter c)
        {
            c.BeginMultiLink();
            c.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
            c.EffectF32(Protocol.Prop.EffectBlur, 1f);
            c.EffectI32(Protocol.Prop.EffectBlurMax, 32);
            c.EndLink();
        }
        void Blend(CommandWriter c)
        {
            c.BeginShaderLink(Protocol.EffectTemplate.Single, [blend], 0, 0, 0, 0, 0);
            c.UniformI32("mode", 0);
            c.UniformF32("layerOpacity", 1);
            c.UniformRef("layer", Layer);
            c.EndLink();
        }
        void Displace(CommandWriter c)
        {
            c.BeginShaderLink(Protocol.EffectTemplate.Single, [displace], 0, 0, 0, 0, 0);
            c.UniformF32("displacement", 20);
            c.UniformRef("map", Map);
            c.EndLink();
        }

        // probes: 3 DIP outside each edge (middle of the edge), 8 DIP inside the corners, the center
        (float x, float y)[] outside =
        {
            (RX - 3, RY + RH / 2), (RX + RW + 3, RY + RH / 2), (RX + RW / 2, RY - 3), (RX + RW / 2, RY + RH + 3),
        };
        string[] sides = { "left", "right", "top", "bottom" };
        (float x, float y)[] inside =
        {
            (RX + 8, RY + 8), (RX + RW - 8, RY + 8), (RX + 8, RY + RH - 8), (RX + RW - 8, RY + RH - 8), (RX + RW / 2, RY + RH / 2),
        };

        // One window, the scene, the chain; probes see the grab.
        void Case(string name, Action<CommandWriter>[] links, Action<Bitmap> probes, Action<CommandWriter>[][]? prior = null)
        {
            var w = Native.vlqt_window_create(800, 600, "VL.Qt blend chain test " + name);
            s.Check(w != IntPtr.Zero, $"chain: {name}: window_create");
            Native.vlqt_window_show(w);
            s.Pump(4);
            var cmd = new CommandWriter();
            void Apply(string what)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"chain: {name}: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
                cmd.Reset();
            }
            cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            cmd.Create(R, Protocol.Type.Rectangle);
            cmd.SetPlace(R, RX, RY, RW, RH);
            cmd.SetColor(R, Protocol.Prop.Color, 1, 0.5f, 0.1f, 1);
            cmd.Reparent(R, 0, 0);
            cmd.Create(Layer, Protocol.Type.Rectangle);
            cmd.SetPlace(Layer, 0, 0, RW, RH);
            cmd.SetColor(Layer, Protocol.Prop.Color, 0, 0.4f, 1, 1);
            cmd.Create(Map, Protocol.Type.Rectangle);
            cmd.SetPlace(Map, 0, 0, RW, RH);
            cmd.SetColor(Map, Protocol.Prop.Color, 1, 0.5f, 0, 1);
            var first = true;
            foreach (var step in (prior ?? []).Append(links))
            {
                cmd.BeginEffectChain(R);
                foreach (var l in step)
                    l(cmd);
                cmd.EndEffectChain();
                Apply(first ? "scene and chain" : "chain");
                first = false;
                s.Pump(8);
            }
            using (var a = s.Grab(w, $"blend-chain_{name}.png"))
                probes(a);
            cmd.BeginEffectChain(R);
            cmd.EndEffectChain();
            cmd.Destroy(R);
            cmd.Destroy(Layer);
            cmd.Destroy(Map);
            Apply("destroy");
            s.Pump(3);
            Native.vlqt_window_destroy(w);
            s.Pump(2);
        }

        Case("blur-blend", [Blur, Blend], a =>
        {
            for (var i = 0; i < inside.Length; i++)
            {
                var c = Px(a, inside[i].x, inside[i].y);
                gpu(Blueish(c), $"chain: Blur -> Blend: layer on the element, probe {i} inside is blue ({c.R}, {c.G}, {c.B})");
            }
            for (var i = 0; i < outside.Length; i++)
            {
                var c = Px(a, outside[i].x, outside[i].y);
                gpu(Lit(c) && Orangeish(c),
                    $"chain: Blur -> Blend: {sides[i]} of the element: blur halo (orange, not blue = not stretched, not cut) ({c.R}, {c.G}, {c.B})");
            }
        });

        Case("blend-blur", [Blend, Blur], a =>
        {
            var mid = Px(a, RX + RW / 2, RY + RH / 2);
            gpu(Blueish(mid), $"chain: Blend -> Blur: element center is blue ({mid.R}, {mid.G}, {mid.B})");
            for (var i = 0; i < outside.Length; i++)
            {
                var c = Px(a, outside[i].x, outside[i].y);
                gpu(Lit(c) && Blueish(c), $"chain: Blend -> Blur: {sides[i]} of the element: blurred blue halo ({c.R}, {c.G}, {c.B})");
            }
        });

        Case("blend", [Blend], a =>
        {
            for (var i = 0; i < inside.Length; i++)
            {
                var p = Px(a, inside[i].x, inside[i].y);
                gpu(Blueish(p), $"chain: Blend alone: probe {i} inside is blue ({p.R}, {p.G}, {p.B})");
            }
            for (var i = 0; i < outside.Length; i++)
            {
                var p = Px(a, outside[i].x, outside[i].y);
                gpu(!Lit(p), $"chain: Blend alone: {sides[i]} outside is background ({p.R}, {p.G}, {p.B})");
            }
        });

        // Map R = 1 shifts the content 20 DIP to the left: the halo is still there on the left (the picture is not cut at the
        // element) and the right edge of the element moved in, soft.
        Case("blur-displace", [Blur, Displace], a =>
        {
            var center = Px(a, RX + RW / 2, RY + RH / 2);
            gpu(Orangeish(center), $"chain: Blur -> Displace: center still orange ({center.R}, {center.G}, {center.B})");
            var left = Px(a, RX - 3 - 20, RY + RH / 2);
            gpu(Lit(left) && Orangeish(left), $"chain: Blur -> Displace: halo left of the content is not cut, map on the element ({left.R}, {left.G}, {left.B})");
            var right = Px(a, RX + RW - 20 + 3, RY + RH / 2);
            gpu(Lit(right) && right.R < 0xF0, $"chain: Blur -> Displace: the right edge moved in by the shift, soft ({right.R}, {right.G}, {right.B})");
        });

        // The task of the note above: Blur at the first link again after the structure changed in the same window. The Blur
        // must keep its halo (outside the element is the orange blur, not the hard-edged background).
        Case("rebuild-blur", [Blur], a =>
        {
            for (var i = 0; i < outside.Length; i++)
            {
                var c = Px(a, outside[i].x, outside[i].y);
                gpu(Lit(c) && Orangeish(c) && c.R < 0xF0, $"chain: Blur -> ... -> Blur again in one window: {sides[i]} of the element: halo ({c.R}, {c.G}, {c.B})");
            }
        }, [[Blur], [Blur, Displace], [Displace, Blur], [Displace]]);

        // Blur, then no Blur, then Blur on the same source (the earlier hard-edge case).
        Case("rebuild-blur-gap", [Blur], a =>
        {
            var c = Px(a, RX - 3, RY + RH / 2);
            gpu(Lit(c) && Orangeish(c) && c.R < 0xF0, $"chain: Blur -> Displace -> Blur: halo ({c.R}, {c.G}, {c.B})");
        }, [[Blur], [Displace]]);

        Native.vlqt_res_release(blend);
        Native.vlqt_res_release(displace);
        s.Pump(2);
    }
}
