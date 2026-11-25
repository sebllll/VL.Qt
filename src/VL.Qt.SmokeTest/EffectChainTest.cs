using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using NUnit.Framework;
using VL.Qt.Reconciliation;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using static Pixels;

// Effect chain through the real nodes (plan effects/01, Task 5): Text/Rectangle/Row nodes + DropShadow/Blur/ColorAdjust/Mask
// nodes -> Reconciler -> CommandWriter -> real QtNative. One MultiEffect per chain, order of the chain without effect on the image,
// unchanged chain -> no commands, effect removed -> no layer, Row layout unchanged, mask at a position, duplicate kind -> warning.
// Node inputs are units (DIPTopLeft, 1 unit = 100 DIP), pixel checks are DIP. Grabs: <outDir>/effect_chain*.png.
[TestFixture, NonParallelizable]
public static class EffectChainTest
{
    [Test, Category("effect-chain")]
    public static void EffectChain() => Scenario.Run(Run);

    private static bool Near(Color c, int r, int g, int b, int tol = 12) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    private const int ScenePage = 2;

    internal static (int layers, int effects) Counts(SceneHandle w)
    {
        var text = NativeCalls.PerfPageText(w, ScenePage);
        var fx = Regex.Match(text, @"effects\s+(\d+)\s+effect layers, (\d+) MultiEffect items");
        return (fx.Success ? int.Parse(fx.Groups[1].Value) : -1, fx.Success ? int.Parse(fx.Groups[2].Value) : -1);
    }

    // Text area statistics: white-ish (text), green-ish (shadow), soft (neither: blur).
    private static (int white, int green, int soft) TextStats(Bitmap a)
    {
        int white = 0, green = 0, soft = 0;
        for (int y = 300; y < 560; y += 2)
            for (int x = 420; x < 790; x += 2)
            {
                var c = Px(a, x, y);
                if (c.R > 200 && c.G > 200 && c.B > 200) white++;
                else if (c.G > 120 && c.R < 90 && c.B < 90) green++;
                else if (!IsBg(c)) soft++;
            }
        return (white, green, soft);
    }

