using System;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Qt;
using Color = System.Drawing.Color;

// Effect layer mask in QtNative (plan effects/01 Task 3, since #489 a MULTI entry of SET_EFFECT_CHAIN):
// EFFECT_MASK_SOURCE attaches an element object (no REPARENT) as hidden texture source. Text as mask over a red rectangle in a Row (layout unchanged, mask not drawn itself),
// Inverted, detach (ref 0), position rule (Task 3c: the mask lies in the parent's coordinates like a sibling, with its
// own Bounds and Transform: same pixels as the sibling; moving/transforming the element, blur/auto padding and explicit
// padding leave the window in place; Row layout position counts; unstretched), DESTROY of the attached mask without
// ref 0, DESTROY of the masked element (takes its mask, id free again), -8 cases, RESET.
// Grabs: <outDir>/effect_mask*.png.
static class EffectMaskTest
{
    static Color Px(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
    static bool IsBg(Color c) => Math.Abs(c.R - 0x20) < 10 && Math.Abs(c.G - 0x20) < 10 && Math.Abs(c.B - 0x20) < 10;
    static bool IsRed(Color c) => c.R > 200 && c.G < 60 && c.B < 60;
    static bool IsWhite(Color c) => c.R > 200 && c.G > 200 && c.B > 200;

    // Samples of R's area (window 50..450 x 50..250): red, background, other (antialiased text edges).
    static bool[] RedSamples(Bitmap a, out int red, out int bg)
    {
        var s = new bool[40 * 20];
        red = bg = 0;
        for (int j = 0; j < 20; j++)
            for (int i = 0; i < 40; i++)
            {
                var c = Px(a, 55 + i * 10, 55 + j * 10);
                s[j * 40 + i] = IsRed(c);
                if (IsRed(c)) red++;
                else if (IsBg(c)) bg++;
            }
        return s;
    }

    static int WhiteCount(Bitmap a)
    {
        int n = 0;
        for (int y = 0; y < 600; y += 6)
            for (int x = 0; x < 800; x += 6)
                if (IsWhite(Px(a, x, y))) n++;
        return n;
    }

    // R > 128 on a 4 px grid: white text (sibling) resp. red through the mask; background and green are 0x20 / 0.
    static bool[] Lit(Bitmap a)
    {
        var s = new bool[200 * 150];
        for (int j = 0; j < 150; j++)
            for (int i = 0; i < 200; i++)
                s[j * 200 + i] = Px(a, i * 4, j * 4).R > 128;
        return s;
    }

    static double IoU(bool[] a, bool[] b)
    {
        int both = 0, any = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] && b[i]) both++;
            if (a[i] || b[i]) any++;
        }
        return any == 0 ? 0 : (double)both / any;
    }

    // MULTI run of one element (#489): the values the steps change, sent whole by SET_EFFECT_CHAIN.
    sealed class FxRun
    {
        public uint Mask;
        public bool Inverted, Blur, Padding;

        public void Write(CommandWriter c)
        {
            if (Mask != 0)
                c.EffectRef(Protocol.Prop.EffectMaskSource, Mask);
            if (Inverted)
                c.EffectBool(Protocol.Prop.EffectMaskInverted, true);
            if (Blur)
            {
                c.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
                c.EffectF32(Protocol.Prop.EffectBlur, 0.1f);
            }
            if (Padding)
            {
                c.EffectBool(Protocol.Prop.EffectAutoPadding, false);
                c.EffectRect(Protocol.Prop.EffectPaddingRect, 200, 0, 0, 0);
            }
        }
    }

    const uint Row = 1, R = 2, B = 3, T = 4, M2 = 5, M2Rect = 6, T2 = 7, R2 = 8, P = 9, X = 10, X2 = 11, Q = 12;

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var gpu = Program.NeedsRhi(check, "MultiEffect is a ShaderEffect");
        var w = Native.vlqt_window_create(800, 600, "VL.Qt effect mask test");
        check(w != IntPtr.Zero, "effect-mask: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"effect-mask: {what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
        }
        int Rc()
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            cmd.Reset();
            return rc;
        }
        void Fx(uint id, FxRun run) => cmd.Chain(id, run.Write);
        var r = new FxRun();
        var q = new FxRun();

        // Scene: Row (50, 50) with R (red 400x200, effect layer on) and B (green 100x200). Mask T: Text "MASK", white,
        // 400x200, created without REPARENT (like the reconciler), attached to R.
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(Row, Protocol.Type.Row);
        cmd.SetPlace(Row, 50, 50, 0, 0);
        cmd.Reparent(Row, 0, 0);
        cmd.Create(R, Protocol.Type.Rectangle);
        cmd.SetPlace(R, 0, 0, 400, 200);
        cmd.SetColor(R, Protocol.Prop.Color, 1, 0, 0, 1);
        cmd.Reparent(R, Row, 0);
        cmd.Create(B, Protocol.Type.Rectangle);
        cmd.SetPlace(B, 0, 0, 100, 200);
        cmd.SetColor(B, Protocol.Prop.Color, 0, 1, 0, 1);
        cmd.Reparent(B, Row, 1);
        CheckApply("scene");
        pump(5);
        Color bColor;
        using (var plain = grab(w, Path.Combine(outDir, "effect_mask_plain.png")))
            bColor = Px(plain, 500, 150);

        cmd.Create(T, Protocol.Type.Text);
        cmd.SetPlace(T, 0, 0, 400, 200);
        cmd.SetString(T, Protocol.Prop.Text, "MASK");
        cmd.SetF32(T, Protocol.Prop.FontSize, 120);
        cmd.SetColor(T, Protocol.Prop.Color, 1, 1, 1, 1);
        r.Mask = T;
        Fx(R, r);
        CheckApply("attach text mask");
        pump(5);
        bool[] masked;
        using (var a = grab(w, Path.Combine(outDir, "effect_mask.png")))
        {
            masked = RedSamples(a, out var red, out var bg);
            gpu(red > 40 && bg > 200, $"effect-mask: text as window on the rectangle ({red} red, {bg} background samples of 800)");
            check(WhiteCount(a) == 0, $"effect-mask: mask element itself not drawn ({WhiteCount(a)} white samples)");
            var bc = Px(a, 500, 150);
            check(Math.Abs(bc.R - bColor.R) < 6 && Math.Abs(bc.G - bColor.G) < 6, $"effect-mask: Row layout unchanged, B in place ({bc} vs {bColor})");
        }

        // A REPARENT of the attached mask is -8.
        cmd.Reparent(T, 0, 0);
        var rcRe = Rc();
        check(rcRe == -8, $"effect-mask: REPARENT of an attached mask -> -8 ({rcRe})");

        // Inverted: the complement of the text.
        r.Inverted = true;
        Fx(R, r);
        CheckApply("inverted");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_inverted.png")))
        {
            var inv = RedSamples(a, out var red, out _);
            int flipped = 0;
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] != masked[i]) flipped++;
            gpu(red > 400 && flipped > 700, $"effect-mask: inverted ({red} red, {flipped} of 800 samples flipped)");
            check(WhiteCount(a) == 0, "effect-mask: VISIBLE true on the mask does not show it");
        }
        r.Inverted = false;

        // Detach (no mask in the run): the full rectangle again, the mask object stays hidden until its DESTROY.
        r.Mask = 0;
        Fx(R, r);
        CheckApply("detach");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_detached.png")))
        {
            // The chain stays, so R keeps its (now unmasked) effect layer: drawn only with RHI.
            var fx = EffectChainTest.Counts(w);
            check(fx.layers == 1 && fx.effects == 1, $"effect-mask: detached -> effect layer stays on ({fx})");
            check(WhiteCount(a) == 0, $"effect-mask: detached mask stays hidden ({WhiteCount(a)} white)");
            RedSamples(a, out var red, out _);
            gpu(red == 800, $"effect-mask: detached -> full rectangle ({red} red)");
        }
        cmd.Destroy(T);
        CheckApply("destroy detached mask");

        // Sibling rule (Task 3c): the mask lies in the coordinates of the element's parent like a sibling, with its
        // own Bounds and Transform. Group P at (20, 20) (Row at Opacity 0): text X as a sibling in P, the same text X2 as mask
        // of the red rectangle Q in P -> the lit pixels (R > 128: white text resp. red through the mask) coincide.
        cmd.SetF32(Row, Protocol.Prop.Opacity, 0);
        cmd.Create(P, Protocol.Type.Item);
        cmd.SetBool(P, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(P, 20, 20, 760, 560);
        cmd.Reparent(P, 0, 0);
        foreach (var t in new[] { X, X2 })
        {
            cmd.Create(t, Protocol.Type.Text);
            cmd.SetString(t, Protocol.Prop.Text, "AB");
            cmd.SetF32(t, Protocol.Prop.FontSize, 90);
            cmd.SetPlace(t, 280, 180, 200, 110);
            cmd.SetColor(t, Protocol.Prop.Color, 1, 1, 1, 1);
        }
        cmd.Reparent(X, P, 0);
        CheckApply("sibling text");
        pump(5);
        bool[] sib0;
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_sibling.png")))
            sib0 = Lit(a);

        // (a) the same text as mask over a big red rectangle Q (sibling X hidden).
        cmd.SetF32(X, Protocol.Prop.Opacity, 0);
        cmd.Create(Q, Protocol.Type.Rectangle);
        cmd.SetPlace(Q, 120, 100, 560, 360);
        cmd.SetColor(Q, Protocol.Prop.Color, 1, 0, 0, 1);
        cmd.Reparent(Q, P, 1);
        q.Mask = X2;
        Fx(Q, q);
        CheckApply("text mask on Q");
        pump(5);
        void Same(string png, bool[] expected, string what, double min = 0.8)
        {
            using var a = grab(w, Path.Combine(outDir, png));
            var iou = IoU(expected, Lit(a));
            gpu(iou > min && WhiteCount(a) == 0, $"effect-mask: {what} (IoU {iou:F2} with the sibling)");
        }
        Same("effect_mask_sibling_masked.png", sib0, "mask covers exactly the sibling's pixels");

        // (b) moving the masked rectangle leaves the window in place.
        cmd.SetPlace(Q, 60, 40, 560, 360);
        CheckApply("move Q");
        pump(5);
        Same("effect_mask_sibling_moved.png", sib0, "masked element moved, window stays");

        // (c) Transform (scale 1.5 + translation) on the mask = the same on the sibling.
        var xf = Matrix.Scaling(1.5f) * Matrix.Translation(40, 30, 0);
        cmd.SetPlaceXf(X, 280, 180, 200, 110, xf);
        cmd.SetPlaceXf(X2, 280, 180, 200, 110, xf);
        cmd.SetF32(X, Protocol.Prop.Opacity, 1);
        cmd.SetF32(Q, Protocol.Prop.Opacity, 0);
        CheckApply("transform on the texts");
        pump(5);
        bool[] sib1;
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_sibling_xf.png")))
            sib1 = Lit(a);
        var moved = IoU(sib0, sib1);
        check(moved < 0.5, $"effect-mask: transformed sibling differs from the plain one (IoU {moved:F2})");
        cmd.SetF32(X, Protocol.Prop.Opacity, 0);
        cmd.SetF32(Q, Protocol.Prop.Opacity, 1);
        CheckApply("mask with transform");
        pump(5);
        Same("effect_mask_sibling_xf_masked.png", sib1, "mask with own Transform covers the transformed sibling's pixels");

        // Transform of the masked element (rotation + scale + translation): the window stays as well.
        cmd.SetPlaceXf(Q, 60, 40, 560, 360, Matrix.RotationZ(0.15f) * Matrix.Scaling(1.1f) * Matrix.Translation(25, -15, 0));
        CheckApply("transform on Q");
        pump(5);
        Same("effect_mask_sibling_qxf.png", sib1, "masked element rotated/scaled/moved, window stays");

        // (d) Inverted: Q red everywhere except the sibling's pixels.
        q.Inverted = true;
        Fx(Q, q);
        CheckApply("inverted sibling mask");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_sibling_inverted.png")))
        {
            var inv = Lit(a);
            int text = 0, hit = 0, lit = 0;
            for (int i = 0; i < inv.Length; i++)
            {
                if (inv[i]) lit++;
                if (sib1[i]) { text++; if (inv[i]) hit++; }
            }
            gpu(text > 50 && hit < text / 10 && lit > 3 * text,
                $"effect-mask: inverted ({hit} of {text} text samples lit, {lit} lit in total)");
        }
        q.Inverted = false;

        // (e) Blur + auto padding: the effect area grows, the window does not move.
        q.Blur = true;
        Fx(Q, q);
        CheckApply("blur with auto padding");
        pump(5);
        Same("effect_mask_sibling_blur.png", sib1, "blur + auto padding: window stays", 0.75);
        q.Blur = false;

        // Explicit padding (left 200, AUTO_PADDING off): the window stays too.
        q.Padding = true;
        Fx(Q, q);
        CheckApply("padding");
        pump(5);
        Same("effect_mask_sibling_padding.png", sib1, "padding left 200: window stays");
        q.Mask = 0;
        Fx(Q, q);
        cmd.Destroy(P);
        cmd.Destroy(X2);
        cmd.SetF32(Row, Protocol.Prop.Opacity, 1);
        CheckApply("detach + destroy sibling scene");

        // Unstretched: mask M2 100x50 at (100, 50) with a white 50x50 square on its left half -> red only on R's
        // 100..150 x 50..100, background elsewhere (also on the rest of M2).
        cmd.Create(M2, Protocol.Type.Item);
        cmd.SetBool(M2, Protocol.Prop.AutoSize, false);
        cmd.SetPlace(M2, 100, 50, 100, 50);
        cmd.Create(M2Rect, Protocol.Type.Rectangle);
        cmd.SetPlace(M2Rect, 0, 0, 50, 50);
        cmd.SetColor(M2Rect, Protocol.Prop.Color, 1, 1, 1, 1);
        cmd.Reparent(M2Rect, M2, 0);
        r.Mask = M2;
        Fx(R, r);
        CheckApply("attach square mask");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_square.png")))
        {
            Color inSq = Px(a, 175, 125), rightHalf = Px(a, 225, 125), left = Px(a, 100, 125), below = Px(a, 175, 200);
            gpu(IsRed(inSq) && IsBg(rightHalf) && IsBg(left) && IsBg(below) && WhiteCount(a) == 0,
                $"effect-mask: mask at its position, unstretched ({inSq}, {rightHalf}, {left}, {below})");
        }
        // Resizing the element keeps the mask in place (R 300x200: B moves, square unchanged).
        cmd.SetPlace(R, 0, 0, 300, 200);
        CheckApply("resize R");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_square_resized.png")))
        {
            Color inSq = Px(a, 175, 125), rightHalf = Px(a, 225, 125);
            gpu(IsRed(inSq) && IsBg(rightHalf), $"effect-mask: element resized, mask in place ({inSq}, {rightHalf})");
        }
        // Row layout: B first moves R to x 100 in the Row, the mask (Row coordinates) stays: square still at 150..200
        // on screen, nothing at R's old-relative place 250..300.
        cmd.Reparent(B, Row, 0);
        CheckApply("B before R in the Row");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_square_row.png")))
        {
            Color inSq = Px(a, 175, 125), relative = Px(a, 275, 125);
            gpu(IsRed(inSq) && IsBg(relative), $"effect-mask: layout position of R counts, mask in place ({inSq}, {relative})");
        }
        cmd.Reparent(B, Row, 1);
        cmd.SetPlace(R, 0, 0, 400, 200);

        // DESTROY of the attached mask without ref 0: detached first, full rectangle in the next frame.
        cmd.Destroy(M2);
        CheckApply("destroy attached mask");
        pump(5);
        using (var a = grab(w, Path.Combine(outDir, "effect_mask_destroyed.png")))
        {
            // As after the detach: the layer stays on (chain), the software backend does not draw it.
            var fx = EffectChainTest.Counts(w);
            check(fx.layers == 1 && fx.effects == 1, $"effect-mask: destroyed mask -> effect layer stays on ({fx})");
            RedSamples(a, out var red, out _);
            gpu(red == 800, $"effect-mask: destroyed mask -> full rectangle ({red} red)");
        }
        r.Mask = 0;
        Fx(R, r);
        CheckApply("no mask after the mask is gone (no-op)");

        // -8: an element with a parent (B), the element itself, its ancestor (Row), a mask attached elsewhere, the same
        // mask in two runs.
        Fx(R, new FxRun { Mask = B });
        var rcParent = Rc();
        Fx(R, new FxRun { Mask = R });
        var rcSelf = Rc();
        Fx(R, new FxRun { Mask = Row });
        var rcAnc = Rc();
        check(rcParent == -8 && rcSelf == -8 && rcAnc == -8, $"effect-mask: parented/self/ancestor -> -8 ({rcParent}, {rcSelf}, {rcAnc})");
        cmd.Create(T2, Protocol.Type.Text);
        cmd.SetString(T2, Protocol.Prop.Text, "Q");
        cmd.SetF32(T2, Protocol.Prop.FontSize, 120);
        cmd.SetPlace(T2, 0, 0, 400, 200);
        r.Mask = T2;
        Fx(R, r);
        CheckApply("attach T2");
        cmd.Chain(R, r.Write, r.Write);
        var rcTwoRuns = Rc();
        check(rcTwoRuns == -8, $"effect-mask: same mask in two runs -> -8 ({rcTwoRuns})");
        cmd.Create(R2, Protocol.Type.Rectangle);
        cmd.SetPlace(R2, 50, 350, 100, 100);
        cmd.SetColor(R2, Protocol.Prop.Color, 0, 0, 1, 1);
        cmd.Reparent(R2, 0, 1);
        CheckApply("R2");
        Fx(R2, new FxRun { Mask = T2 });
        var rcTwice = Rc();
        check(rcTwice == -8, $"effect-mask: mask attached to another element -> -8 ({rcTwice})");
        pump(3);

        // REPARENT index on the root ignores the parked mask: R2 to index 0 = before the Row.
        cmd.Reparent(R2, 0, 0);
        CheckApply("reparent R2 to root index 0");

        // DESTROY of the masked element takes its mask (id free again: CREATE with the same id works).
        cmd.Destroy(Row);
        CheckApply("destroy masked element with its parent");
        pump(3);
        cmd.Destroy(T2);
        var rcGone = Rc();
        check(rcGone == -4, $"effect-mask: mask went with its element ({rcGone})");
        cmd.Create(T2, Protocol.Type.Text);
        CheckApply("mask id free again");

        // Attached mask at RESET and window destroy.
        cmd.SetString(T2, Protocol.Prop.Text, "Q");
        cmd.SetPlace(T2, 0, 0, 100, 100);
        Fx(R2, new FxRun { Mask = T2 });
        CheckApply("attach before RESET");
        pump(2);
        cmd.ResetScene();
        CheckApply("RESET with attached mask");
        pump(2);
        cmd.Create(R2, Protocol.Type.Rectangle);
        cmd.SetPlace(R2, 50, 350, 100, 100);
        cmd.Reparent(R2, 0, 0);
        cmd.Create(T2, Protocol.Type.Text);
        cmd.SetString(T2, Protocol.Prop.Text, "Q");
        Fx(R2, new FxRun { Mask = T2 });
        CheckApply("attach before window destroy");
        pump(2);
        Native.vlqt_window_destroy(w);
    }
}
