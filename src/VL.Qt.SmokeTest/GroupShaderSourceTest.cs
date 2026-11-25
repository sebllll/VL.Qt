using System;
using System.Drawing;
using System.IO;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// Group as Shader texture: Input and texture uniform sample the whole Group frame, also its part left of /
// above the origin; the effect's implicit size is the frame and follows it live. Raw commands (DIP).
// Group children: orange 100 x 100 at (-40, -30), blue 100 x 80 at (60, 20), a Text on the blue: frame (-40, -30) .. (160, 100)
// = 200 x 130. In the texture (frame coordinates): orange 0..100 x 0..100, blue 100..200 x 50..130.
// A) Input: builtin:wave with amplitude 0 (shows the Input 1:1) at (50, 50).
// B) texture uniform: shaders/dual.frag.qsb (mix(source, overlay, amount)), amount 1, Input yellow 200 x 130, at (450, 50).
// C) live: orange moves to (-60, -30): frame 220 x 130, effect A widens, blue at 120..220.
// D) Input off + texture Group destroyed, Input on again. E) empty Group (frame 0 x 0) as Input + texture, then a child.
// Grabs: <outDir>/486_*.png.
[TestFixture, NonParallelizable]
public static class GroupShaderSourceTest
{
    [Test, Category("group-shader-source")]
    public static void GroupShaderSource() => Scenario.Run(Run);

    private const uint FxA = 10, GA = 11, OrangeA = 12, BlueA = 13, TextA = 14;
    private const uint FxB = 20, InB = 21, GB = 22, OrangeB = 23, BlueB = 24, TextB = 25;
    private const uint FxE = 30, GE = 31, OrangeE = 32;

    private static bool Near(Color c, int r, int g, int b, int tol = 16) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;
    private static bool IsBg(Color c) => Near(c, 0x20, 0x20, 0x20, 10);
    private static bool IsOrange(Color c) => Near(c, 255, 153, 0);
    private static bool IsBlue(Color c) => Near(c, 51, 102, 255);

