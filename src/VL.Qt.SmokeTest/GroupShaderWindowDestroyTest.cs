using System;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// vlqt_scene_destroy while ShaderEffects with a Group as Input (frame proxy) are still in the tree, without
// destroying the effects first. 10 GLSL effects (texture(source, ...)), each with its own Group of two Rectangles as Input,
// plus one with a Group of 80 Rectangles; a few frames, then the window goes directly. Second round: first the Input of one
// effect released and its Group destroyed in one apply. Both crashed with 0xC0000005 in ~QQuickShaderEffectSource before the fix.
[TestFixture, NonParallelizable]
public static class GroupShaderWindowDestroyTest
{
    [Test, Category("group-shader-window-destroy")]
    public static void GroupShaderWindowDestroy() => Scenario.Run(Run);

    private const string Code =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; };\n" +
        "layout(binding = 1) uniform sampler2D source;\n" +
        "void main() { fragColor = texture(source, qt_TexCoord0) * qt_Opacity; }\n";

    private static void Run(Scenario s)
    {
        Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var shader);
        var rc = Native.vlqt_shader_load_source(shader, Code, Protocol.ShaderStage.Fragment);
        var st = rc == 0 ? Native.vlqt_res_wait(shader, 20000) : Protocol.ResourceState.Failed;
        s.Check(st == Protocol.ResourceState.Ready, $"shader bakes -> Ready ({st}, rc {rc})");

        for (var round = 0; round < 2; round++)
        {
            var w = NativeCalls.CreateWindow(800, 600, "VL.Qt group shader window destroy test");
            s.Check(w != SceneHandle.Null, "window_create");
            Native.vlqt_window_show(w);
            var cmd = new CommandWriter();
            cmd.Reset();
            uint id = 100;
            for (var i = 0; i < 11; i++)
            {
                uint fx = id++, g = id++;
                cmd.Create(fx, Protocol.ElementType.Shader);
                cmd.SetPlace(fx, 10 + (i % 5) * 150, 10 + (i / 5) * 150, 100, 100);
                cmd.SetU64(fx, Protocol.PropertyId.FragmentShader, shader);
                cmd.Reparent(fx, 0, (uint)i);
                cmd.Create(g, Protocol.ElementType.Group);
                cmd.SetI32(g, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
                var n = i == 10 ? 80 : 2; // the last one with a large Group
                for (var k = 0; k < n; k++)
                {
                    uint r = id++;
                    cmd.Create(r, Protocol.ElementType.Rectangle);
                    cmd.SetColor(r, Protocol.PropertyId.Color, k % 2, 0.5f, 1 - k % 2, 1);
                    cmd.SetPlace(r, k * 5 - 20, k % 7 * 5, 40, 40);
                    cmd.Reparent(r, g, (uint)k);
                }
                cmd.Reparent(g, fx, 0);
                cmd.SetRef(fx, Protocol.PropertyId.Source, g);
            }
            rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"scene of 11 effects with a Group as Input ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            s.Pump(5);
            if (round == 1)
            {
                // Runtime path of the same bug: Input released and its Group destroyed in one apply. The Group goes at once,
                // its proxy only at the next apply (frameDelete) and used the deleted Group in its destructor.
                cmd.Reset();
                cmd.SetRef(100, Protocol.PropertyId.Source, 0);
                cmd.Destroy(101);
                rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"Input released + Group destroyed in one apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
                s.Pump(3);
                cmd.Reset();
                rc = Native.Apply(w, cmd.EndFrame()); // flushes the retired proxy
                s.Pump(2);
                s.Check(rc == 0, "next apply after the Group went: no crash");
            }
            Native.vlqt_scene_destroy(w); // effects still in the tree: crashed before the fix
            s.Pump(2);
            s.Check(true, $"window_destroy with Group-input effects still in the tree, round {round}: no crash");
        }
        Native.vlqt_res_release(shader);
        s.Pump(1);
    }
}
