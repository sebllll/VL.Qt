using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using VL.Qt;
using Color = System.Drawing.Color;

// SHADER links of the Effect chain (#489, plan effects/08 Task 3): QtNative's pass driver with the templates SINGLE and
// TWO_PASS, raw SET_EFFECT_CHAIN. (a) MULTI(blur) -> SINGLE(builtin:wave) -> MULTI(hard red shadow) against the reversed
// order on a white rectangle R: both show the wave and the shadow, the pictures differ. (b) TWO_PASS builtin:directional_blur
// 400 DIP on a Text T: no copies (much smoother than one pass of the same length), and on a rectangle S: the blur reaches
// L / 2 into the padding in DIP (texelSize / dpr by QtNative; the extent is checked at the dpr of this display), padding 0
// cuts it at the element. (c) A texture uniform (REF, builtin:displace with a map element that is never REPARENTed) shifts
// R. (d) Reserved names and a REF with a parent are EV_ERROR lines ("link N: ..."), the apply succeeds. (e) Values only:
// no new items; live changes of the structure and back to no chain: QML item count as before (no leak).
// (f) PYRAMID (Task 4, own window, Pyramid): builtin glow_prefilter/glow_composite and innershadow_* as links with radius
// 4/32/256 DIP and padding ceil(1.5 r) (the glow is the one of GlowTest; a larger padding gives the same glow = nothing
// cut), radius sweep over two step boundaries (no jump, the far point grows, no new item), size change of the element
// (steps follow), MULTI -> PYRAMID -> SINGLE, a name neither shader declares is an EV_ERROR line, live changes without leak.
// Grabs: <outDir>/effect-passes_*.png.
static class EffectPassesTest
{
    const uint R = 1, T = 2, S = 3, Map = 4, Bad = 5, SinkR = 41;
    const float RX = 250, RY = 60, RW = 300, RH = 150;
    const float SX = 350, SY = 470, SW = 100, SH = 60;
    const float L = 400; // directional blur length (DIP)

    static Color Px(Bitmap a, float x, float y) => a.GetPixel((int)(x * a.Width / 800), (int)(y * a.Height / 600));
    static bool IsBg(Color c) => Math.Abs(c.R - 0x20) < 8 && Math.Abs(c.G - 0x20) < 8 && Math.Abs(c.B - 0x20) < 8;
    static double Lit(Color c) => ((c.R + c.G + c.B) / 3.0 - 0x20) / (255.0 - 0x20); // 0 = background, 1 = white

    [DllImport("vlqt", CallingConvention = CallingConvention.Cdecl)]
    static extern int vlqt_test_perf_text(IntPtr w, int page, byte[]? out_, int cap);

