using System;
using System.Drawing;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// NFX-02: two samplers of one ShaderEffect read the same element, the lower binding with a mip filter and the higher one
// with a state that has none (Nearest filter, no mips). The layer request of the element must be the OR of both: refreshLayerRequests
// asks once per sampler and requestLayer overwrites the one entry of the owner, so the last sampler wins and the mip filter is lost
// after the second refresh. Read from a minified read (stripes, uv * 14): with mips every pixel is the purple mean, without they alias.
// A control cell with no state for the higher binding (no conflict) must show the mean at any time.
[TestFixture, NonParallelizable]
public static class SamplerMipSharedTest
{
    /// <summary>NFX-02: one element read by two samplers, the mip filter only on the lower binding.</summary>
    [Test, Category("sampler-mip-shared")]
    [Ignore("NFX-02: refreshLayerRequests lets the last sampler overwrite the owner's mip request, the mip filter of the other sampler on the same element is lost (fails: mip on the higher binding b, plain a)")]
    public static void SamplerMipShared() => Scenario.Run(Run);

    const float CellW = 150, CellH = 120;

    // Two sampler2D, bindings 1 and 2: `which` picks the one that is read (whichever order QtNative walks the samplers in).
    const string Shader =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; vec2 c; float k; float which; };\n" +
        "layout(binding = 1) uniform sampler2D a;\n" +
        "layout(binding = 2) uniform sampler2D b;\n" +
        "void main() {\n" +
        "    vec4 col = texture(a, c + (qt_TexCoord0 - 0.5) * k);\n" +
        "    col.a += texture(b, vec2(0.5)).a * 0.000001;\n" +
        "    fragColor = col * qt_Opacity;\n" +
        "}\n";

    // protocol.h "Sampler": filter (0 Linear, 1 Nearest), mip (0 None, 1 Nearest, 2 Linear), address U, V (0 Clamp, 1 Repeat, 2 Mirror)
    static ulong S(int filter, int mip, int au, int av) =>
        (ulong)(uint)filter | (ulong)(uint)mip << 8 | (ulong)(uint)au << 16 | (ulong)(uint)av << 24;

    static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect needs a QRhi scenegraph");
        Native.vlqt_res_create(Protocol.ResKind.Shader, out var shader);
        var rc = Native.vlqt_res_load_shader_source(shader, Shader, Protocol.ShaderStage.Fragment);
        var st = rc == 0 ? Native.vlqt_res_wait(shader, 20000) : Protocol.ResState.Failed;
        s.Check(st == Protocol.ResState.Ready, $"two sampler shader bakes -> Ready ({st}, rc {rc})");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt sampler mip shared test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        void Apply(string what)
        {
            var r = Native.Apply(w, cmd.EndFrame());
            s.Check(r == 0, $"{what} ({r}) {(r != 0 ? Native.LastError() : "")}");
        }

        // Cell 0: mip on a (binding 1), b Nearest without mips, a is read. Cell 1: the other way round, b has the mip filter and is
        // read (one of the two loses its mip request today). Cell 2: no mip anywhere (the aliasing reference). Cell 3: control,
        // mip on a, no sampler state for b (nothing to overwrite).
        uint next = 10;
        (uint fx, uint group)[] cells = new (uint, uint)[5];
        ulong[][] states = { new[] { S(0, 2, 1, 1), S(1, 0, 1, 1) }, new[] { S(1, 0, 1, 1), S(0, 2, 1, 1) }, new[] { S(1, 0, 1, 1), S(1, 0, 1, 1) }, new[] { S(0, 2, 1, 1), 0UL }, new[] { 0UL, S(0, 2, 1, 1) } };
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        for (int i = 0; i < 5; i++)
        {
            uint fx = next++, g = next++;
            cells[i] = (fx, g);
            cmd.Create(fx, Protocol.Type.ShaderEffect);
            cmd.SetPlace(fx, 15 + i * 155, 20, CellW, CellH);
            cmd.SetU64(fx, Protocol.Prop.FragmentShader, shader);
            cmd.Reparent(fx, 0, (uint)i);
            // 80 stripes of 2 DIP, red / blue
            cmd.Create(g, Protocol.Type.Item);
            cmd.SetI32(g, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
            for (uint k = 0; k < 80; k++)
            {
                uint r = next++;
                cmd.Create(r, Protocol.Type.Rectangle);
                cmd.SetColor(r, Protocol.Prop.Color, k % 2 == 0 ? 1 : 0, 0, k % 2 == 0 ? 0 : 1, 1);
                cmd.SetPlace(r, k * 2, 0, 2, CellH);
                cmd.Reparent(r, g, k);
            }
            cmd.Reparent(g, fx, 0);
            Uniforms(fx, g, Protocol.UniformSet.Clear, 14f, i == 1 || i == 4 ? 1f : 0f, states[i]);
        }
        void Uniforms(uint fx, uint group, ushort flags, float k, float which, ulong[] state)
        {
            cmd.BeginUniforms(fx, flags);
            cmd.UniformVec2("c", 0.5f, 0.5f);
            cmd.UniformF32("k", k);
            cmd.UniformF32("which", which);
            cmd.UniformRef("a", group);
            cmd.UniformRef("b", group);
            if (state[0] != 0)
                cmd.UniformSampler("a", state[0]);
            if (state[1] != 0)
                cmd.UniformSampler("b", state[1]);
            cmd.EndUniforms();
        }
        Apply("scene");
        s.Pump(10);

        // Off-mean count of one cell in a grab: pixels of the stripes' purple mean are the mipmapped read.
        int Off(Bitmap a, int cell)
        {
            int n = 0, cy = 20 + (int)(CellH / 2);
            for (int x = 20; x < 140; x += 3)
                if (!Near(Px(a, 15 + cell * 155 + x, cy), 128, 0, 128, 40)) n++;
            return n;
        }

        // A second refresh: the sampler state of the cells 0 and 1 is sent again (a diff entry, nothing changes).
        // The defect shows only from the second refresh on, when the element is already in m_layerItems.
        for (int frame = 0; frame < 2; frame++)
        {
            using (var a = s.Grab(w, $"sampler-mip-shared_{frame}.png"))
            {
                int off0 = Off(a, 0), off1 = Off(a, 1), off2 = Off(a, 2), off3 = Off(a, 3), off4 = Off(a, 4);
                gpu(off2 > 8, $"frame {frame}: no mips at all: the stripes alias ({off2} of 40 samples off the mean)");
                gpu(off3 == 0, $"frame {frame}: control, mip filter on a, no state for b: the mean ({off3} off)");
                gpu(off4 == 0, $"frame {frame}: control, mip filter on b, no state for a: the mean ({off4} off)");
                gpu(off0 == 0, $"frame {frame}: mip filter on the lower binding, none on the higher one: the mean ({off0} off)");
                gpu(off1 == 0, $"frame {frame}: mip filter on the higher binding, none on the lower one: the mean ({off1} off)");
            }
            cmd.Reset();
            // Sending a sampler state marks the layer requests dirty, whatever its value.
            for (int i = 0; i < 2; i++)
            {
                cmd.BeginUniforms(cells[i].fx, 0);
                cmd.UniformSampler(i == 0 ? "a" : "b", states[i][i]);
                cmd.EndUniforms();
            }
            Apply($"second refresh {frame}");
            s.Pump(6);
        }

        cmd.Reset();
        foreach (var (fx, _) in cells)
            cmd.Destroy(fx);
        Apply("destroy");
        s.Pump(4);
        Native.vlqt_window_destroy(w);
        Native.vlqt_res_release(shader);
    }
}
