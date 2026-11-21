using System;
using System.Buffers.Binary;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt.Wire;
using VL.Qt;
using Color = System.Drawing.Color;
using static Pixels;

// Effect layer in QtNative (plan effects/01 Task 2, since #489 by SET_EFFECT_CHAIN): a chain of one MULTI run on the
// element's own item (layer.effect = one MultiEffect), raw commands. Blur + shadow + color adjust on a Text and a
// Rectangle in a Row; Row layout and hit-test unchanged; linkCount 0 removes the MultiEffect again (no leak, image as
// without effect); REPARENT indices ignore the effect sibling; two MULTI runs (the second run greys the first run's
// shadow, its effect area includes the first's padding; reversed = like one run); SET EFFECT_* -6; DESTROY with the
// effect on. Grabs: <outDir>/effect_layer*.png.
[TestFixture, NonParallelizable]
public static class EffectLayerTest
{
    [Test, Category("effect-layer")]
    public static void EffectLayer() => Scenario.Run(Run);

    private static bool Near(Color c, int r, int g, int b, int tol = 12) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    private const int ScenePage = 2;

    // Scene page (F5): "effects <layers> effect layers, <n> MultiEffect items" and "QML items <n>".
    private static (int layers, int effects, int items) Counts(SceneHandle w)
    {
        var text = NativeCalls.PerfPageText(w, ScenePage);
        var fx = Regex.Match(text, @"effects\s+(\d+)\s+effect layers, (\d+) MultiEffect items");
        var it = Regex.Match(text, @"QML items\s+(\d+)");
        return (fx.Success ? int.Parse(fx.Groups[1].Value) : -1, fx.Success ? int.Parse(fx.Groups[2].Value) : -1,
            it.Success ? int.Parse(it.Groups[1].Value) : -1);
    }

    // Text area statistics: white-ish (text), green-ish (shadow), soft (neither text, shadow nor background: blur).
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

