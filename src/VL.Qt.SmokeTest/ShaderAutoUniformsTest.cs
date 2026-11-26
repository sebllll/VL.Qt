using System;
using System.Drawing;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// Automatic uniforms of a ShaderEffect element (not of an effect chain): texelSize (1 / pixel size of source), dpr and
// viewSize (size in DIP), written when the fragment shader declares them. The shader shows them as colors:
//   red   = viewSize.x / 400                       (200 DIP -> 0.5, 120 DIP -> 0.3)
//   green = viewSize.y / 400                       (100 DIP -> 0.25, 60 DIP -> 0.15)
//   blue  = texelSize.x * dpr * viewSize.x         (1 at every device pixel ratio and size: DIP * dpr * texelSize is a UV length)
// Two effects of different size over a Rectangle as Input. Grab: <outDir>/120_auto_uniforms.png.
[TestFixture, NonParallelizable]
public static class ShaderAutoUniformsTest
{
    [Test, Category("shader-auto-uniforms")]
    public static void ShaderAutoUniforms() => Scenario.Run(Run);

    private const string Code =
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; vec2 texelSize; float dpr; vec2 viewSize; };\n" +
        "layout(binding = 1) uniform sampler2D source;\n" +
        "void main() { fragColor = vec4(viewSize.x / 400.0, viewSize.y / 400.0, texelSize.x * dpr * viewSize.x, 1.0) * qt_Opacity; }\n";

    private static bool Near(Color c, int r, int g, int b, int tol = 10) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    private static void Run(Scenario s)
    {
        Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var shader);
        var rc = Native.vlqt_shader_load_source(shader, Code, Protocol.ShaderStage.Fragment);
        var st = rc == 0 ? Native.vlqt_res_wait(shader, 20000) : Protocol.ResourceState.Failed;
        s.Check(st == Protocol.ResourceState.Ready, $"shader bakes -> Ready ({st}, rc {rc})");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt shader auto uniforms test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        cmd.Clear();
        cmd.ClearColor(0, 0, 0, 1);
        void Effect(uint fx, uint input, float x, float y, float width, float height, uint index)
        {
            cmd.Create(fx, Protocol.ElementType.Shader);
            cmd.SetPlace(fx, x, y, width, height);
            cmd.SetU64(fx, Protocol.PropertyId.FragmentShader, shader);
            cmd.Reparent(fx, 0, index);
            cmd.Create(input, Protocol.ElementType.Rectangle);
            cmd.SetColor(input, Protocol.PropertyId.Color, 1, 1, 1, 1);
            cmd.SetPlace(input, 0, 0, width, height);
            cmd.Reparent(input, fx, 0);
            cmd.SetRef(fx, Protocol.PropertyId.Source, input);
        }
        Effect(10, 11, 50, 50, 200, 100, 0);
        Effect(20, 21, 400, 50, 120, 60, 1);
        rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, $"scene ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(8);

        using (var a = s.Grab(w, "120_auto_uniforms.png"))
        {
            var big = Px(a, 150, 100);
            var small = Px(a, 460, 80);
            s.NeedsRhi(Near(big, 128, 64, 255), $"effect 200 x 100 DIP: viewSize, and DIP * dpr * texelSize = 1 ({big})", "Shader needs a QRhi scenegraph");
            s.NeedsRhi(Near(small, 77, 38, 255), $"effect 120 x 60 DIP: viewSize of its own size ({small})", "Shader needs a QRhi scenegraph");
        }

        cmd.Clear();
        cmd.Destroy(10);
        cmd.Destroy(20);
        rc = Native.Apply(w, cmd.EndFrame());
        s.Check(rc == 0, "destroy");
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        Native.vlqt_res_release(shader);
        s.Pump(2);
    }
}
