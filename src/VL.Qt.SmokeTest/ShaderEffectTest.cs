using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// ShaderEffect (#437, effects package 03, task 5): shipped shaders builtin:wave / builtin:gradient, a fixture shader with a
// second sampler (shaders/dual.frag.qsb: Texture uniform = element, Image uniform = image resource), mesh resolution,
// broken / missing shader paths, unknown uniform name / wrong type -> EV_ERROR and its correction.
// Raw commands (DIP). Grabs go to <outDir>/shader-effect_*.png.
[TestFixture, NonParallelizable]
public static class ShaderEffectTest
{
    [Test, Category("shader-effect")]
    public static void ShaderEffect() => Scenario.Run(Run);

    // Case 1: wave
    const uint Wave = 10, WaveIn = 11;
    // Case 2: gradient (generative), also the target of the uniform errors
    const uint Grad = 20;
    // Case 3: fixture with texture uniform (element) and image uniform
    const uint DualTex = 30, DualTexIn = 31, DualTexOv = 32, DualImg = 33, DualImgIn = 34;
    // Mesh resolution
    const uint Mesh = 40, MeshIn = 41;
    // Case 4: broken / missing shader
    const uint Broken = 50, BrokenIn = 51, NoShader = 52, NoShaderIn = 53;
    const uint HWave = 1, HGrad = 2, HBroken = 3;