    // Hover events (EVENT_HOVER) for feedbackId in the poll buffer: last flag, or null if none.
    private static bool? HoverFlag(ReadOnlySpan<byte> b, uint feedbackId)
    {
        bool? flag = null;
        int off = 0;
        while (off + 12 <= b.Length)
        {
            var ev = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off));
            var len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off + 2));
            if (len < 8 || off + 4 + len > b.Length) break;
            if (ev == Protocol.EventId.Hover && BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(off + 4)) == feedbackId && len > 8)
                flag = b[off + 12] != 0;
            off += (4 + len + 3) & ~3;
        }
        return flag;
    }

    private const uint Row = 1, A = 2, B = 3, T = 4, H = 10, FeedbackIdH = 31;

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "MultiEffect is a ShaderEffect");
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt effect layer test");
        s.Check(w != SceneHandle.Null, "effect-layer: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var poll = new byte[1 << 16];
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"effect-layer: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        bool? Hover(float x, float y)
        {
            NativeTest.vlqt_test_send_mouse(w, TestProtocol.Mouse.Move, x, y);
            s.Pump(1);
            var n = NativeCalls.Poll(w, poll);
            return HoverFlag(poll.AsSpan(0, Math.Max(n, 0)), FeedbackIdH);
        }
        // A = hard red shadow (30, 30) + saturation -1 (grey); T = blur + green shadow + brightness.
        static void RunA(CommandWriter c)
        {
            c.EffectBool(Protocol.MultiEffectKey.ShadowEnabled, true);
            c.EffectColor(Protocol.MultiEffectKey.ShadowColor, 1, 0, 0, 1);
            c.EffectF32(Protocol.MultiEffectKey.ShadowHorizontalOffset, 30);
            c.EffectF32(Protocol.MultiEffectKey.ShadowVerticalOffset, 30);
            c.EffectF32(Protocol.MultiEffectKey.ShadowBlur, 0);
            c.EffectF32(Protocol.MultiEffectKey.Saturation, -1);
        }
        static void RunT(CommandWriter c)
        {
            c.EffectBool(Protocol.MultiEffectKey.BlurEnabled, true);
            c.EffectF32(Protocol.MultiEffectKey.Blur, 0.6f);
            c.EffectI32(Protocol.MultiEffectKey.BlurMax, 16);
            c.EffectBool(Protocol.MultiEffectKey.ShadowEnabled, true);
            c.EffectColor(Protocol.MultiEffectKey.ShadowColor, 0, 1, 0, 1);
            c.EffectF32(Protocol.MultiEffectKey.ShadowHorizontalOffset, 20);
            c.EffectF32(Protocol.MultiEffectKey.ShadowVerticalOffset, 20);
            c.EffectF32(Protocol.MultiEffectKey.ShadowBlur, 0);
            c.EffectF32(Protocol.MultiEffectKey.Brightness, 0.2f);
        }
        void EffectA(bool on) { if (on) cmd.Chain(A, RunA); else cmd.Chain(A); }
        void EffectT(bool on) { if (on) cmd.Chain(T, RunT); else cmd.Chain(T); }

        // Scene: Row (50, 50) with A (blue 200x150, hover handler) and B (green 100x150); Text "Fx" at (450, 300).
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Row, Protocol.ElementType.Row);
        cmd.SetPlace(Row, 50, 50, 0, 0);
        cmd.Reparent(Row, 0, 0);
        cmd.Create(A, Protocol.ElementType.Rectangle);
        cmd.SetPlace(A, 0, 0, 200, 150);
        cmd.SetColor(A, Protocol.PropertyId.Color, 0, 0, 1, 1);
        cmd.Reparent(A, Row, 0);
        cmd.Create(B, Protocol.ElementType.Rectangle);
        cmd.SetPlace(B, 0, 0, 100, 150);
        cmd.SetColor(B, Protocol.PropertyId.Color, 0, 1, 0, 1);
        cmd.Reparent(B, Row, 1);
        cmd.Create(H, Protocol.ElementType.HoverHandler);
        cmd.Bind(H, FeedbackIdH, 0);
        cmd.Reparent(H, A, 0);
        cmd.Create(T, Protocol.ElementType.Text);
        cmd.SetPlace(T, 450, 300, 300, 220);
        cmd.SetString(T, Protocol.PropertyId.Text, "Fx");
        cmd.SetF32(T, Protocol.PropertyId.FontSize, 150);
        cmd.SetColor(T, Protocol.PropertyId.Color, 1, 1, 1, 1);
        cmd.Reparent(T, 0, 1);
        CheckApply("scene");
        s.Pump(5);
        var before = Counts(w);
        s.Check(before.layers == 0 && before.effects == 0 && before.items > 0, $"effect-layer: no effect -> no layer, no MultiEffect ({before})");
        using var plain = s.Grab(w, "effect_layer_off.png");
        var plainText = TextStats(plain);
        var bColor = Px(plain, 300, 125); // B in the Row (Row spacing default)
        s.Check(Near(Px(plain, 150, 125), 0, 0, 255) && !IsBg(bColor) && IsBg(Px(plain, 150, 215)) && plainText.white > 50,
            $"effect-layer: plain scene ({Px(plain, 150, 125)}, {bColor}, {Px(plain, 150, 215)}, {plainText})");

        // On: chain of one run on A and on T.
        EffectA(true);
        EffectT(true);
        CheckApply("effects on");
        s.Pump(5);
        var on = Counts(w);
        s.Check(on.layers == 2 && on.effects == 2, $"effect-layer: two elements -> exactly one MultiEffect each ({on})");
        using (var a = s.Grab(w, "effect_layer.png"))
        {
            var ac = Px(a, 150, 125);
            gpu(Math.Abs(ac.R - ac.B) < 30 && Math.Abs(ac.G - ac.B) < 30 && ac.B < 120, // pure blue -> dark grey (luminance)
                $"effect-layer: saturation -1 -> A grey ({ac})");
            gpu(Near(Px(a, 150, 215), 255, 0, 0, 40) && IsBg(Px(a, 40, 125)),
                $"effect-layer: red shadow below A ({Px(a, 150, 215)}, {Px(a, 40, 125)})");
            s.Check(Px(a, 300, 125) == bColor || Near(Px(a, 300, 125), bColor.R, bColor.G, bColor.B, 4),
                $"effect-layer: Row layout unchanged, B in place ({Px(a, 300, 125)} vs {bColor})");
            var st = TextStats(a);
            gpu(st.green > 50 && st.soft > plainText.soft + 50,
                $"effect-layer: text shadow + blur ({st} vs plain {plainText})");
        }

        // Hit-test: inside A hovered, the shadow area below A is not.
        s.Check(Hover(150, 125) == true, "effect-layer: hover inside A with effect");
        s.Check(Hover(150, 215) == false, "effect-layer: shadow area is not A (hover leaves)");

        // REPARENT indices count elements only (the effect sibling of A does not): B to index 0 = first in the Row.
        cmd.Reparent(B, Row, 0);
        CheckApply("reparent B to index 0");
        s.Pump(5);
        using (var a = s.Grab(w, "effect_layer_reorder.png"))
            s.Check(Near(Px(a, 100, 125), bColor.R, bColor.G, bColor.B, 8), $"effect-layer: B first in the Row ({Px(a, 100, 125)})");
        cmd.Reparent(B, Row, 1);
        CheckApply("reparent B back to index 1");
        s.Pump(5);

        // Off: MultiEffects gone, item count as before, image as without effect.
        EffectA(false);
        EffectT(false);
        CheckApply("effects off");
        s.Pump(5);
        var off = Counts(w);
        s.Check(off.layers == 0 && off.effects == 0 && off.items == before.items, $"effect-layer: off -> no MultiEffect left ({off} vs {before})");
        using (var a = s.Grab(w, "effect_layer_off_again.png"))
        {
            int diff = 0;
            for (int y = 0; y < 600; y += 4)
                for (int x = 0; x < 800; x += 4)
                {
                    Color p = Px(plain, x, y), q = Px(a, x, y);
                    if (!Near(p, q.R, q.G, q.B, 6)) diff++;
                }
            s.Check(diff == 0, $"effect-layer: off -> same image as without effect ({diff} differing samples)");
        }

        // 20 on/off cycles: no MultiEffect or item left over.
        for (int i = 0; i < 20; i++)
        {
            EffectA(true);
            EffectT(true);
            CheckApply("cycle on");
            s.Pump(1);
            EffectA(false);
            EffectT(false);
            CheckApply("cycle off");
            s.Pump(1);
        }
        s.Pump(3);
        var cycled = Counts(w);
        s.Check(cycled.layers == 0 && cycled.effects == 0 && cycled.items == before.items, $"effect-layer: 20 on/off cycles leak nothing ({cycled})");
        EffectA(true);
        CheckApply("on again");
        s.Pump(5);
        using (var a = s.Grab(w, "effect_layer_again.png"))
            gpu(Near(Px(a, 150, 215), 255, 0, 0, 40), $"effect-layer: on again after the cycles ({Px(a, 150, 215)})");

        // Two MULTI runs: run 1 = the hard red shadow, run 2 = saturation -1. One run keeps the shadow red (above:
        // saturation acts on the source only); as a second run the saturation greys run 1's output, shadow included. The
        // shadow lies outside A (y 200..230): drawn only if run 2's effect area includes run 1's padding.
        static void ShadowRun(CommandWriter c) => Shadow(c, 1, 0, 0);
        static void BlueShadowRun(CommandWriter c) => Shadow(c, 0, 0, 1);
        static void Shadow(CommandWriter c, float r, float g, float b)
        {
            c.EffectBool(Protocol.MultiEffectKey.ShadowEnabled, true);
            c.EffectColor(Protocol.MultiEffectKey.ShadowColor, r, g, b, 1);
            c.EffectF32(Protocol.MultiEffectKey.ShadowHorizontalOffset, 30);
            c.EffectF32(Protocol.MultiEffectKey.ShadowVerticalOffset, 30);
            c.EffectF32(Protocol.MultiEffectKey.ShadowBlur, 0);
        }
        static void GreyRun(CommandWriter c) => c.EffectF32(Protocol.MultiEffectKey.Saturation, -1);
        cmd.Chain(A, ShadowRun, GreyRun);
        CheckApply("two runs");
        s.Pump(5);
        NativeCalls.Poll(w, poll); // the old one-run stage is retired and deleted after the next frame, at poll/apply
        s.Pump(2);
        var two = Counts(w);
        s.Check(two.layers == 1 && two.effects == 2, $"effect-layer: two runs on A -> one layer, two MultiEffects ({two})");
        using (var a = s.Grab(w, "effect_layer_two_runs.png"))
        {
            Color sc = Px(a, 150, 215), ac = Px(a, 150, 125);
            gpu(!IsBg(sc) && Math.Abs(sc.R - sc.G) < 30 && Math.Abs(sc.R - sc.B) < 30,
                $"effect-layer: two runs -> shadow of run 1 greyed by run 2 ({sc})");
            gpu(Math.Abs(ac.R - ac.B) < 30 && Math.Abs(ac.G - ac.B) < 30 && ac.B < 120, $"effect-layer: two runs -> A grey ({ac})");
        }
        // Reversed: saturation first, the shadow of the grey A stays red (as with one run).
        cmd.Chain(A, GreyRun, ShadowRun);
        CheckApply("two runs reversed");
        s.Pump(5);
        using (var a = s.Grab(w, "effect_layer_two_runs_reversed.png"))
            gpu(Near(Px(a, 150, 215), 255, 0, 0, 40), $"effect-layer: reversed runs -> red shadow ({Px(a, 150, 215)})");
        // Same structure, other values: no new item (written in place).
        var itemsBefore = Counts(w).items;
        cmd.Chain(A, GreyRun, BlueShadowRun);
        CheckApply("two runs, other shadow color");
        s.Pump(5);
        var itemsAfter = Counts(w).items;
        s.Check(itemsAfter == itemsBefore, $"effect-layer: same structure -> no new item ({itemsAfter} vs {itemsBefore})");
        using (var a = s.Grab(w, "effect_layer_two_runs_blue.png"))
            gpu(Near(Px(a, 150, 215), 0, 0, 255, 40), $"effect-layer: value changed in place -> blue shadow ({Px(a, 150, 215)})");
        EffectA(true);
        CheckApply("back to one run");
        s.Pump(3);

        // Errors: SET with an EFFECT_* property -> UnknownProperty (only via SET_EFFECT_CHAIN); a chain on a handler -> UnknownProperty.
        cmd.SetBool(A, Protocol.MultiEffectKey.ShadowEnabled, true);
        var rc1 = Native.Apply(w, cmd.EndFrame());
        cmd.Reset();
        s.Check(rc1 == Native.Result.UnknownProperty, $"effect-layer: SET EFFECT_SHADOW_ENABLED -> UnknownProperty ({rc1})");
        cmd.Chain(H, RunA);
        var rc2 = Native.Apply(w, cmd.EndFrame());
        cmd.Reset();
        s.Check(rc2 == Native.Result.UnknownProperty, $"effect-layer: effect chain on a handler -> UnknownProperty ({rc2})");

        // DESTROY with the effect on: its MultiEffect goes too.
        cmd.Destroy(A);
        CheckApply("destroy A with effect");
        s.Pump(3);
        var gone = Counts(w);
        s.Check(gone.layers == 0 && gone.effects == 0, $"effect-layer: destroyed element takes its MultiEffect ({gone})");
        EffectT(true);
        CheckApply("T on before window destroy");
        s.Pump(2);
        Native.vlqt_scene_destroy(w);
    }
}
