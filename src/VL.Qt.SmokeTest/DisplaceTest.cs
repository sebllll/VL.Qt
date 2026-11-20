using System;
using System.Drawing;
using System.IO;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// builtin:displace (effects package 04, task A3): one-pass displacement of the input by a map element.
// 3 x 3 grid of 240 x 140 DIP cells on 0x20 grey; the source is a white Text "HAL", centered, with margin all around
// (the shift stays inside the cell). texelSize = 1 / pixel size of the cell (240 x 140 DIP x dpr) and dpr, set here like
// the pass driver of #489 does (doc 21: texelSize is per pixel, the shift in DIP).
//   (0,0) reference: the text without an effect.
//   (1,0) wave map (R = sin over y, G = sin over x), displacement 0 -> same picture as the reference.
//   (2,0) wave map, displacement 8; (0,1) wave map, displacement -8 (negative: other direction); (1,1) wave map, 20 (large).
//   (2,1) constant map R = 1, G = 0.5, alpha 1, displacement 24 -> all content moves exactly 24 DIP to the left.
//   (0,2) the same map with alpha 0.5 -> half the shift (12 DIP). (1,2) the map with alpha 0 -> no shift, no NaN smear.
//   (2,2) constant map R = 0 (pulls from the left), displacement 24 -> 24 DIP to the right.
// Raw commands (DIP). Grabs: <outDir>/displace.png, displace_live.png.
[TestFixture, NonParallelizable]
public static class DisplaceTest
{
    [Test, Category("displace")]
    public static void Displace() => Scenario.Run(Run);

