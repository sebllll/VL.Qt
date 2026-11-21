using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using static Pixels;

// ShaderEffect Phase 2: GLSL text baked at runtime in the worker (vlqt_shader_load_source).
// A text shader (wave.frag) draws, the default template of the node bakes and draws, a syntax error ends Failed with a line
// number while the last good version keeps drawing, going back to an earlier text comes from the bake cache.
// Raw commands (DIP). Grabs go to <outDir>/shader-source_*.png.
[TestFixture, NonParallelizable]
public static class ShaderSourceTest
{
    [Test, Category("shader-source")]
    public static void ShaderSource() => Scenario.Run(Run);

    private const uint Solid = 10, Wave = 20, WaveIn = 21, Template = 30, Fresh = 40;

    private static bool Near(Color c, int r, int g, int b, int tol = 14) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    private static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // Number of sampled pixels (step 3) in the region that differ from the other grab by more than 8 in any channel.
    private static int Diff(Bitmap a, Bitmap b, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y += 3)
            for (int x = x0; x < x1; x += 3)
            {
                Color p = Px(a, x, y), q = Px(b, x, y);
                if (Math.Abs(p.R - q.R) > 8 || Math.Abs(p.G - q.G) > 8 || Math.Abs(p.B - q.B) > 8) n++;
            }
        return n;
    }

    private sealed record Res(ushort Ev, ulong Id, byte[] Payload);

    // Generative solid shader (no input, uniform block with the two reserved members only).
    private static string SolidSource(string rgb) =>
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf {\n" +
        "    mat4 qt_Matrix;\n" +
        "    float qt_Opacity;\n" +
        "};\n" +
        "void main()\n" +
        "{\n" +
        "    fragColor = vec4(" + rgb + ", 1.0) * qt_Opacity;\n" +
        "}\n";

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect source needs a QRhi scenegraph");
        var stripes = FindFile("src", "VL.Qt.SmokeTest", "assets", "stripes_rgb_30x10.png");
        var waveFile = FindFile("src", "VL.Qt.Native", "shaders", "wave.frag");
        s.Check(stripes != null && waveFile != null, $"test files found ({stripes}, {waveFile})");
        if (stripes == null || waveFile == null) return;
        var waveText = File.ReadAllText(waveFile);

        var resEvents = new List<Res>();
        var resBuf = new byte[16 * 1024];
        string ResError(ulong id)
        {
            var n = NativeCalls.ResPoll(resBuf);
            for (int at = 0; at < n;)
            {
                var len = (int)BitConverter.ToUInt32(resBuf, at + 4);
                resEvents.Add(new Res(BitConverter.ToUInt16(resBuf, at), BitConverter.ToUInt64(resBuf, at + 8), resBuf.AsSpan(at + 16, len - 8).ToArray()));
                at += (8 + len + 3) & ~3;
            }
            var e = resEvents.FindLast(r => r.Id == id && r.Ev == Protocol.ResourceEventId.Error);
            return e == null ? "" : Encoding.UTF8.GetString(e.Payload);
        }
        string ResWarning(ulong id)
        {
            ResError(id); // polls
            var e = resEvents.FindLast(r => r.Id == id && r.Ev == Protocol.ResourceEventId.Warning);
            return e == null ? "" : Encoding.UTF8.GetString(e.Payload);
        }
        // Loads GLSL text; returns the id, the state after the wait, the time of the call itself and of call + wait.
        ulong Source(string code, out int state, out double callMs, out double totalMs)
        {
            Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var id);
            var sw = Stopwatch.StartNew();
            var rc = Native.vlqt_shader_load_source(id, code, Protocol.ShaderStage.Fragment);
            callMs = sw.Elapsed.TotalMilliseconds;
            state = rc == 0 ? Native.vlqt_res_wait(id, 20000) : Protocol.ResourceState.Failed;
            totalMs = sw.Elapsed.TotalMilliseconds;
            return id;
        }

        Native.vlqt_res_create(Protocol.ResourceKind.Image, out var image);
        Native.vlqt_image_load(image, stripes, 0);
        s.Check(Native.vlqt_res_wait(image, 10000) == Protocol.ResourceState.Ready, "stripes image Ready");

        var green = Source(SolidSource("0.0, 1.0, 0.0"), out var sGreen, out var callGreen, out var bakeGreen);
        var waveShader = Source(waveText, out var sWave, out _, out _);
        var template = Source(ShaderTemplates.Fragment, out var sTemplate, out _, out _);
        s.Check(sGreen == Protocol.ResourceState.Ready && sWave == Protocol.ResourceState.Ready && sTemplate == Protocol.ResourceState.Ready,
            $"GLSL texts (solid, wave.frag, default template) bake -> Ready ({sGreen}, {sWave}, {sTemplate}; {ResError(green)}{ResError(waveShader)}{ResError(template)})");
        s.Check(ResError(template).Length == 0 && ResError(waveShader).Length == 0, "no bake error for the default template and wave.frag");

        // a uniform named like a GLSL builtin is renamed in the OpenGL code, where it is never set: Ready plus a warning
        // (on every backend: another one checks an extra GLSL translation), none for ordinary names.
        var builtinName = Source(SolidSource("length, 0.0, 0.0").Replace("float qt_Opacity;\n", "float qt_Opacity;\n    float length;\n"),
            out var sBuiltinName, out _, out _);
        var builtinWarning = ResWarning(builtinName);
        s.Check(sBuiltinName == Protocol.ResourceState.Ready && builtinWarning.Contains("'length'"),
            $"uniform 'length' -> Ready with a rename warning ({sBuiltinName}, '{builtinWarning}' {ResError(builtinName)})");
        s.Check(ResWarning(template).Length == 0 && ResWarning(waveShader).Length == 0 && ResWarning(green).Length == 0,
            $"no rename warning for ordinary names ({ResWarning(template)}{ResWarning(waveShader)}{ResWarning(green)})");
        s.Check(callGreen < 250, $"the load call returns at once, the bake runs in the worker (call {callGreen:F1} ms of {bakeGreen:F1} ms)");

        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt shader source test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        Native.Result Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        void Effect(uint id, float x, float y, float ew, float eh, ulong frag)
        {
            cmd.Create(id, Protocol.ElementType.Shader);
            cmd.SetPlace(id, x, y, ew, eh);
            cmd.SetU64(id, Protocol.PropertyId.FragmentShader, frag);
            cmd.Reparent(id, 0, 1000);
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // Solid green (text shader) at (50, 50) 300 x 200
        Effect(Solid, 50, 50, 300, 200, green);
        // wave.frag text on the stripes image at (400, 50) 300 x 200, amplitude 0 -> stripes
        Effect(Wave, 400, 50, 300, 200, waveShader);
        cmd.Create(WaveIn, Protocol.ElementType.Image);
        cmd.SetU64(WaveIn, Protocol.PropertyId.Image, image);
        cmd.SetI32(WaveIn, Protocol.PropertyId.FillMode, 0);
        cmd.SetPlace(WaveIn, 0, 0, 300, 200);
        cmd.Reparent(WaveIn, Wave, 0);
        cmd.SetRef(Wave, Protocol.PropertyId.Source, WaveIn);
        cmd.BeginUniforms(Wave, Protocol.UniformSet.Clear);
        cmd.UniformF32("time", 0);
        cmd.UniformF32("amplitude", 0);
        cmd.UniformF32("frequency", 2);
        cmd.EndUniforms();
        // Default template of the node (no uniforms, no input) at (50, 300) 300 x 200
        Effect(Template, 50, 300, 300, 200, template);
        CheckApply("shader source scene");
        s.Pump(8);

        // --- (a)/(b) drawing ---
        using var first = s.Grab(w, "shader-source_text.png");
        gpu(Near(Px(first, 200, 150), 0, 255, 0) && IsBg(Px(first, 40, 150)) && IsBg(Px(first, 360, 150)),
            $"solid GLSL text draws green inside the item ({Px(first, 200, 150)})");
        gpu(Near(Px(first, 450, 150), 255, 0, 0) && Near(Px(first, 550, 150), 0, 255, 0) && Near(Px(first, 650, 150), 0, 0, 255),
            $"wave.frag as text draws the stripes ({Px(first, 450, 150)}, {Px(first, 550, 150)}, {Px(first, 650, 150)})");
        int visible = 0, total = 0;
        for (int y = 320; y < 480; y += 20)
            for (int x = 70; x < 340; x += 20)
            {
                total++;
                if (!IsBg(Px(first, x, y))) visible++;
            }
        gpu(visible > total / 2 && IsBg(Px(first, 40, 400)) && IsBg(Px(first, 360, 400)),
            $"default template draws visibly and stays inside the item ({visible} of {total} samples differ from the background)");

        // --- (c)/(d) syntax error: Failed with a line number, last good version keeps drawing ---
        var broken = Source(SolidSource("foo, 1.0, 0.0"), out var sBroken, out _, out _);
        var error = ResError(broken);
        s.Check(sBroken == Protocol.ResourceState.Failed && error.Length > 0, $"syntax error -> Failed with a message ('{error}')");
        s.Check(Regex.IsMatch(error, @"line\s+\d+") && error.Contains("foo"), $"the message names the line and the identifier ('{error}')");
        cmd.Reset();
        cmd.SetU64(Solid, Protocol.PropertyId.FragmentShader, broken);
        CheckApply("good -> broken text");
        s.Pump(6);
        using (var a = s.Grab(w, "shader-source_error_keeps_last_good.png"))
        {
            gpu(Near(Px(a, 200, 150), 0, 255, 0) && IsBg(Px(a, 40, 150)),
                $"after the failed bake the last good version keeps drawing ({Px(a, 200, 150)})");
            gpu(Diff(first, a, 50, 50, 350, 250) == 0, "pixels of the element unchanged by the failed bake (no black, no transparent)");
        }

        // --- a fix: red text replaces green, then back to the green text (bake cache) ---
        var red = Source(SolidSource("1.0, 0.0, 0.0"), out var sRed, out _, out var bakeRed);
        s.Check(sRed == Protocol.ResourceState.Ready, $"corrected text bakes -> Ready ({sRed}; {ResError(red)})");
        cmd.Reset();
        cmd.SetU64(Solid, Protocol.PropertyId.FragmentShader, red);
        CheckApply("broken -> red text");
        s.Pump(6);
        using (var a = s.Grab(w, "shader-source_fixed_red.png"))
            gpu(Near(Px(a, 200, 150), 255, 0, 0), $"a corrected text replaces the last good version ({Px(a, 200, 150)})");

        var greenAgain = Source(SolidSource("0.0, 1.0, 0.0"), out var sAgain, out _, out var cached);
        s.Check(sAgain == Protocol.ResourceState.Ready, $"the earlier text is Ready again ({sAgain})");
        s.Check(cached < bakeGreen * 0.6 || cached < 30, $"back to an earlier text comes from the cache (first bake {bakeGreen:F1} ms, again {cached:F1} ms, red {bakeRed:F1} ms)");
        cmd.Reset();
        cmd.SetU64(Solid, Protocol.PropertyId.FragmentShader, greenAgain);
        CheckApply("red -> earlier green text");
        s.Pump(6);
        using (var a = s.Grab(w, "shader-source_back_to_green.png"))
            gpu(Near(Px(a, 200, 150), 0, 255, 0), $"the earlier text draws again ({Px(a, 200, 150)})");

        // --- a failed text without an earlier Ready version draws nothing ---
        cmd.Reset();
        Effect(Fresh, 400, 300, 200, 150, broken);
        CheckApply("element with a failed text only");
        s.Pump(6);
        using (var a = s.Grab(w, "shader-source_failed_first.png"))
            gpu(IsBg(Px(a, 500, 375)), $"a failed text without an earlier Ready version draws nothing ({Px(a, 500, 375)})");

        cmd.Reset();
        foreach (var id in new[] { Solid, Wave, Template, Fresh }) cmd.Destroy(id);
        CheckApply("destroy all effects");
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        foreach (var id in new[] { image, green, waveShader, template, broken, red, greenAgain }) Native.vlqt_res_release(id);
        s.Pump(2);
        s.Check(true, "window destroyed, resources released");
    }
}