    private static int Diff(Bitmap a, Bitmap b)
    {
        int diff = 0;
        for (int y = 0; y < 600; y += 4)
            for (int x = 0; x < 800; x += 4)
            {
                Color p = Px(a, x, y), q = Px(b, x, y);
                if (!Near(p, q.R, q.G, q.B, 10)) diff++;
            }
        return diff;
    }

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "MultiEffect is a ShaderEffect");
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt effect chain test");
        s.Check(w != SceneHandle.Null, "effect-chain: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());

        var warnings = new List<string>();
        IDisposable Warn(string m) { warnings.Add(m); return new Remove(warnings, m); }

        var text = new TextNode(Warn);
        var rectA = new RectangleNode(Warn);
        var rectB = new RectangleNode(Warn);
        var rectM = new RectangleNode(Warn);
        var maskText = new TextNode(Warn);
        var row = new RowNode(Warn);
        var shadow = new DropShadowNode();
        var blur = new BlurNode();
        var adjust = new ColorAdjustNode();
        var mask = new MaskNode();
        var shadow2 = new DropShadowNode();
        var blur2 = new BlurNode();

        void Frame(Element root, string what, bool clear = false)
        {
            cmd.Reset();
            if (clear) cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            rec.Update(root, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"effect-chain: apply {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            s.Pump(4);
        }

        Element Text(TextNode n, string s, RectangleF bounds, float size, Effect? effect)
            => (Element)n.Update(bounds.Sz(), bounds.Tr(), RectangleAnchor.TopLeft, s, "", size, 400, false, new Color4(1, 1, 1, 1),
                TextHorizontalAlignment.Left, VerticalAlignment.Top, TextWrap.NoWrap, TextElide.None, 1, 0, TextAutofit.Fixed,
                1, false, false, null, effect, true, out _, out _);

        Element Rect(RectangleNode n, Color4 fill, RectangleF bounds, Effect? effect, out RectangleF sceneBounds)
            => (Element)n.Update(bounds.Sz(), bounds.Tr(), RectangleAnchor.TopLeft, fill, 0, new Color4(0, 0, 0, 1), 0, 1, false, false,
                null, effect, true, out _, out sceneBounds);

        // Effect pin <- DropShadow <- Blur <- ColorAdjust, or the reverse order of the same three links.
        Effect Chain(bool shadowFirst)
        {
            var steps = new Func<Effect?, Effect>[]
            {
                e => adjust.Update(e, 0.2f, 0, 0),
                e => blur.Update(e, 0.6f, 16, 0, true, default),
                e => shadow.Update(e, new Color4(0, 1, 0, 1), new Vector2(0.2f, 0.2f), 0, 1, true, default),
            };
            Effect? chain = null;
            if (shadowFirst) for (int i = 0; i < 3; i++) chain = steps[i](chain);
            else for (int i = 2; i >= 0; i--) chain = steps[i](chain);
            return chain!;
        }

        var textBounds = new RectangleF(4.5f, 3, 3, 2.2f);

        // 1) Plain text.
        Frame(Text(text, "Fx", textBounds, 1.5f, null), "plain", clear: true);
        var c0 = Counts(w);
        s.Check(c0.layers == 0 && c0.effects == 0, $"effect-chain: no Effect -> no layer ({c0})");
        using var plain = s.Grab(w, "effect_chain_off.png");
        var plainStats = TextStats(plain);

        // 2) DropShadow <- Blur <- ColorAdjust: exactly one MultiEffect, blur + shadow visible.
        Frame(Text(text, "Fx", textBounds, 1.5f, Chain(true)), "chain");
        var c1 = Counts(w);
        s.Check(c1.layers == 1 && c1.effects == 1, $"effect-chain: three effect nodes -> exactly one MultiEffect ({c1})");
        using var chainA = s.Grab(w, "effect_chain.png");
        var st = TextStats(chainA);
        gpu(st.green > 50 && st.soft > plainStats.soft + 50, $"effect-chain: blur + shadow visible ({st} vs plain {plainStats})");

        // 3) Unchanged chain over several frames: nothing is sent.
        int sent = 0;
        for (int i = 0; i < 5; i++)
        {
            cmd.Reset();
            rec.Update(Text(text, "Fx", textBounds, 1.5f, Chain(true)), Space.DIPTopLeft);
            sent += cmd.Length;
            s.Pump(1);
        }
        s.Check(sent == 0, $"effect-chain: unchanged chain over 5 frames sends no commands ({sent} bytes)");
        s.Check(Counts(w) == c1, "effect-chain: still one MultiEffect after 5 frames");

        // 4) Reversed order: same image.
        Frame(Text(text, "Fx", textBounds, 1.5f, Chain(false)), "reversed chain");
        var c2 = Counts(w);
        s.Check(c2.layers == 1 && c2.effects == 1, $"effect-chain: reversed chain -> still one MultiEffect ({c2})");
        using (var b = s.Grab(w, "effect_chain_reversed.png"))
        {
            var d = Diff(chainA, b);
            s.Check(d <= 20, $"effect-chain: reversed order -> same image ({d} differing samples)");
        }

        // 5) Effect removed: no layer, image as plain.
        Frame(Text(text, "Fx", textBounds, 1.5f, null), "effect removed");
        var c3 = Counts(w);
        s.Check(c3.layers == 0 && c3.effects == 0, $"effect-chain: Effect removed -> no layer ({c3})");
        using (var b = s.Grab(w, "effect_chain_removed.png"))
        {
            var d = Diff(plain, b);
            s.Check(d <= 20, $"effect-chain: removed -> image as without effect ({d} differing samples)");
        }

        // 6) Row layout: element with effect keeps layout and bounds (A with shadow, B next to it).
        var red = new Color4(1, 0, 0, 1);
        var blue = new Color4(0, 0, 1, 1);
        Element RowOf(Effect? fx, out RectangleF rb, out RectangleF ab, out RectangleF bb)
        {
            var a = Rect(rectA, blue, new RectangleF(0, 0, 2, 1.5f), fx, out ab);
            var b = Rect(rectB, red, new RectangleF(0, 0, 1, 1.5f), null, out bb);
            return (Element)row.Update(Spread.Create<IElement?>(a, b), new Vector2(0, 0), true, Matrix.Translation(0.5f, 0.5f, 0), RectangleAnchor.TopLeft,
                0.1f, 0, VerticalAlignment.Top, 1, false, false, null, null, true, out _, out rb);
        }
        // Row layout is checked on the image (the sceneBounds outputs only fill from QtNative events): extent of blue (A) and red (B) on y = 100.
        (int blue, int red) Extent(Bitmap a)
        {
            int bl = 0, rd = 0;
            for (int x = 0; x < 800; x++)
            {
                var c = Px(a, x, 100);
                if (Near(c, 0, 0, 255, 30)) bl = x;
                else if (Near(c, 255, 0, 0, 30)) rd = x;
            }
            return (bl, rd);
        }
        Frame(RowOf(null, out _, out _, out _), "row plain");
        using var rowPlain = s.Grab(w, "effect_chain_row_off.png");
        var e0 = Extent(rowPlain);
        var shadowFx = shadow2.Update(null, new Color4(0, 1, 0, 1), new Vector2(0.3f, 0.3f), 0, 1, true, default);
        Frame(RowOf(shadowFx, out _, out _, out _), "row with effect");
        var cRowOn = Counts(w);
        s.Check(cRowOn.layers == 1 && cRowOn.effects == 1, $"effect-chain: one effect in the Row -> one MultiEffect ({cRowOn})");
        using (var a = s.Grab(w, "effect_chain_row.png"))
        {
            var e1 = Extent(a);
            // B (no effect) is drawn on every backend; A sits in its effect layer, which the software backend does not
            // draw (MultiEffect is a ShaderEffect), so A's extent and pixels are only checked with RHI.
            s.Check(e0.blue > 0 && e0.red > e0.blue && e1.red == e0.red && Near(Px(a, 350, 100), 255, 0, 0, 40),
                $"effect-chain: Row layout equal with effect, B in place (B..x {e0.red} vs {e1.red}, {Px(a, 350, 100)})");
            gpu(e1 == e0 && Near(Px(a, 100, 100), 0, 0, 255, 40),
                $"effect-chain: A drawn in place through its layer (A..x {e0.blue} vs {e1.blue}, {Px(a, 100, 100)})");
            gpu(Near(Px(a, 100, 215), 0, 255, 0, 60), $"effect-chain: shadow below A ({Px(a, 100, 215)})");
        }
        Frame(RowOf(null, out _, out _, out _), "row effect off");
        var cRow = Counts(w);
        s.Check(cRow.layers == 0, $"effect-chain: Row child effect removed -> no layer ({cRow})");

        // 7) Mask node with a Text element at a position: window there (mask lies in the parent's coordinates, like a sibling).
        // Red 300 x 200 at (200, 200); text mask "MASK" at (220, 220), glyphs ~225..435 x 245..310 (to ~450 with the wider
        // DejaVu Sans of Linux): red only there.
        Element Masked(Element? m)
            => Rect(rectM, red, new RectangleF(2, 2, 3, 2), m is null ? null : mask.Update(null, m, 0, 0, 1, 0, false), out _);
        Frame(Masked(Text(maskText, "MASK", new RectangleF(2.2f, 2.2f, 2, 1), 0.8f, null)), "mask");
        using (var a = s.Grab(w, "effect_chain_mask.png"))
        {
            int inside = 0, outside = 0;
            for (int y = 200; y < 400; y += 2)
                for (int x = 200; x < 500; x += 2)
                {
                    var c = Px(a, x, y);
                    bool isRed = c.R > 200 && c.G < 60 && c.B < 60;
                    bool inBox = x >= 215 && x < 470 && y >= 215 && y < 330;
                    if (isRed && inBox) inside++;
                    else if (isRed) outside++;
                }
            gpu(inside > 40 && outside == 0, $"effect-chain: Mask node text -> window at its position ({inside} red in the text box, {outside} outside)");
        }
        Frame(Rect(rectM, red, new RectangleF(2, 2, 3, 2), null, out _), "mask removed");
        var cMask = Counts(w);
        s.Check(cMask.layers == 0, $"effect-chain: Mask removed -> no layer ({cMask})");

        // 8) Duplicate kind in the chain: warning at the element node (read at the next Update, from the last frame's feedback).
        warnings.Clear();
        var dup = blur2.Update(blur2.Update(null, 0.3f, 32, 0, true, default), 0.6f, 32, 0, true, default);
        Frame(Text(text, "Fx", textBounds, 1.5f, dup), "duplicate");
        Text(text, "Fx", textBounds, 1.5f, dup);
        s.Check(warnings.Count == 1 && warnings[0].Contains("Blur"), $"effect-chain: duplicate Blur -> warning at the Text node ({string.Join("|", warnings)})");
        var single = blur2.Update(null, 0.6f, 32, 0, true, default);
        Frame(Text(text, "Fx", textBounds, 1.5f, single), "duplicate gone");
        Text(text, "Fx", textBounds, 1.5f, single);
        s.Check(warnings.Count == 0, $"effect-chain: warning gone with the duplicate ({warnings.Count})");

        text.Dispose(); rectA.Dispose(); rectB.Dispose(); rectM.Dispose(); maskText.Dispose(); row.Dispose();
        cmd.Reset();
        rec.Clear();
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_scene_destroy(w);
        s.Pump(2);
    }

    private sealed class Remove(List<string> list, string message) : IDisposable
    {
        public void Dispose() => list.Remove(message);
    }
}