    private const float CellW = 240, CellH = 140;
    private const uint Ref = 100;
    private const string WaveMap =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; };\n" +
        "void main() {\n" +
        "    vec2 uv = qt_TexCoord0;\n" +
        "    fragColor = vec4(0.5 + 0.5 * sin(uv.y * 9.4248), 0.5 + 0.5 * sin(uv.x * 12.5664), 0.5, 1.0) * qt_Opacity;\n" +
        "}\n";

    private static (float x, float y) Cell(int cx, int cy) => (20 + cx * 260, 20 + cy * 160);

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect needs a QRhi scenegraph");
        Directory.CreateDirectory(s.OutDir);

        Native.vlqt_res_create(Protocol.ResKind.Shader, out var displace);
        var rcLoad = Native.vlqt_res_load_shader(displace, "builtin:displace", Protocol.ShaderStage.Fragment);
        var sLoad = Native.vlqt_res_wait(displace, 10000);
        s.Check(rcLoad == 0 && sLoad == Protocol.ResState.Ready, $"builtin:displace loads -> Ready ({sLoad}, rc {rcLoad})");
        Native.vlqt_res_create(Protocol.ResKind.Shader, out var wave);
        var rcWave = Native.vlqt_res_load_shader_source(wave, WaveMap, Protocol.ShaderStage.Fragment);
        var sWave = rcWave == 0 ? Native.vlqt_res_wait(wave, 20000) : Protocol.ResState.Failed;
        s.Check(sWave == Protocol.ResState.Ready, $"wave map shader bakes -> Ready ({sWave}, rc {rcWave})");
        if (sLoad != Protocol.ResState.Ready || sWave != Protocol.ResState.Ready) return;

        var w = Native.vlqt_window_create(800, 600, "VL.Qt displace test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(4);
        float dpr;
        using (var probe = s.Grab(w, "displace_probe.png"))
            dpr = Math.Max(1f, probe.Width / 800f);
        s.Info($"displace: device pixel ratio {dpr:F2}");
        var cmd = new CommandWriter();
        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Hal(uint id, uint parent, uint index)
        {
            cmd.Create(id, Protocol.Type.Text);
            cmd.SetPlace(id, 0, 0, CellW, CellH);
            cmd.SetString(id, Protocol.Prop.Text, "HAL");
            cmd.SetF32(id, Protocol.Prop.FontSize, 64);
            cmd.SetI32(id, Protocol.Prop.FontWeight, 900);
            cmd.SetColor(id, Protocol.Prop.Color, 1, 1, 1, 1);
            cmd.SetI32(id, Protocol.Prop.HorizontalAlignment, 4);
            cmd.SetI32(id, Protocol.Prop.VerticalAlignment, 128);
            cmd.Reparent(id, parent, index);
        }
        uint next = 10;
        // One effect: Input text + map element (child of the effect, referenced by the map uniform).
        void Fx(int cx, int cy, float displacement, bool waveMap, float mr, float mg, float ma)
        {
            var (x, y) = Cell(cx, cy);
            uint fx = next++, text = next++, map = next++;
            cmd.Create(fx, Protocol.Type.ShaderEffect);
            cmd.SetPlace(fx, x, y, CellW, CellH);
            cmd.SetU64(fx, Protocol.Prop.FragmentShader, displace);
            cmd.Reparent(fx, 0, (uint)(cy * 3 + cx));
            Hal(text, fx, 0);
            cmd.SetRef(fx, Protocol.Prop.Source, text);
            if (waveMap)
            {
                cmd.Create(map, Protocol.Type.ShaderEffect);
                cmd.SetU64(map, Protocol.Prop.FragmentShader, wave);
            }
            else
            {
                cmd.Create(map, Protocol.Type.Rectangle);
                cmd.SetColor(map, Protocol.Prop.Color, mr, mg, 0, ma);
            }
            cmd.SetPlace(map, 0, 0, CellW, CellH);
            cmd.Reparent(map, fx, 1);
            cmd.BeginUniforms(fx, Protocol.UniformSet.Clear);
            cmd.UniformVec2("texelSize", 1 / (CellW * dpr), 1 / (CellH * dpr));
            cmd.UniformF32("dpr", dpr);
            cmd.UniformF32("displacement", displacement);
            cmd.UniformRef("map", map);
            cmd.EndUniforms();
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        var (rx, ry) = Cell(0, 0);
        Hal(Ref, 0, 0);
        cmd.SetPlace(Ref, rx, ry, CellW, CellH);
        Fx(1, 0, 0, true, 0, 0, 0);
        Fx(2, 0, 8, true, 0, 0, 0);
        Fx(0, 1, -8, true, 0, 0, 0);
        Fx(1, 1, 20, true, 0, 0, 0);
        Fx(2, 1, 24, false, 1, 0.5f, 1);
        Fx(0, 2, 24, false, 1, 0.5f, 0.5f);
        Fx(1, 2, 24, false, 1, 0.5f, 0);
        Fx(2, 2, 24, false, 0, 0.5f, 1);
        Apply("displace scene");
        s.Pump(8);

        using var a = s.Grab(w, "displace.png");
        var scale = a.Width / 800f;
        bool Bright(Bitmap b, int px, int py) { var c = b.GetPixel(px, py); return c.R > 0x80 && c.G > 0x80 && c.B > 0x80; }
        // bounding box of the white pixels of a cell, in DIP relative to the cell
        (float l, float r, float t, float b, int n) Box(int cx, int cy)
        {
            var (x, y) = Cell(cx, cy);
            int x0 = (int)(x * scale), x1 = (int)((x + CellW) * scale), y0 = (int)(y * scale), y1 = (int)((y + CellH) * scale);
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, n = 0;
            for (int py = y0; py < y1; py++)
                for (int px = x0; px < x1; px++)
                    if (Bright(a, px, py))
                    {
                        n++;
                        minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                    }
            if (n == 0) return (0, 0, 0, 0, 0);
            return (minX / scale - x, (maxX + 1) / scale - x, minY / scale - y, (maxY + 1) / scale - y, n);
        }
        // share of pixels in cell A of bitmap ba that differ clearly from the same pixel in cell B of bitmap bb
        float Diff(Bitmap ba, int ax, int ay, Bitmap bb, int bx, int by)
        {
            var (xa, ya) = Cell(ax, ay);
            var (xb, yb) = Cell(bx, by);
            int diff = 0, total = 0;
            for (int dy = 0; dy < (int)(CellH * scale); dy++)
                for (int dx = 0; dx < (int)(CellW * scale); dx++)
                {
                    var p = ba.GetPixel((int)(xa * scale) + dx, (int)(ya * scale) + dy);
                    var q = bb.GetPixel((int)(xb * scale) + dx, (int)(yb * scale) + dy);
                    total++;
                    if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 96) diff++;
                }
            return (float)diff / total;
        }
        bool Near(float v, float expect, float tol = 1.5f) => Math.Abs(v - expect) <= tol;

        var reference = Box(0, 0);
        gpu(reference.n > 500 && reference.l > 30 && reference.r < CellW - 30 && reference.t > 20 && reference.b < CellH - 20,
            $"reference text sits inside the cell with margin ({reference})");
        var zero = Box(1, 0);
        gpu(Near(zero.l, reference.l) && Near(zero.r, reference.r) && Near(zero.t, reference.t) && Near(zero.b, reference.b),
            $"displacement 0 = unchanged picture ({zero} vs {reference})");
        var dZero = Diff(a, 1, 0, a, 0, 0);
        gpu(dZero < 0.02f, $"displacement 0: pixels match the reference ({dZero:P1} differ)");
        var d24 = Diff(a, 2, 0, a, 1, 0);
        gpu(d24 > 0.08f, $"displacement 8 with the wave map changes the picture ({d24:P1} differ)");
        var dNeg = Diff(a, 0, 1, a, 2, 0);
        gpu(dNeg > 0.08f, $"displacement -8 differs from +8 ({dNeg:P1} differ)");
        var dBig = Diff(a, 1, 1, a, 2, 0);
        gpu(dBig > 0.08f, $"displacement 20 differs from 8 ({dBig:P1} differ)");
        // constant maps: exact shift in DIP
        var left = Box(2, 1);
        gpu(Near(left.l, reference.l - 24) && Near(left.r, reference.r - 24) && Near(left.t, reference.t) && Near(left.b, reference.b),
            $"constant map R = 1, displacement 24: content moves 24 DIP left ({left}, reference {reference})");
        var half = Box(0, 2);
        gpu(Near(half.l, reference.l - 12) && Near(half.r, reference.r - 12) && Near(half.t, reference.t),
            $"map alpha 0.5: half the shift, 12 DIP ({half})");
        var none = Box(1, 2);
        gpu(Near(none.l, reference.l) && Near(none.r, reference.r) && Near(none.t, reference.t) && Near(none.b, reference.b),
            $"map alpha 0: no shift ({none})");
        var right = Box(2, 2);
        gpu(Near(right.l, reference.l + 24) && Near(right.r, reference.r + 24) && Near(right.t, reference.t),
            $"constant map R = 0, displacement 24: content moves 24 DIP right ({right})");

        // live: the displacement uniform of the effect at (2,0) goes 24 -> 0 without a new scene
        cmd.Reset();
        cmd.BeginUniforms(13, 0);
        cmd.UniformF32("displacement", 0);
        cmd.EndUniforms();
        Apply("displacement live to 0");
        s.Pump(4);
        using (var b = s.Grab(w, "displace_live.png"))
        {
            var dLive = Diff(b, 2, 0, b, 0, 0);
            gpu(dLive < 0.02f, $"displacement set to 0 live: back to the reference ({dLive:P1} differ)");
        }

        cmd.Reset();
        for (uint id = 10; id < next; id += 3) cmd.Destroy(id);
        cmd.Destroy(Ref);
        Apply("destroy");
        s.Pump(3);
        Native.vlqt_window_destroy(w);
        Native.vlqt_res_release(displace);
        Native.vlqt_res_release(wave);
        s.Pump(2);
    }
}