    // Scene page (F5) of the perf overlay: "QML items <n>" (items in the window's tree). A poll first: retired stages are
    // deleted one frame late, at the next poll or apply (#123).
    static int Items(IntPtr w)
    {
        Native.Poll(w, new byte[1 << 16]);
        var need = -vlqt_test_perf_text(w, 2, null, 0);
        var buf = new byte[Math.Max(need, 1)];
        var n = vlqt_test_perf_text(w, 2, buf, buf.Length);
        var m = Regex.Match(n > 0 ? Encoding.UTF8.GetString(buf, 0, n) : "", @"QML items\s+(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    // Last EV_ERROR text for sink in the poll buffer, null if none.
    static string? ErrorText(ReadOnlySpan<byte> b, uint sink)
    {
        string? text = null;
        int off = 0;
        while (off + 12 <= b.Length)
        {
            var ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            var len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 2));
            if (len < 8 || off + 4 + len > b.Length) break;
            if (ev == Protocol.Ev.Error && BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4)) == sink)
                text = Encoding.UTF8.GetString(b.Slice(off + 12, len - 8));
            off += (4 + len + 3) & ~3;
        }
        return text;
    }

    // Right edge of R per row (first lit pixel from the right), min and max over the rows.
    static (float min, float max) RightEdge(Bitmap a)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (var y = RY + 10; y < RY + RH - 10; y += 4)
            for (var x = RX + RW + 60; x > RX; x -= 1)
                if (Lit(Px(a, x, y)) > 0.5)
                {
                    min = Math.Min(min, x);
                    max = Math.Max(max, x);
                    break;
                }
        return (min, max);
    }

    // Mean second difference of Lit along rows through T (one physical pixel apart): high for copies of the text.
    static double Rough(Bitmap a)
    {
        var step = 800.0 / a.Width;
        double sum = 0;
        int n = 0;
        for (var y = 285; y <= 375; y += 6)
        {
            double v0 = Lit(Px(a, 150, y)), v1 = Lit(Px(a, (float)(150 + step), y));
            for (var x = 150 + 2 * step; x < 650; x += step, n++)
            {
                var v2 = Lit(Px(a, (float)x, y));
                sum += Math.Abs(v0 - 2 * v1 + v2);
                (v0, v1) = (v1, v2);
            }
        }
        return sum / n;
    }

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var gpu = Program.NeedsRhi(check, "the passes are ShaderEffects");
        ulong Shader(string name)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader(id, "builtin:" + name, Protocol.ShaderStage.Fragment);
            check(Native.vlqt_res_wait(id, 10000) == Protocol.ResState.Ready, $"effect-passes: shader builtin:{name} Ready");
            return id;
        }
        var wave = Shader("wave");
        var blur = Shader("directional_blur");
        var displace = Shader("displace");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt effect passes test");
        check(w != IntPtr.Zero, "effect-passes: window_create");
        Native.vlqt_window_show(w);
        pump(4);
        float dpr;
        using (var probe = grab(w, Path.Combine(outDir, "effect-passes_probe.png")))
            dpr = Math.Max(1f, probe.Width / 800f);
        Console.WriteLine($"INFO effect-passes: device pixel ratio {dpr:F2} (another dpr needs another run, QT_SCALE_FACTOR)");
        var cmd = new CommandWriter();
        var poll = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"effect-passes: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        string? PollError()
        {
            pump(2);
            var n = Native.Poll(w, poll);
            return ErrorText(poll.AsSpan(0, Math.Max(n, 0)), SinkR);
        }
        Bitmap Shot(string name) { pump(5); return grab(w, Path.Combine(outDir, $"effect-passes_{name}.png")); }

        // Links
        static void Blur(CommandWriter c)
        {
            c.BeginMultiLink();
            c.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
            c.EffectF32(Protocol.Prop.EffectBlur, 0.5f);
            c.EffectI32(Protocol.Prop.EffectBlurMax, 16);
            c.EndLink();
        }
        static void Shadow(CommandWriter c)
        {
            c.BeginMultiLink();
            c.EffectBool(Protocol.Prop.EffectShadowEnabled, true);
            c.EffectColor(Protocol.Prop.EffectShadowColor, 1, 0, 0, 1);
            c.EffectF32(Protocol.Prop.EffectShadowHorizontalOffset, 30);
            c.EffectF32(Protocol.Prop.EffectShadowVerticalOffset, 30);
            c.EffectF32(Protocol.Prop.EffectShadowBlur, 0);
            c.EndLink();
        }
        void Wave(CommandWriter c, float amplitude = 0.05f, string? extra = null)
        {
            c.BeginShaderLink(Protocol.EffectTemplate.Single, [wave], 20, 20, 20, 20, 0, "");
            c.UniformF32("time", 0);
            c.UniformF32("amplitude", amplitude);
            c.UniformF32("frequency", 3);
            if (extra != null)
                c.UniformF32(extra, 1);
            c.EndLink();
        }
        void Directional(CommandWriter c, ushort template, float pad)
        {
            c.BeginShaderLink(template, [blur], pad, 0, pad, 0, 0, "");
            c.UniformF32("angle", 0);
            c.UniformF32("blurLength", L);
            c.EndLink();
        }
        void Chain(uint id, params Action<CommandWriter>[] links)
        {
            cmd.BeginEffectChain(id);
            foreach (var l in links)
                l(cmd);
            cmd.EndEffectChain();
        }

        // Scene: R white rectangle, T text "MO", S white rectangle, Map (red 1, green 0.5: shift along x) never REPARENTed,
        // Bad (a rectangle with a parent) for the REF error.
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(R, Protocol.Type.Rectangle);
        cmd.SetPlace(R, RX, RY, RW, RH);
        cmd.SetColor(R, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(R, 0, 0);
        cmd.Bind(R, SinkR, 0);
        cmd.Create(T, Protocol.Type.Text);
        cmd.SetPlace(T, 300, 260, 200, 140);
        cmd.SetString(T, Protocol.Prop.Text, "MO");
        cmd.SetF32(T, Protocol.Prop.FontSize, 110);
        cmd.SetColor(T, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(T, 0, 1);
        cmd.Create(S, Protocol.Type.Rectangle);
        cmd.SetPlace(S, SX, SY, SW, SH);
        cmd.SetColor(S, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(S, 0, 2);
        cmd.Create(Map, Protocol.Type.Rectangle);
        cmd.SetPlace(Map, 0, 0, 100, 100);
        cmd.SetColor(Map, Protocol.Prop.Color, 1, 0.5f, 0, 1);
        cmd.Create(Bad, Protocol.Type.Rectangle);
        cmd.SetPlace(Bad, 0, 0, 10, 10);
        cmd.SetColor(Bad, Protocol.Prop.Color, 0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Reparent(Bad, 0, 3);
        CheckApply("scene");
        pump(5);
        var itemsPlain = Items(w);
        using var plain = Shot("plain");
        var plainEdge = RightEdge(plain);
        check(itemsPlain > 0 && Math.Abs(plainEdge.max - plainEdge.min) <= 1 && Math.Abs(plainEdge.max - (RX + RW)) <= 2,
            $"effect-passes: plain scene (items {itemsPlain}, R right edge {plainEdge})");

        // (a) SINGLE wave alone: the right edge waves; then the mixed chain in both orders.
        Chain(R, c => Wave(c));
        CheckApply("SINGLE wave");
        using (var a = Shot("wave"))
        {
            var e = RightEdge(a);
            gpu(e.max - e.min > 6, $"effect-passes: SINGLE wave bends the right edge of R ({e})");
        }
        Chain(R, Blur, c => Wave(c), Shadow);
        CheckApply("blur -> wave -> shadow");
        using var order1 = Shot("order_blur-wave-shadow");
        Chain(R, Shadow, c => Wave(c), Blur);
        CheckApply("shadow -> wave -> blur");
        using var order2 = Shot("order_shadow-wave-blur");
        int red1 = 0, red2 = 0, diff = 0;
        for (var y = 30f; y < 280; y += 2)
            for (var x = 200f; x < 640; x += 2)
            {
                Color c1 = Px(order1, x, y), c2 = Px(order2, x, y);
                if (c1.R > c1.G + 60) red1++;
                if (c2.R > c2.G + 60) red2++;
                if (Math.Abs(c1.R - c2.R) + Math.Abs(c1.G - c2.G) + Math.Abs(c1.B - c2.B) > 60) diff++;
            }
        gpu(red1 > 100 && red2 > 100, $"effect-passes: red shadow in both orders ({red1}, {red2})");
        gpu(diff > 300, $"effect-passes: reversed order is a different picture ({diff} pixels differ)");

        // (e) values only (same structure): no new item.
        pump(3);
        var itemsChain = Items(w);
        Chain(R, Shadow, c => Wave(c, 0.02f), Blur);
        CheckApply("values only");
        pump(5);
        check(Items(w) == itemsChain, $"effect-passes: values only -> no new item ({Items(w)} vs {itemsChain})");

        // (d) reserved names and a REF with a parent: EV_ERROR lines with the link index, the apply succeeds.
        Chain(R, Blur, c => Wave(c, 0.05f, "texelSize"));
        CheckApply("reserved name");
        var err = PollError();
        check(err != null && err.Contains("link 1: uniform 'texelSize' is reserved"), $"effect-passes: reserved name -> EV_ERROR ({err})");
        // passIndex is set by QtNative (TWO_PASS: 0, then 1), so C# may not send it.
        Chain(R, Blur, c => Wave(c, 0.05f, "passIndex"));
        CheckApply("reserved passIndex");
        err = PollError();
        check(err != null && err.Contains("link 1: uniform 'passIndex' is reserved"), $"effect-passes: passIndex reserved -> EV_ERROR ({err})");
        Chain(R, c =>
        {
            c.BeginShaderLink(Protocol.EffectTemplate.Single, [displace], 0, 0, 0, 0, 0, "");
            c.UniformF32("displacement", 30);
            c.UniformRef("displacementSource", Bad);
            c.EndLink();
        });
        CheckApply("REF with a parent");
        err = PollError();
        check(err != null && err.StartsWith("link 0: uniform 'displacementSource': the texture element has a parent"),
            $"effect-passes: REF with a parent -> EV_ERROR ({err})");

        // (c) REF texture: Map (red 1 = shift) parked and sampled; R shifts left, Map itself is not drawn.
        Chain(R, c =>
        {
            c.BeginShaderLink(Protocol.EffectTemplate.Single, [displace], 0, 0, 0, 0, 0, "");
            c.UniformF32("displacement", 30);
            c.UniformRef("displacementSource", Map);
            c.EndLink();
        });
        CheckApply("REF texture");
        err = PollError();
        check(string.IsNullOrEmpty(err), $"effect-passes: REF texture without error ({err})");
        using (var d = Shot("displace"))
        {
            var e = RightEdge(d);
            // displacement 30 DIP at full red: 30 DIP at any dpr (texelSize per pixel, the shader scales by dpr).
            gpu(Math.Abs(e.max - (RX + RW - 30)) <= 2 && Math.Abs(e.min - (RX + RW - 30)) <= 2,
                $"effect-passes: REF map shifts R 30 DIP to the left at dpr {dpr:F2} ({e}, expected {RX + RW - 30})");
            check(IsBg(Px(d, 50, 50)), $"effect-passes: the parked map is not drawn ({Px(d, 50, 50)})");
        }
        Chain(R);
        CheckApply("R chain off");

        // (b) TWO_PASS directional blur 400 DIP: T smooth (vs one pass), S reaches L / 2 into the padding.
        var pad = L / 2 + L / 64;
        Chain(T, c => Directional(c, Protocol.EffectTemplate.Single, pad));
        Chain(S, c => Directional(c, Protocol.EffectTemplate.TwoPass, 0));
        CheckApply("one pass / padding 0");
        double roughSingle;
        using (var b = Shot("directional_single"))
        {
            roughSingle = Rough(b);
            gpu(IsBg(Px(b, SX - 50, SY + SH / 2)) && Lit(Px(b, SX + 10, SY + SH / 2)) > 0.1,
                $"effect-passes: padding 0 cuts the blur at the element ({Px(b, SX - 50, SY + SH / 2)}, {Px(b, SX + 10, SY + SH / 2)})");
        }
        Chain(T, c => Directional(c, Protocol.EffectTemplate.TwoPass, pad));
        Chain(S, c => Directional(c, Protocol.EffectTemplate.TwoPass, pad));
        CheckApply("TWO_PASS");
        using (var b = Shot("directional_two-pass"))
        {
            var rough = Rough(b);
            gpu(rough < roughSingle * 0.5, $"effect-passes: TWO_PASS has no copies (roughness {rough:F4} vs one pass {roughSingle:F4})");
            var y = SY + SH / 2;
            double at50 = Lit(Px(b, SX - 50, y)), at150 = Lit(Px(b, SX - 150, y)), at185 = Lit(Px(b, SX - 185, y)),
                at215 = Lit(Px(b, SX - 215, y)), right150 = Lit(Px(b, SX + SW + 150, y));
            // Box of L DIP over S (100 DIP): overlap / L at the distance d left of S (0.25 at 50, 0.125 at 150, 0 beyond 200).
            gpu(Math.Abs(at50 - 0.25) < 0.06 && Math.Abs(at150 - 0.125) < 0.04 && Math.Abs(right150 - 0.125) < 0.04,
                $"effect-passes: blur profile in DIP at dpr {dpr:F2} (50: {at50:F3}, 150: {at150:F3}, right 150: {right150:F3})");
            gpu(at185 > 0.012 && at215 < 0.012, $"effect-passes: blur ends L / 2 left of S (185: {at185:F3}, 215: {at215:F3})");
        }

        // (e) live structure changes, then no chains: item count as without effects.
        Chain(T);
        Chain(S);
        CheckApply("T, S off");
        Chain(R, Blur, c => Wave(c), Shadow);
        CheckApply("mixed again");
        pump(3);
        Chain(R, c => Directional(c, Protocol.EffectTemplate.TwoPass, pad));
        CheckApply("TWO_PASS on R");
        pump(3);
        Chain(R, c =>
        {
            c.BeginShaderLink(Protocol.EffectTemplate.Pyramid, [wave, wave], 0, 0, 0, 0, 10, "");
            c.EndLink();
        }, c => Wave(c));
        CheckApply("PYRAMID (pass-through) + wave");
        pump(3);
        Chain(R);
        CheckApply("all chains off");
        pump(6);
        var itemsAfter = Items(w);
        // + 2: Map is parked under the root since its use as texture (hidden until a REPARENT or its DESTROY), with the
        // ShaderEffectSource of its layer as sibling (Qt's QQuickItemLayer).
        check(itemsAfter == itemsPlain + 2, $"effect-passes: no leak after live changes (items {itemsAfter} vs {itemsPlain} + parked map + its layer)");
        using (var off = Shot("off"))
        {
            var e = RightEdge(off);
            check(Math.Abs(e.max - (RX + RW)) <= 2 && e.max - e.min <= 1, $"effect-passes: R plain again ({e})");
        }

        cmd.Destroy(R);
        cmd.Destroy(T);
        cmd.Destroy(S);
        cmd.Destroy(Map);
        cmd.Destroy(Bad);
        CheckApply("destroy");
        pump(3);
        Native.vlqt_window_destroy(w);
        Native.vlqt_res_release(blur);
        Native.vlqt_res_release(displace);
        pump(2);

        Pyramid(outDir, check, pump, grab, gpu, wave);
        Native.vlqt_res_release(wave);
        pump(2);
    }

    // (f) PYRAMID links. G: orange box 160 x 100 at (320, 250) like GlowTest, the glow is measured right of it (y 300).
    const uint G = 1, SinkG = 42;
    const float GX = 320, GY = 250, GW = 160, GH = 100;

    // Glow strength at (x, y): mean green above the background over 5 x 5 samples 1 DIP apart (like GlowTest).
    static float Glow(Bitmap a, float x, float y)
    {
        float sum = 0;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                sum += Px(a, x + dx, y + dy).G - 0x20;
        return sum / 25;
    }
    static bool Near(Color c, int r, int g, int b, int tol = 14) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;
    static float Level(float radius, float dpr)
    {
        var s = radius * dpr / 2;
        return 0.5f * MathF.Log2(1 + s * s * 6 / 7);
    }
    static float RadiusOf(float level, float dpr) => 2 / dpr * MathF.Sqrt((MathF.Pow(4, level) - 1) * 7 / 6);

    static void Pyramid(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab,
        Action<bool, string> gpu, ulong wave)
    {
        ulong Shader(string name)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            Native.vlqt_res_load_shader(id, "builtin:" + name, Protocol.ShaderStage.Fragment);
            check(Native.vlqt_res_wait(id, 10000) == Protocol.ResState.Ready, $"effect-passes: shader builtin:{name} Ready");
            return id;
        }
        var glowPre = Shader("glow_prefilter");
        var glowComp = Shader("glow_composite");
        var isPre = Shader("innershadow_prefilter");
        var isComp = Shader("innershadow_composite");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt effect passes test (pyramid)");
        check(w != IntPtr.Zero, "effect-passes pyramid: window_create");
        Native.vlqt_window_show(w);
        pump(4);
        float dpr;
        using (var probe = grab(w, Path.Combine(outDir, "effect-passes_pyramid_probe.png")))
            dpr = Math.Max(1f, probe.Width / 800f);
        var cmd = new CommandWriter();
        var poll = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"effect-passes pyramid: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        string? PollError()
        {
            pump(2);
            var n = Native.Poll(w, poll);
            return ErrorText(poll.AsSpan(0, Math.Max(n, 0)), SinkG);
        }
        Bitmap Shot(string name) { pump(6); return grab(w, Path.Combine(outDir, $"effect-passes_{name}.png")); }

        // Glow link (cyan, like GlowTest); padding ceil(1.5 r) unless given.
        Action<CommandWriter> GlowLink(float radius, float? pad = null, float intensity = 1.5f, string? extra = null) => c =>
        {
            var p = pad ?? MathF.Ceiling(1.5f * radius);
            c.BeginShaderLink(Protocol.EffectTemplate.Pyramid, [glowPre, glowComp], p, p, p, p, radius, "");
            c.UniformColor("color", 0.1f, 0.8f, 1, 1);
            c.UniformF32("spread", 0);
            c.UniformF32("intensity", intensity);
            c.UniformBool("additive", false);
            if (extra != null)
                c.UniformF32(extra, 1);
            c.EndLink();
        };
        // InnerShadow link, offset in DIP (sent in UV units of the effect area: element + padding).
        Action<CommandWriter> ShadowLink(float radius, float ox, float oy) => c =>
        {
            var p = MathF.Ceiling(1.5f * radius);
            c.BeginShaderLink(Protocol.EffectTemplate.Pyramid, [isPre, isComp], p, p, p, p, radius, "");
            c.UniformVec2("offset", ox / (GW + 2 * p), oy / (GH + 2 * p));
            c.UniformColor("color", 0, 0, 0, 1);
            c.UniformF32("spread", 0);
            c.UniformF32("intensity", 1);
            c.EndLink();
        };
        void Chain(params Action<CommandWriter>[] links)
        {
            cmd.BeginEffectChain(G);
            foreach (var l in links)
                l(cmd);
            cmd.EndEffectChain();
        }

        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(G, Protocol.Type.Rectangle);
        cmd.SetPlace(G, GX, GY, GW, GH);
        cmd.SetColor(G, Protocol.Prop.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(G, 0, 0);
        cmd.Bind(G, SinkG, 0);
        CheckApply("scene");
        pump(4);
        var itemsPlain = Items(w);

        const float y0 = 300, edge = GX + GW;
        string Profile(Bitmap a) => $"d3 {Glow(a, edge + 3, y0):F1}, d10 {Glow(a, edge + 10, y0):F1}, d25 {Glow(a, edge + 25, y0):F1}, " +
            $"d40 {Glow(a, edge + 40, y0):F1}, d100 {Glow(a, edge + 100, y0):F1}, d200 {Glow(a, edge + 200, y0):F1}";
        float[] Far(Bitmap a) => new[] { 3f, 10, 25, 40, 100, 200 }.Select(d => Glow(a, edge + d, y0)).ToArray();
        bool Same(float[] x, float[] y) => x.Zip(y, (p, q) => Math.Abs(p - q) <= Math.Max(1.5f, 0.06f * Math.Max(p, q))).All(b => b);

        // Radius 4 / 32 / 256 with padding ceil(1.5 r); each against padding 3 r: the same glow (the padding cuts nothing).
        foreach (var radius in new[] { 4f, 32f, 256f })
        {
            Chain(GlowLink(radius));
            CheckApply($"glow r{radius}");
            using var a = Shot($"glow_r{radius}");
            var v = Far(a);
            Console.WriteLine($"INFO effect-passes glow r{radius} (dpr {dpr:F2}): {Profile(a)}, box {Px(a, 400, y0)}");
            gpu(Near(Px(a, 400, y0), 255, 153, 0), $"effect-passes: glow r{radius} behind the box: box unchanged ({Px(a, 400, y0)})");
            if (radius == 4)
                gpu(v[0] > 6 && v[1] < 3 && v[3] < 1, $"effect-passes: glow r4 at the edge only ({Profile(a)})");
            else if (radius == 32)
                gpu(v[1] > 20 && v[2] > 5 && v[4] < 1, $"effect-passes: glow r32 reaches ~30 DIP, not 100 ({Profile(a)})");
            else
                gpu(v[4] > 8 && v[5] > 2, $"effect-passes: glow r256 reaches 200 DIP ({Profile(a)})");
            if (radius < 256) // r256 reaches past the window either way
            {
                Chain(GlowLink(radius, 3 * radius));
                CheckApply($"glow r{radius} padding 3r");
                using var b = Shot($"glow_r{radius}_pad3r");
                gpu(Same(v, Far(b)), $"effect-passes: glow r{radius}: padding ceil(1.5 r) cuts nothing (1.5 r: {Profile(a)}; 3 r: {Profile(b)})");
            }
        }
        var err = PollError();
        check(string.IsNullOrEmpty(err), $"effect-passes: glow links without EV_ERROR ({err})");

        // Radius sweep over two step boundaries (step 4.5 .. 6.5 in 0.1), padding fixed: weights only, no new item, no jump.
        var padMax = MathF.Ceiling(1.5f * RadiusOf(6.5f, dpr));
        Chain(GlowLink(RadiusOf(4.5f, dpr), padMax));
        CheckApply("sweep start");
        pump(4);
        var itemsSweep = Items(w);
        var points = new[] { (Name: "d10", D: 10f), (Name: "d40", D: 40f), (Name: "d100", D: 100f) };
        var rows = new List<float[]>();
        for (int i = 0; i <= 20; i++)
        {
            var level = 4.5f + i * 0.1f;
            Chain(GlowLink(RadiusOf(level, dpr), padMax));
            CheckApply("glow sweep");
            pump(3);
            using var a = grab(w, Path.Combine(outDir, "effect-passes_glow_sweep.png"));
            rows.Add(points.Select(p => Glow(a, edge + p.D, y0)).ToArray());
        }
        Console.WriteLine("INFO effect-passes glow sweep: " + string.Join(" | ", rows.Select(v => string.Join(" ", v.Select(x => x.ToString("F1"))))));
        check(Items(w) == itemsSweep, $"effect-passes: radius sweep writes weights only, no new item ({Items(w)} vs {itemsSweep})");
        for (int j = 0; j < points.Length; j++)
        {
            var col = rows.Select(v => v[j]).ToArray();
            var range = col.Max() - col.Min();
            var maxStep = col.Zip(col.Skip(1), (p, q) => Math.Abs(q - p)).Max();
            gpu(maxStep <= Math.Max(0.2f * range, 3), $"effect-passes: glow sweep {points[j].Name}: no jump (largest step {maxStep:F1} of range {range:F1})");
        }
        var far = rows.Select(v => v[2]).ToArray();
        gpu(far.Zip(far.Skip(1), (p, q) => q - p).All(d => d > -1.5f) && far[^1] > far[0] + 5,
            $"effect-passes: glow sweep d100 grows with the radius ({far[0]:F1} -> {far[^1]:F1})");

        // Size change of the element: the steps follow (glow keeps its extent around the new edge).
        Chain(GlowLink(32));
        cmd.SetPlace(G, GX, GY, GW - 100, GH - 60);
        CheckApply("glow r32 small element");
        using (var a = Shot("glow_r32_small"))
        {
            var e2 = GX + GW - 100;
            gpu(Glow(a, e2 + 10, GY + 20) > 8 &&Glow(a, e2 + 100, GY + 20) < 1,
                $"effect-passes: glow r32 follows a size change ({Glow(a, e2 + 10, GY + 20):F1}, {Glow(a, e2 + 100, GY + 20):F1})");
        }
        cmd.SetPlace(G, GX, GY, GW, GH);
        CheckApply("element size back");

        // InnerShadow: inside only; offset (8, 8) shadows the left/top inner edge more than the right/bottom one.
        float Shade(Bitmap a, float x, float y) => 153 - Px(a, x, y).G; // orange G 153 -> darker
        Chain(ShadowLink(8, 0, 0));
        CheckApply("innershadow r8");
        using (var a = Shot("innershadow_r8"))
        {
            Console.WriteLine($"INFO effect-passes innershadow r8: L1 {Shade(a, GX + 1, y0):F0}, L4 {Shade(a, GX + 4, y0):F0}, centre {Px(a, 400, y0)}, outside {Px(a, GX - 4, y0)}");
            gpu(Shade(a, GX + 1, y0) > 20 && Shade(a, GX + GW - 2, y0) > 20 && Near(Px(a, 400, y0), 255, 153, 0) && IsBg(Px(a, GX - 4, y0)),
                $"effect-passes: innershadow r8 inside the edges only ({Shade(a, GX + 1, y0):F0}, {Shade(a, GX + GW - 2, y0):F0}, {Px(a, 400, y0)}, {Px(a, GX - 4, y0)})");
        }
        Chain(ShadowLink(8, 8, 8));
        CheckApply("innershadow r8 offset");
        using (var a = Shot("innershadow_r8_offset"))
            gpu(Shade(a, GX + 4, y0) > Shade(a, GX + GW - 5, y0) + 20 && Shade(a, 400, GY + 4) > Shade(a, 400, GY + GH - 5) + 20,
                $"effect-passes: innershadow offset (8, 8) at the left/top inner edge ({Shade(a, GX + 4, y0):F0} vs {Shade(a, GX + GW - 5, y0):F0}, {Shade(a, 400, GY + 4):F0} vs {Shade(a, 400, GY + GH - 5):F0})");
        err = PollError();
        check(string.IsNullOrEmpty(err), $"effect-passes: innershadow links without EV_ERROR ({err})");
        // The node's way (#438 phase B): the offset as offsetDip in DIP, converted by the shader (dpr, texture size); same picture as the UV offset above.
        float[] ShadeRow(Bitmap a) => new[] { GX + 2f, GX + 4f, GX + 10f, GX + GW - 5f }.Select(x => Shade(a, x, y0)).Concat(
            new[] { GY + 2f, GY + 4f, GY + 10f, GY + GH - 5f }.Select(y => Shade(a, 400, y))).ToArray();
        float[] viaUv, viaDip;
        Chain(ShadowLink(8, 8, 8));
        CheckApply("innershadow r8 offset again");
        using (var a = Shot("innershadow_r8_offset_uv"))
            viaUv = ShadeRow(a);
        Chain(c =>
        {
            var p = MathF.Ceiling(1.5f * 8);
            c.BeginShaderLink(Protocol.EffectTemplate.Pyramid, [isPre, isComp], p, p, p, p, 8, "");
            c.UniformVec2("offsetDip", 8, 8);
            c.UniformColor("color", 0, 0, 0, 1);
            c.UniformF32("spread", 0);
            c.UniformF32("intensity", 1);
            c.EndLink();
        });
        CheckApply("innershadow r8 offsetDip");
        using (var a = Shot("innershadow_r8_offset_dip"))
            viaDip = ShadeRow(a);
        gpu(viaUv.Zip(viaDip, (u, d) => Math.Abs(u - d) <= 3).All(b => b),
            $"effect-passes: innershadow offsetDip (8, 8) = UV offset ({string.Join(" ", viaUv.Select(x => x.ToString("F0")))} vs {string.Join(" ", viaDip.Select(x => x.ToString("F0")))})");

        // Mixed MULTI -> PYRAMID -> SINGLE: blurred box, glow around it, the wave bends the whole.
        Chain(c =>
        {
            c.BeginMultiLink();
            c.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
            c.EffectF32(Protocol.Prop.EffectBlur, 0.3f);
            c.EffectI32(Protocol.Prop.EffectBlurMax, 8);
            c.EndLink();
        }, GlowLink(24), c =>
        {
            c.BeginShaderLink(Protocol.EffectTemplate.Single, [wave], 20, 20, 20, 20, 0, "");
            c.UniformF32("time", 0);
            c.UniformF32("amplitude", 0.03f);
            c.UniformF32("frequency", 3);
            c.EndLink();
        });
        CheckApply("MULTI -> PYRAMID -> SINGLE");
        using (var a = Shot("mixed_multi-pyramid-single"))
        {
            Console.WriteLine($"INFO effect-passes mixed: {Profile(a)}, centre {Px(a, 400, y0)}");
            gpu(Glow(a, edge + 12, y0) > 8 && Px(a, 400, y0).R > 200, $"effect-passes: mixed chain shows box and glow ({Profile(a)}, {Px(a, 400, y0)})");
        }
        err = PollError();
        check(string.IsNullOrEmpty(err), $"effect-passes: mixed chain without EV_ERROR ({err})");

        // A name neither prefilter nor composite declares: EV_ERROR line, the apply succeeds.
        Chain(GlowLink(16, extra: "bogus"));
        CheckApply("unknown name");
        err = PollError();
        check(err != null && err.Contains("link 0: unknown uniform 'bogus'") && !err.Contains("'spread'"),
            $"effect-passes: unknown PYRAMID uniform -> EV_ERROR, known ones not ({err})");

        // Live changes, then no chain: item count as before.
        Chain(GlowLink(64), ShadowLink(4, 0, 0));
        CheckApply("glow -> innershadow");
        pump(3);
        Chain(ShadowLink(16, 4, 0));
        CheckApply("innershadow alone");
        pump(3);
        Chain(GlowLink(128));
        CheckApply("glow 128");
        pump(3);
        Chain();
        CheckApply("chain off");
        pump(6);
        var itemsAfter = Items(w);
        check(itemsAfter == itemsPlain, $"effect-passes: PYRAMID live changes without leak (items {itemsAfter} vs {itemsPlain})");

        cmd.Destroy(G);
        CheckApply("destroy");
        pump(3);
        Native.vlqt_window_destroy(w);
        foreach (var id in new[] { glowPre, glowComp, isPre, isComp })
            Native.vlqt_res_release(id);
        pump(2);
    }
}