    static bool Near(Color c, int r, int g, int b, int tol = 14) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // Number of sampled pixels (step 3, logical coordinates) in the region whose color differs by more than 40 in any channel.
    static int Diff(Bitmap a, Bitmap b, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y += 3)
            for (int x = x0; x < x1; x += 3)
            {
                Color p = Px(a, x, y), q = Px(b, x, y);
                if (Math.Abs(p.R - q.R) > 40 || Math.Abs(p.G - q.G) > 40 || Math.Abs(p.B - q.B) > 40) n++;
            }
        return n;
    }

    sealed record Res(ushort Ev, ulong Id, byte[] Payload);

    static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect needs a QRhi scenegraph");
        var stripes = FindFile("src", "VL.Qt.SmokeTest", "assets", "stripes_rgb_30x10.png");
        var dualQsb = FindFile("src", "VL.Qt.SmokeTest", "shaders", "dual.frag.qsb");
        s.Check(stripes != null && dualQsb != null, $"test files found ({stripes}, {dualQsb})");
        if (stripes == null || dualQsb == null) return;

        // Resources: shaders (async, wait), image, and the status queue for the error texts.
        var resEvents = new List<Res>();
        var resBuf = new byte[16 * 1024];
        void DrainRes()
        {
            var n = Native.ResPoll(resBuf);
            for (int at = 0; at < n;)
            {
                var len = BitConverter.ToUInt16(resBuf, at + 2);
                resEvents.Add(new Res(BitConverter.ToUInt16(resBuf, at), BitConverter.ToUInt64(resBuf, at + 4), resBuf.AsSpan(at + 12, len - 8).ToArray()));
                at += (4 + len + 3) & ~3;
            }
        }
        string ResError(ulong id)
        {
            DrainRes();
            var e = resEvents.FindLast(r => r.Id == id && r.Ev == Protocol.ResEv.Error);
            return e == null ? "" : Encoding.UTF8.GetString(e.Payload);
        }
        ulong Load(string path, int kind, int stage, out int state)
        {
            Native.vlqt_res_create(kind, out var id);
            if (kind == Protocol.ResKind.Shader) Native.vlqt_res_load_shader(id, path, stage);
            else Native.vlqt_res_load_image(id, path, 0);
            state = Native.vlqt_res_wait(id, 10000);
            return id;
        }
        ulong Shader(string path, out int state) => Load(path, Protocol.ResKind.Shader, Protocol.ShaderStage.Fragment, out state);

        var wave = Shader("builtin:wave", out var sWave);
        var gradient = Shader("builtin:gradient", out var sGrad);
        var dual = Shader(dualQsb, out var sDual);
        var image = Load(stripes, Protocol.ResKind.Image, 0, out var sImage);
        s.Check(sWave == Protocol.ResState.Ready && sGrad == Protocol.ResState.Ready && sDual == Protocol.ResState.Ready && sImage == Protocol.ResState.Ready,
            $"shaders builtin:wave/gradient + fixture + image Ready ({sWave}, {sGrad}, {sDual}, {sImage}; {ResError(wave)}{ResError(gradient)}{ResError(dual)})");

        // Broken: missing file, not a .qsb (GLSL text), wrong stage (fragment .qsb loaded as vertex shader).
        var missing = Shader(Path.Combine(s.OutDir, "does-not-exist.frag.qsb"), out var sMissing);
        var glsl = FindFile("src", "VL.Qt.SmokeTest", "shaders", "dual.frag")!;
        var text = Shader(glsl, out var sText);
        var wrongStage = Load(dualQsb, Protocol.ResKind.Shader, Protocol.ShaderStage.Vertex, out var sStage);
        s.Check(sMissing == Protocol.ResState.Failed && ResError(missing).Length > 0, $"missing shader file -> Failed ({ResError(missing)})");
        s.Check(sText == Protocol.ResState.Failed && ResError(text).Length > 0, $"GLSL text instead of .qsb -> Failed ({ResError(text)})");
        s.Check(sStage == Protocol.ResState.Failed && ResError(wrongStage).Length > 0, $"wrong stage -> Failed ({ResError(wrongStage)})");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt shader effect test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var evBuf = new byte[1 << 16];
        var errors = new Dictionary<uint, string>(); // last EV_ERROR text per handle (empty = cleared)
        int Apply() => Native.Apply(w, cmd.EndFrame());
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        string Err(uint handle)
        {
            int n = Native.Poll(w, evBuf);
            for (int at = 0; at < n;)
            {
                ushort ev = BitConverter.ToUInt16(evBuf, at), len = BitConverter.ToUInt16(evBuf, at + 2);
                if (ev == Protocol.Ev.Error)
                    errors[BitConverter.ToUInt32(evBuf, at + 4)] = Encoding.UTF8.GetString(evBuf, at + 12, len - 8);
                at += (4 + len + 3) & ~3;
            }
            return errors.TryGetValue(handle, out var s) ? s : "";
        }

        // Element helpers: the effect, its hidden input child (index 0) and an optional hidden texture child (index 1).
        void Effect(uint id, float x, float y, float ew, float eh, ulong frag)
        {
            cmd.Create(id, Protocol.Type.ShaderEffect);
            cmd.SetPlace(id, x, y, ew, eh);
            cmd.SetU64(id, Protocol.Prop.FragmentShader, frag);
            cmd.Reparent(id, 0, 1000);
        }
        void Input(uint fx, uint id, float ew, float eh, uint idx, bool stripesImage, float r, float g, float b)
        {
            if (stripesImage)
            {
                cmd.Create(id, Protocol.Type.Image);
                cmd.SetU64(id, Protocol.Prop.Image, image);
                cmd.SetI32(id, Protocol.Prop.FillMode, 0); // stretch: red | green | blue stripes
            }
            else
            {
                cmd.Create(id, Protocol.Type.Rectangle);
                cmd.SetColor(id, Protocol.Prop.Color, r, g, b, 1);
            }
            cmd.SetPlace(id, 0, 0, ew, eh);
            cmd.Reparent(id, fx, idx);
            if (idx == 0) cmd.SetRef(fx, Protocol.Prop.Source, id);
        }
        void Waves(float time, float amplitude, float frequency)
        {
            cmd.BeginUniforms(Wave, Protocol.UniformSet.Clear);
            cmd.UniformF32("time", time);
            cmd.UniformF32("amplitude", amplitude);
            cmd.UniformF32("frequency", frequency);
            cmd.EndUniforms();
        }
        // Gradient: premultiplied color (0.5, 0.25, 0, 0.5) from straight (1, 0.5, 0, 0.5); at uv (0.5, 0.5) the pattern is
        // sin((0.5 + 0.5) * 4 + time) = 1 for time = pi/2 - 4, i.e. full brightness there.
        const float gradientTime = MathF.PI / 2 - 4;
        void Gradient()
        {
            cmd.BeginUniforms(Grad, Protocol.UniformSet.Clear);
            cmd.UniformF32("time", gradientTime);
            cmd.UniformColor("color", 1, 0.5f, 0, 0.5f);
            cmd.EndUniforms();
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // 1) wave on the stripes image at (50, 50) 300 x 200: stripes red 50..150 | green 150..250 | blue 250..350
        Effect(Wave, 50, 50, 300, 200, wave);
        Input(Wave, WaveIn, 300, 200, 0, true, 0, 0, 0);
        Waves(0, 0, 2);
        // 2) generative gradient at (400, 50) 300 x 200, no input
        Effect(Grad, 400, 50, 300, 200, gradient);
        Gradient();
        cmd.Bind(Wave, HWave, 0);
        cmd.Bind(Grad, HGrad, 0);
        // 3a) fixture, element as texture: input red, overlay (child 1) blue, amount 0.5 -> purple at (50, 300) 200 x 150
        Effect(DualTex, 50, 300, 200, 150, dual);
        Input(DualTex, DualTexIn, 200, 150, 0, false, 1, 0, 0);
        cmd.Create(DualTexOv, Protocol.Type.Rectangle);
        cmd.SetPlace(DualTexOv, 0, 0, 200, 150);
        cmd.SetColor(DualTexOv, Protocol.Prop.Color, 0, 0, 1, 1);
        cmd.Reparent(DualTexOv, DualTex, 1);
        cmd.BeginUniforms(DualTex, Protocol.UniformSet.Clear);
        cmd.UniformF32("amount", 0.5f);
        cmd.UniformRef("overlay", DualTexOv);
        cmd.EndUniforms();
        // 3b) fixture, image as texture: input yellow, overlay = stripes image, amount 1 -> stripes at (300, 300) 200 x 150
        Effect(DualImg, 300, 300, 200, 150, dual);
        Input(DualImg, DualImgIn, 200, 150, 0, false, 1, 1, 0);
        cmd.BeginUniforms(DualImg, Protocol.UniformSet.Clear);
        cmd.UniformF32("amount", 1);
        cmd.UniformU64("overlay", image);
        cmd.EndUniforms();
        // 3c) mesh resolution 8 x 8 with wave at (550, 300) 200 x 150
        Effect(Mesh, 550, 300, 200, 150, wave);
        Input(Mesh, MeshIn, 200, 150, 0, true, 0, 0, 0);
        cmd.SetIvec2(Mesh, Protocol.Prop.MeshResolution, 8, 8);
        cmd.BeginUniforms(Mesh, Protocol.UniformSet.Clear);
        cmd.UniformF32("time", 0.7f);
        cmd.UniformF32("amplitude", 0.01f);
        cmd.UniformF32("frequency", 3);
        cmd.EndUniforms();
        // 4) failed shader (missing file) and no shader at all: draw nothing, input hidden, at (50, 470) and (250, 470)
        Effect(Broken, 50, 470, 150, 100, missing);
        Input(Broken, BrokenIn, 150, 100, 0, true, 0, 0, 0);
        cmd.Bind(Broken, HBroken, 0);
        Effect(NoShader, 250, 470, 150, 100, 0);
        Input(NoShader, NoShaderIn, 150, 100, 0, true, 0, 0, 0);
        CheckApply("shader effect scene");
        s.Pump(8);

        // --- 1) wave: baseline (amplitude 0) = stripes, then distorted, then another time ---
        using var baseline = s.Grab(w, "shader-effect_wave_baseline.png");
        gpu(Near(Px(baseline, 100, 150), 255, 0, 0) && Near(Px(baseline, 200, 150), 0, 255, 0) && Near(Px(baseline, 300, 150), 0, 0, 255)
            && IsBg(Px(baseline, 40, 150)) && IsBg(Px(baseline, 360, 150)),
            $"wave amplitude 0 shows the input stripes ({Px(baseline, 100, 150)}, {Px(baseline, 200, 150)}, {Px(baseline, 300, 150)})");
        gpu(IsBg(Px(baseline, 100, 100)) == false, "input itself hidden (only the effect output is drawn)");

        cmd.Reset();
        Waves(0, 0.05f, 2);
        CheckApply("wave time 0");
        s.Pump(5);
        using var waveA = s.Grab(w, "shader-effect_wave_a.png");
        cmd.Reset();
        Waves(1.5f, 0.05f, 2);
        CheckApply("wave time 1.5");
        s.Pump(5);
        using var waveB = s.Grab(w, "shader-effect_wave_b.png");
        int dBase = Diff(baseline, waveA, 50, 50, 350, 250), dTime = Diff(waveA, waveB, 50, 50, 350, 250);
        gpu(dBase > 50, $"wave distorts the stripes vs. amplitude 0 ({dBase} differing samples)");
        gpu(dTime > 50, $"animated time changes the distortion between frames ({dTime} differing samples)");
        gpu(IsBg(Px(waveA, 360, 150)) && IsBg(Px(waveA, 40, 150)), "wave stays inside the item");

        // --- 2) gradient: color premultiplied exactly once, in the bright spot (150, 100 local) -> (550, 150) ---
        s.Pump(2);
        using (var a = s.Grab(w, "shader-effect_gradient.png"))
        {
            // premultiplied output (0.5, 0.25, 0, 0.5) over #202020: (0.5 + 0.125, 0.25 + 0.0627 ... ) -> ~ (144, 80, 16)
            var c = Px(a, 550, 150);
            gpu(Near(c, 144, 80, 16, 14), $"gradient color premultiplied once ({c}; straight would be ~255/..., double ~88/..)");
            var dark = Px(a, 405, 55); // uv ~ (0.02, 0.03): sin(0.2 - 2.43) < 0 -> dimmer than the bright spot
            gpu(dark.R < c.R - 15 && !IsBg(dark), $"gradient varies over the item (bright {c}, corner {dark})");
            gpu(IsBg(Px(a, 390, 150)) && IsBg(Px(a, 710, 150)), "gradient stays inside the item bounds");
            gpu(Err(HGrad).Length == 0 && Err(HWave).Length == 0, "no EV_ERROR for valid uniforms");
        }

        // --- 3) fixture with second sampler, mesh resolution ---
        using (var a = s.Grab(w, "shader-effect_samplers.png"))
        {
            var p = Px(a, 150, 375);
            gpu(Near(p, 128, 0, 128, 20), $"Texture uniform (element) mixed 0.5 with the input -> purple ({p})");
            var st = (Px(a, 340, 375), Px(a, 400, 375), Px(a, 460, 375));
            gpu(ImageTest.IsRed(st.Item1) && ImageTest.IsGreen(st.Item2) && ImageTest.IsBlue(st.Item3),
                $"Image uniform (QtImage) shows the stripes instead of the yellow input ({st.Item1}, {st.Item2}, {st.Item3})");
            var m = Px(a, 650, 375);
            gpu(ImageTest.IsGreen(m) || Near(m, 0, 255, 0, 60), $"mesh resolution 8 x 8 with wave renders ({m})");
        }
        // Mesh resolution changes at runtime (and back) must not crash.
        cmd.Reset();
        cmd.SetIvec2(Mesh, Protocol.Prop.MeshResolution, 1, 1);
        CheckApply("mesh resolution 1 x 1");
        s.Pump(3);
        cmd.Reset();
        cmd.SetIvec2(Mesh, Protocol.Prop.MeshResolution, 32, 4);
        CheckApply("mesh resolution 32 x 4");
        s.Pump(3);
        using (var a = s.Grab(w, "shader-effect_mesh.png"))
            gpu(!IsBg(Px(a, 650, 375)), $"mesh resolution switched 1x1 / 32x4 keeps drawing ({Px(a, 650, 375)})");
        cmd.Reset();
        cmd.SetIvec2(Mesh, Protocol.Prop.MeshResolution, 0, 3);
        var rcMesh = Apply();
        s.Check(rcMesh == -7, $"mesh resolution < 1 -> -7 ({rcMesh}: {Native.LastError()})");

        // --- 4) failed shader / no shader: nothing drawn, no crash ---
        using (var a = s.Grab(w, "shader-effect_broken.png"))
        {
            gpu(IsBg(Px(a, 125, 520)) && IsBg(Px(a, 325, 520)) && IsBg(Px(a, 75, 490)),
                $"failed shader without an earlier Ready one and no shader draw nothing, input hidden ({Px(a, 125, 520)}, {Px(a, 325, 520)})");
            s.Check(ResError(missing).Length > 0 && Err(HBroken) is var e0 && e0.Length == 0, $"the shader error comes by the resource, not as EV_ERROR of the element ('{Err(HBroken)}')");
        }
        // Recovery: the element gets a good shader later (and the uniforms stay by name): now it draws.
        cmd.Reset();
        cmd.SetU64(Broken, Protocol.Prop.FragmentShader, wave);
        cmd.BeginUniforms(Broken, Protocol.UniformSet.Clear);
        cmd.UniformF32("time", 0);
        cmd.UniformF32("amplitude", 0);
        cmd.UniformF32("frequency", 2);
        cmd.EndUniforms();
        CheckApply("failed -> good shader");
        s.Pump(5);
        using (var a = s.Grab(w, "shader-effect_recovered.png"))
            gpu(ImageTest.IsRed(Px(a, 70, 520)) && ImageTest.IsGreen(Px(a, 125, 520)) && ImageTest.IsBlue(Px(a, 180, 520)),
                $"a good shader replaces the failed one ({Px(a, 70, 520)}, {Px(a, 125, 520)}, {Px(a, 180, 520)})");
        // And back to a failed one (#483): the last good version keeps drawing (live editing never goes black).
        cmd.Reset();
        cmd.SetU64(Broken, Protocol.Prop.FragmentShader, missing);
        CheckApply("good -> failed shader");
        s.Pump(5);
        using (var a = s.Grab(w, "shader-effect_broken_again.png"))
            gpu(ImageTest.IsRed(Px(a, 70, 520)) && ImageTest.IsGreen(Px(a, 125, 520)) && ImageTest.IsBlue(Px(a, 180, 520)),
                $"a failed new shader keeps the last good version drawing ({Px(a, 70, 520)}, {Px(a, 125, 520)}, {Px(a, 180, 520)})");

        // --- 5) unknown uniform name / wrong type -> EV_ERROR, correction clears it ---
        cmd.Reset();
        cmd.BeginUniforms(Grad, 0);
        cmd.UniformF32("noSuchUniform", 1);
        cmd.EndUniforms();
        CheckApply("unknown uniform name");
        s.Pump(3);
        var unknown = Err(HGrad);
        gpu(unknown.Contains("noSuchUniform"), $"unknown uniform name -> EV_ERROR ('{unknown}')");

        cmd.Reset();
        Gradient(); // full send (Clear): valid again
        CheckApply("correction (full send)");
        s.Pump(3);
        gpu(Err(HGrad).Length == 0, $"correction clears the error ('{Err(HGrad)}')");

        cmd.Reset();
        cmd.BeginUniforms(Grad, 0);
        cmd.UniformVec2("time", 1, 2); // time is a float
        cmd.EndUniforms();
        CheckApply("wrong uniform type");
        s.Pump(3);
        var wrong = Err(HGrad);
        gpu(wrong.Contains("time"), $"wrong type (vec2 for float) -> EV_ERROR ('{wrong}')");
        using (var a = s.Grab(w, "shader-effect_error.png"))
            gpu(!IsBg(Px(a, 550, 150)), $"element keeps drawing with the previous values while it has an error ({Px(a, 550, 150)})");

        cmd.Reset();
        cmd.BeginUniforms(Grad, 0);
        cmd.UniformF32("time", gradientTime); // diff: just the corrected entry
        cmd.EndUniforms();
        CheckApply("correction (diff)");
        s.Pump(3);
        gpu(Err(HGrad).Length == 0, $"corrected entry clears the error ('{Err(HGrad)}')");

        cmd.Reset();
        cmd.BeginUniforms(Grad, 0);
        cmd.UniformF32("qt_Opacity", 0.5f); // reserved
        cmd.UniformColor("color", 1, 0.5f, 0, 0.5f);
        cmd.EndUniforms();
        CheckApply("reserved uniform name");
        s.Pump(3);
        var reserved = Err(HGrad);
        gpu(reserved.Contains("qt_Opacity"), $"reserved name -> EV_ERROR ('{reserved}')");
        cmd.Reset();
        Gradient();
        CheckApply("correction (full send) 2");
        s.Pump(3);
        gpu(Err(HGrad).Length == 0, "second correction clears the error");

        // Not a ShaderEffect: SET_UNIFORMS on a rectangle -> -6; unknown ref -> -4.
        cmd.Reset();
        cmd.BeginUniforms(WaveIn, 0);
        cmd.UniformF32("time", 1);
        cmd.EndUniforms();
        var rc6 = Apply();
        s.Check(rc6 == -6, $"SET_UNIFORMS on an image -> -6 ({rc6}: {Native.LastError()})");
        cmd.Reset();
        cmd.BeginUniforms(DualTex, 0);
        cmd.UniformRef("overlay", 999);
        cmd.EndUniforms();
        var rc4 = Apply();
        s.Check(rc4 == -4, $"texture uniform with an unknown element id -> -4 ({rc4}: {Native.LastError()})");

        // Lifetime: destroy the effects with children, release the resources, window down (no crash, no leak message).
        cmd.Reset();
        foreach (var id in new[] { Wave, Grad, DualTex, DualImg, Mesh, Broken, NoShader }) cmd.Destroy(id);
        CheckApply("destroy all effects");
        s.Pump(3);
        Native.vlqt_window_destroy(w);
        foreach (var id in new[] { wave, gradient, dual, image, missing, text, wrongStage }) Native.vlqt_res_release(id);
        s.Pump(2);
        s.Check(true, "window destroyed, resources released");
    }
}
