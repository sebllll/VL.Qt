using System;
using System.Drawing;
using System.IO;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// builtin:blend (#439, effects package 05): one pass, the layer blended over the input with a mode (W3C separable modes).
// A 5 x 3 grid of 140 x 100 DIP cells on 0x20 grey, each a ShaderEffect with Input = opaque base rectangle and a layer
// rectangle as layer (both fill the cell). Every cell is sampled in its center and compared with the reference
// formula below (tolerance +-2/255, the mode runs in the shader on straight colors).
//   cells 0..12: the 13 modes, opaque base (0.8, 0.4, 0.2) and opaque layer (0.3, 0.6, 0.9), opacity 1.
//   cell 13: Multiply, layer alpha 0 -> base unchanged.  cell 14: Normal, layer alpha 0.5, opacity 0.5 -> a quarter layer.
// Raw commands (DIP). Grab: <outDir>/blend.png.
[TestFixture, NonParallelizable]
public static class BlendTest
{
    [Test, Category("blend")]
    public static void Blend() => Scenario.Run(Run);

    private const float CellW = 140, CellH = 100;
    private static readonly string[] Names = { "Normal", "Multiply", "Screen", "Overlay", "Add", "Subtract", "Difference", "Darken", "Lighten", "SoftLight", "HardLight", "ColorDodge", "ColorBurn" };
    private static readonly float[] Base = { 0.8f, 0.4f, 0.2f };
    private static readonly float[] Layer = { 0.3f, 0.6f, 0.9f };

    private static (float x, float y) Cell(int i) => (10 + i % 5 * 150, 10 + i / 5 * 110);

    private static float HardLight(float cb, float cs) => cs <= 0.5f ? cb * 2 * cs : cb + (2 * cs - 1) - cb * (2 * cs - 1);

    private static float Blend(int mode, float cb, float cs)
    {
        switch (mode)
        {
            case 1: return cb * cs;
            case 2: return cb + cs - cb * cs;
            case 3: return HardLight(cs, cb);
            case 4: return Math.Min(cb + cs, 1);
            case 5: return Math.Max(cb - cs, 0);
            case 6: return Math.Abs(cb - cs);
            case 7: return Math.Min(cb, cs);
            case 8: return Math.Max(cb, cs);
            case 9:
                {
                    if (cs <= 0.5f) return cb - (1 - 2 * cs) * cb * (1 - cb);
                    var d = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : MathF.Sqrt(cb);
                    return cb + (2 * cs - 1) * (d - cb);
                }
            case 10: return HardLight(cb, cs);
            case 11: return cb == 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs));
            case 12: return cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs);
            default: return cs;
        }
    }

    // straight base (opaque) with a layer of alpha a: result = (1 - a) * base + a * B(base, layer)
    private static float Expect(int mode, int c, float a) => (1 - a) * Base[c] + a * Blend(mode, Base[c], Layer[c]);

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect needs a QRhi scenegraph");
        Directory.CreateDirectory(s.OutDir);

        Native.vlqt_res_create(Protocol.ResKind.Shader, out var blend);
        var rcLoad = Native.vlqt_res_load_shader(blend, "builtin:blend", Protocol.ShaderStage.Fragment);
        var sLoad = Native.vlqt_res_wait(blend, 10000);
        s.Check(rcLoad == 0 && sLoad == Protocol.ResState.Ready, $"builtin:blend loads -> Ready ({sLoad}, rc {rcLoad})");
        if (sLoad != Protocol.ResState.Ready) return;

        var w = Native.vlqt_window_create(800, 600, "VL.Qt blend test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(4);
        var cmd = new CommandWriter();
        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }

        uint next = 10;
        void Fx(int i, int mode, float layerAlpha, float opacity)
        {
            var (x, y) = Cell(i);
            uint fx = next++, bas = next++, layer = next++;
            cmd.Create(fx, Protocol.Type.ShaderEffect);
            cmd.SetPlace(fx, x, y, CellW, CellH);
            cmd.SetU64(fx, Protocol.Prop.FragmentShader, blend);
            cmd.Reparent(fx, 0, (uint)i);
            cmd.Create(bas, Protocol.Type.Rectangle);
            cmd.SetPlace(bas, 0, 0, CellW, CellH);
            cmd.SetColor(bas, Protocol.Prop.Color, Base[0], Base[1], Base[2], 1);
            cmd.Reparent(bas, fx, 0);
            cmd.SetRef(fx, Protocol.Prop.Source, bas);
            cmd.Create(layer, Protocol.Type.Rectangle);
            cmd.SetPlace(layer, 0, 0, CellW, CellH);
            cmd.SetColor(layer, Protocol.Prop.Color, Layer[0], Layer[1], Layer[2], layerAlpha);
            cmd.Reparent(layer, fx, 1);
            cmd.BeginUniforms(fx, Protocol.UniformSet.Clear);
            cmd.UniformI32("mode", mode);
            cmd.UniformF32("layerOpacity", opacity);
            cmd.UniformRef("layer", layer);
            cmd.EndUniforms();
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        for (var m = 0; m < Names.Length; m++) Fx(m, m, 1, 1);
        Fx(13, 1, 0, 1);
        Fx(14, 0, 0.5f, 0.5f);
        Apply("blend scene");
        s.Pump(8);

        using var a = s.Grab(w, "blend.png");
        var scale = a.Width / 800f;
        Color Sample(int i)
        {
            var (x, y) = Cell(i);
            return a.GetPixel((int)((x + CellW / 2) * scale), (int)((y + CellH / 2) * scale));
        }
        bool Near(Color c, int mode, float alpha) =>
            Math.Abs(c.R - Expect(mode, 0, alpha) * 255) <= 2 && Math.Abs(c.G - Expect(mode, 1, alpha) * 255) <= 2 && Math.Abs(c.B - Expect(mode, 2, alpha) * 255) <= 2;
        string Want(int mode, float alpha) => $"({Expect(mode, 0, alpha) * 255:F0}, {Expect(mode, 1, alpha) * 255:F0}, {Expect(mode, 2, alpha) * 255:F0})";

        for (var m = 0; m < Names.Length; m++)
        {
            var c = Sample(m);
            gpu(Near(c, m, 1), $"{Names[m]} opaque layer: ({c.R}, {c.G}, {c.B}) expected {Want(m, 1)}");
        }
        var none = Sample(13);
        gpu(Near(none, 1, 0), $"transparent layer leaves the base unchanged: ({none.R}, {none.G}, {none.B}) expected {Want(1, 0)}");
        var quarter = Sample(14);
        gpu(Near(quarter, 0, 0.25f), $"Normal, layer alpha 0.5 x opacity 0.5 = a quarter layer: ({quarter.R}, {quarter.G}, {quarter.B}) expected {Want(0, 0.25f)}");

        cmd.Reset();
        for (uint id = 10; id < next; id += 3) cmd.Destroy(id);
        Apply("destroy");
        s.Pump(3);
        Native.vlqt_window_destroy(w);
        Native.vlqt_res_release(blend);
        s.Pump(2);
    }
}
