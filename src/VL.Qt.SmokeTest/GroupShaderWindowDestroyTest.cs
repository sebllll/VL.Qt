using System;
using VL.Qt;

// #534: vlqt_window_destroy while ShaderEffects with a Group as Input (frame proxy, #486) are still in the tree, without
// destroying the effects first. 10 GLSL effects (texture(source, ...)), each with its own Group of two Rectangles as Input,
// plus one with a Group of 80 Rectangles; a few frames, then the window goes directly. Second round: first the Input of one
// effect released and its Group destroyed in one apply. Both crashed with 0xC0000005 in ~QQuickShaderEffectSource before the fix.
static class GroupShaderWindowDestroyTest
{
    const string Code =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; };\n" +
        "layout(binding = 1) uniform sampler2D source;\n" +
        "void main() { fragColor = texture(source, qt_TexCoord0) * qt_Opacity; }\n";

    public static void Run(Action<bool, string> check, Action<int> pump)
    {
        Native.vlqt_res_create(Protocol.ResKind.Shader, out var shader);
        var rc = Native.vlqt_res_load_shader_source(shader, Code, Protocol.ShaderStage.Fragment);
        var st = rc == 0 ? Native.vlqt_res_wait(shader, 20000) : Protocol.ResState.Failed;
        check(st == Protocol.ResState.Ready, $"534: shader bakes -> Ready ({st}, rc {rc})");

        for (var round = 0; round < 2; round++)
        {
            var w = Native.vlqt_window_create(800, 600, "VL.Qt group shader window destroy test");
            check(w != IntPtr.Zero, "534: window_create");
            Native.vlqt_window_show(w);
            var cmd = new CommandWriter();
            cmd.Reset();
            uint id = 100;
            for (var i = 0; i < 11; i++)
            {
                uint fx = id++, g = id++;
                cmd.Create(fx, Protocol.Type.ShaderEffect);
                cmd.SetPlace(fx, 10 + (i % 5) * 150, 10 + (i / 5) * 150, 100, 100);
                cmd.SetU64(fx, Protocol.Prop.FragmentShader, shader);
                cmd.Reparent(fx, 0, (uint)i);
                cmd.Create(g, Protocol.Type.Item);
                cmd.SetI32(g, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
                var n = i == 10 ? 80 : 2; // the last one with a large Group
                for (var k = 0; k < n; k++)
                {
                    uint r = id++;
                    cmd.Create(r, Protocol.Type.Rectangle);
                    cmd.SetColor(r, Protocol.Prop.Color, k % 2, 0.5f, 1 - k % 2, 1);
                    cmd.SetPlace(r, k * 5 - 20, k % 7 * 5, 40, 40);
                    cmd.Reparent(r, g, (uint)k);
                }
                cmd.Reparent(g, fx, 0);
                cmd.SetRef(fx, Protocol.Prop.Source, g);
            }
            rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"534: scene of 11 effects with a Group as Input ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            pump(5);
            if (round == 1)
            {
                // Runtime path of the same bug: Input released and its Group destroyed in one apply. The Group goes at once,
                // its proxy only at the next apply (frameDelete) and used the deleted Group in its destructor.
                cmd.Reset();
                cmd.SetRef(100, Protocol.Prop.Source, 0);
                cmd.Destroy(101);
                rc = Native.Apply(w, cmd.EndFrame());
                check(rc == 0, $"534: Input released + Group destroyed in one apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
                pump(3);
                cmd.Reset();
                rc = Native.Apply(w, cmd.EndFrame()); // flushes the retired proxy
                pump(2);
                check(rc == 0, "534: next apply after the Group went: no crash");
            }
            Native.vlqt_window_destroy(w); // effects still in the tree: crashed before the fix
            pump(2);
            check(true, $"534: window_destroy with Group-input effects still in the tree, round {round}: no crash");
        }
        Native.vlqt_res_release(shader);
        pump(1);
    }
}