    private static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "Shader needs a QRhi scenegraph");
        var dualQsb = FindFile("src", "VL.Qt.SmokeTest", "shaders", "dual.frag.qsb");
        s.Check(dualQsb != null, $"fixture shader found ({dualQsb})");
        if (dualQsb == null) return;
        ulong Shader(string path)
        {
            Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var id);
            Native.vlqt_shader_load(id, path, Protocol.ShaderStage.Fragment);
            s.Check(Native.vlqt_res_wait(id, 10000) == Protocol.ResourceState.Ready, $"shader {path} Ready");
            return id;
        }
        var wave = Shader("builtin:wave");
        var dual = Shader(dualQsb);

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt group shader source test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        void CheckApply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Rect(uint id, float x, float y, float rw, float rh, float r, float g, float b, uint parent, uint index)
        {
            cmd.Create(id, Protocol.ElementType.Rectangle);
            cmd.SetColor(id, Protocol.PropertyId.Color, r, g, b, 1);
            cmd.SetPlace(id, x, y, rw, rh);
            cmd.Reparent(id, parent, index);
        }
        // Group with its children, reparented under the effect at index (hidden there, drawn through the proxy).
        void Group(uint g, uint orange, uint blue, uint text, uint parent, uint index)
        {
            cmd.Create(g, Protocol.ElementType.Group);
            cmd.SetI32(g, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
            Rect(orange, -40, -30, 100, 100, 1, 0.6f, 0, g, 0);
            Rect(blue, 60, 20, 100, 80, 0.2f, 0.4f, 1, g, 1);
            cmd.Create(text, Protocol.ElementType.Text);
            cmd.SetString(text, Protocol.PropertyId.Text, "Hg");
            cmd.SetI32(text, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
            cmd.SetMat4(text, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation(70, 40, 0));
            cmd.Reparent(text, g, 2);
            cmd.Reparent(g, parent, index);
        }

        cmd.Clear();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // A) Group as Input (Auto Size on: the effect takes the frame size; the SIZE 10 x 10 must not count).
        cmd.Create(FxA, Protocol.ElementType.Shader);
        cmd.SetPlace(FxA, 50, 50, 10, 10);
        cmd.SetU64(FxA, Protocol.PropertyId.FragmentShader, wave);
        cmd.Reparent(FxA, 0, 0);
        Group(GA, OrangeA, BlueA, TextA, FxA, 0);
        cmd.SetRef(FxA, Protocol.PropertyId.Source, GA);
        cmd.BeginUniforms(FxA, Protocol.UniformSet.Clear);
        cmd.UniformF32("time", 0);
        cmd.UniformF32("amplitude", 0);
        cmd.UniformF32("frequency", 2);
        cmd.EndUniforms();
        // B) Group as texture uniform over a yellow Input of the frame size.
        cmd.Create(FxB, Protocol.ElementType.Shader);
        cmd.SetPlace(FxB, 450, 50, 200, 130);
        cmd.SetU64(FxB, Protocol.PropertyId.FragmentShader, dual);
        cmd.Reparent(FxB, 0, 1);
        Rect(InB, 0, 0, 200, 130, 1, 1, 0, FxB, 0);
        cmd.SetRef(FxB, Protocol.PropertyId.Source, InB);
        Group(GB, OrangeB, BlueB, TextB, FxB, 1);
        cmd.BeginUniforms(FxB, Protocol.UniformSet.Clear);
        cmd.UniformF32("amount", 1);
        cmd.UniformRef("overlay", GB);
        cmd.EndUniforms();
        CheckApply("group shader source scene");
        s.Pump(8);

        using (var a = s.Grab(w, "486_group_source.png"))
        {
            // A at (50, 50): orange in window 50..150 x 50..150, blue 150..250 x 100..180, frame ends at (250, 180).
            gpu(IsOrange(Px(a, 60, 60)) && IsOrange(Px(a, 130, 60)) && IsOrange(Px(a, 140, 140)),
                $"Input: the orange part left of / above the group origin is in the texture ({Px(a, 60, 60)}, {Px(a, 130, 60)}, {Px(a, 140, 140)})");
            gpu(IsBlue(Px(a, 160, 110)) && IsBlue(Px(a, 240, 170)), $"Input: blue child in place ({Px(a, 160, 110)}, {Px(a, 240, 170)})");
            gpu(IsBg(Px(a, 240, 60)) && IsBg(Px(a, 60, 170)), $"Input: empty frame corners stay transparent ({Px(a, 240, 60)}, {Px(a, 60, 170)})");
            s.Check(IsBg(Px(a, 260, 120)) && IsBg(Px(a, 120, 190)), $"Input: effect size = frame 200 x 130, nothing outside ({Px(a, 260, 120)}, {Px(a, 120, 190)})");
            // B at (450, 50): the same picture through the overlay sampler (amount 1), no yellow.
            gpu(IsOrange(Px(a, 460, 60)) && IsOrange(Px(a, 530, 60)) && IsBlue(Px(a, 640, 170)),
                $"texture uniform: whole frame incl. negative part ({Px(a, 460, 60)}, {Px(a, 530, 60)}, {Px(a, 640, 170)})");
            gpu(IsBg(Px(a, 460, 170)), $"texture uniform: empty frame corner transparent, not the yellow Input ({Px(a, 460, 170)})");
        }

        // C) live: orange moves further left; the frame (220 x 130) and the effect size follow without a new SOURCE.
        cmd.Clear();
        cmd.SetMat4(OrangeA, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation(-60, -30, 0));
        cmd.SetMat4(OrangeB, Protocol.PropertyId.Transform, Stride.Core.Mathematics.Matrix.Translation(-60, -30, 0));
        CheckApply("move orange");
        s.Pump(5);
        using (var a = s.Grab(w, "486_group_source_moved.png"))
        {
            gpu(IsBlue(Px(a, 262, 170)) && IsBg(Px(a, 160, 120)) && IsOrange(Px(a, 140, 140)),
                $"live: the effect widens to the new frame, blue at 170..270 ({Px(a, 262, 170)}, gap {Px(a, 160, 120)}, {Px(a, 140, 140)})");
            s.Check(IsBg(Px(a, 280, 120)), $"live: nothing right of the new frame ({Px(a, 280, 120)})");
            // B keeps its size 200 x 130: the 220 x 130 texture is squeezed into it, blue still in the bottom right corner.
            gpu(IsBlue(Px(a, 640, 170)) && IsOrange(Px(a, 460, 60)), $"live: texture uniform follows ({Px(a, 640, 170)}, {Px(a, 460, 60)})");
        }

        // D) switching away and destroying: Input off, then the Groups go (uniform still pointing at GB); no crash, no error.
        cmd.Clear();
        cmd.SetRef(FxA, Protocol.PropertyId.Source, 0);
        cmd.Destroy(GB);
        CheckApply("source off, texture group destroyed");
        s.Pump(3);
        cmd.Clear();
        cmd.SetRef(FxA, Protocol.PropertyId.Source, GA);
        CheckApply("source again");
        s.Pump(3);
        using (var a = s.Grab(w, "486_group_source_again.png"))
        {
            gpu(IsOrange(Px(a, 60, 60)), $"Input again after a release: proxy recreated ({Px(a, 60, 60)})");
            gpu(IsBg(Px(a, 640, 170)), $"destroyed texture group samples transparent ({Px(a, 640, 170)})");
        }

        // E) empty Group (frame 0 x 0) as Input and as texture uniform: draws nothing, no crash; a child added later shows up.
        cmd.Clear();
        cmd.Create(FxE, Protocol.ElementType.Shader);
        cmd.SetPlace(FxE, 50, 350, 10, 10);
        cmd.SetU64(FxE, Protocol.PropertyId.FragmentShader, dual);
        cmd.Reparent(FxE, 0, 2);
        cmd.Create(GE, Protocol.ElementType.Group);
        cmd.SetI32(GE, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft);
        cmd.Reparent(GE, FxE, 0);
        cmd.SetRef(FxE, Protocol.PropertyId.Source, GE);
        cmd.BeginUniforms(FxE, Protocol.UniformSet.Clear);
        cmd.UniformF32("amount", 0);
        cmd.UniformRef("overlay", GE);
        cmd.EndUniforms();
        CheckApply("empty group as input and texture");
        s.Pump(3);
        using (var a = s.Grab(w, "486_group_source_empty.png"))
            s.Check(IsBg(Px(a, 55, 355)), $"empty group: nothing drawn ({Px(a, 55, 355)})");
        cmd.Clear();
        Rect(OrangeE, -20, -20, 60, 60, 1, 0.6f, 0, GE, 0);
        CheckApply("child into the empty group");
        s.Pump(3);
        using (var a = s.Grab(w, "486_group_source_filled.png"))
            gpu(IsOrange(Px(a, 55, 355)) && IsOrange(Px(a, 105, 405)) && IsBg(Px(a, 115, 415)),
                $"empty group gets a child at (-20, -20): effect 60 x 60 shows it ({Px(a, 55, 355)}, {Px(a, 105, 405)}, {Px(a, 115, 415)})");

        cmd.Clear();
        cmd.Destroy(FxA);
        cmd.Destroy(FxB);
        cmd.Destroy(FxE);
        CheckApply("destroy");
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        Native.vlqt_res_release(wave);
        Native.vlqt_res_release(dual);
        s.Pump(2);
    }
}
