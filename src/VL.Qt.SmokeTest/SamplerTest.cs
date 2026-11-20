using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using NUnit.Framework;
using VL.Qt;
using static Pixels;

// Sampler state of a sampler2D (#492, SAMPLER entry of SET_UNIFORMS): filter, address, mip filter, read back from pixels.
// One GLSL shader per sampler name (source = Input, overlay = texture uniform) that samples c + (uv - 0.5) * k.
// 4 x 3 grid of 160 x 120 DIP cells on 0x20 grey:
//   row 0  (Input = Group of a red | blue pair, zoomed in at their edge, k = 0.04): Point, Linear, no sampler (a layer reads Nearest),
//          and a plain Rectangle with mip filter Linear (the layer of a plain element gets mipmaps, the picture stays).
//   row 1  (texture uniform = Image element red | green | blue, uv * 2): Clamp, Wrap, Mirror, no sampler (= Clamp).
//   row 2  (Input = Group of 80 two DIP wide red / blue stripes, uv * 14 = minified, Wrap): without mips the pixels alias,
//          with MipFilter Linear every pixel is the purple mean (the layer of the Group proxy is rendered with mipmaps).
// Live: Point -> Linear by a diff entry, back by CLEAR, a sampler state for a name the shader does not have (EV_ERROR),
// an out of range state (apply error -7). Raw commands (DIP). Grabs: <outDir>/sampler_*.png.
[TestFixture, NonParallelizable]
public static class SamplerTest
{
    [Test, Category("sampler")]
    public static void Sampler() => Scenario.Run(Run);

    private const float CellW = 160, CellH = 120;

    private static string Shader(string sampler) =>
        "#version 440\n" +
        "layout(location = 0) in vec2 qt_TexCoord0;\n" +
        "layout(location = 0) out vec4 fragColor;\n" +
        "layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; vec2 c; float k; };\n" +
        $"layout(binding = 1) uniform sampler2D {sampler};\n" +
        $"void main() {{ fragColor = texture({sampler}, c + (qt_TexCoord0 - 0.5) * k) * qt_Opacity; }}\n";

    // protocol.h "Sampler": filter (0 Linear, 1 Nearest), mip (0 None, 1 Nearest, 2 Linear), address U, V (0 Clamp, 1 Repeat, 2 Mirror)
    private static ulong S(int filter, int mip, int au, int av) =>
        (ulong)(uint)filter | (ulong)(uint)mip << 8 | (ulong)(uint)au << 16 | (ulong)(uint)av << 24;

    private static bool Near(Color c, int r, int g, int b, int tol = 16) => Math.Abs(c.R - r) < tol && Math.Abs(c.G - g) < tol && Math.Abs(c.B - b) < tol;

    private static (float x, float y) Cell(int cx, int cy) => (15 + cx * 200, 20 + cy * 140);

    private static void Run(Scenario s)
    {
        Action<bool, string> gpu = (ok, what) => s.NeedsRhi(ok, what, "ShaderEffect needs a QRhi scenegraph");
        Directory.CreateDirectory(s.OutDir);
        var stripesPath = FindFile("src", "VL.Qt.SmokeTest", "assets", "stripes_rgb_30x10.png");
        s.Check(stripesPath != null, "stripes image found");
        if (stripesPath == null) return;

        ulong LoadShader(string name)
        {
            Native.vlqt_res_create(Protocol.ResKind.Shader, out var id);
            var rc = Native.vlqt_res_load_shader_source(id, Shader(name), Protocol.ShaderStage.Fragment);
            var st = rc == 0 ? Native.vlqt_res_wait(id, 20000) : Protocol.ResState.Failed;
            s.Check(st == Protocol.ResState.Ready, $"shader sampling '{name}' bakes -> Ready ({st}, rc {rc})");
            return id;
        }
        var onSource = LoadShader("source");
        var onOverlay = LoadShader("overlay");
        Native.vlqt_res_create(Protocol.ResKind.Image, out var stripes);
        Native.vlqt_res_load_image(stripes, stripesPath, 0);
        s.Check(Native.vlqt_res_wait(stripes, 10000) == Protocol.ResState.Ready, "stripes image Ready");

        var w = Native.vlqt_window_create(800, 600, "VL.Qt sampler test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var evBuf = new byte[1 << 16];
        var errors = new Dictionary<uint, string>();
        int ApplyRc() => Native.Apply(w, cmd.EndFrame());
        void Apply(string what)
        {
            var rc = ApplyRc();
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

        var effects = new List<uint>();
        void Teardown()
        {
            cmd.Reset();
            foreach (var fx in effects)
                cmd.Destroy(fx);
            ApplyRc();
            s.Pump(4);
            Native.vlqt_window_destroy(w);
            foreach (var id in new[] { onSource, onOverlay, stripes }) Native.vlqt_res_release(id);
        }
        uint next = 10, rootIndex = 0;
        void Rect(uint id, float x, float y, float rw, float rh, float r, float g, float b, uint parent, uint index)
        {
            cmd.Create(id, Protocol.Type.Rectangle);
            cmd.SetColor(id, Protocol.Prop.Color, r, g, b, 1);
            cmd.SetPlace(id, x, y, rw, rh);
            cmd.Reparent(id, parent, index);
        }
        uint NewEffect(int cx, int cy, ulong shader, float cxUv, float cyUv, float k)
        {
            var (x, y) = Cell(cx, cy);
            uint fx = next++;
            effects.Add(fx);
            cmd.Create(fx, Protocol.Type.ShaderEffect);
            cmd.SetPlace(fx, x, y, CellW, CellH);
            cmd.SetU64(fx, Protocol.Prop.FragmentShader, shader);
            cmd.Reparent(fx, 0, rootIndex++);
            return fx;
        }
        void Uniforms(uint fx, ushort flags, float cxUv, float cyUv, float k, string? sampler, ulong state, string? textureName = null, uint texture = 0)
        {
            cmd.BeginUniforms(fx, flags);
            cmd.UniformVec2("c", cxUv, cyUv);
            cmd.UniformF32("k", k);
            if (textureName != null)
                cmd.UniformRef(textureName, texture);
            if (sampler != null)
                cmd.UniformSampler(sampler, state);
            cmd.EndUniforms();
        }
        // Group (hidden Input) of a red | blue pair 80 + 80 wide (whole pixels at every common device pixel ratio).
        uint RedBlueGroup(uint fx)
        {
            uint g = next++;
            cmd.Create(g, Protocol.Type.Item);
            cmd.SetI32(g, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
            Rect(next++, 0, 0, 80, CellH, 1, 0, 0, g, 0);
            Rect(next++, 80, 0, 80, CellH, 0, 0, 1, g, 1);
            cmd.Reparent(g, fx, 0);
            cmd.SetRef(fx, Protocol.Prop.Source, g);
            return g;
        }
        uint StripeGroup(uint fx)
        {
            uint g = next++;
            cmd.Create(g, Protocol.Type.Item);
            cmd.SetI32(g, Protocol.Prop.Anchor, GeometryExtensions.AlignTopLeft);
            for (uint i = 0; i < 80; i++)
            {
                Rect(next++, i * 2, 0, 2, CellH, i % 2 == 0 ? 1 : 0, 0, i % 2 == 0 ? 0 : 1, g, i);
            }
            cmd.Reparent(g, fx, 0);
            cmd.SetRef(fx, Protocol.Prop.Source, g);
            return g;
        }
        uint StripesImage(uint fx)
        {
            uint img = next++;
            cmd.Create(img, Protocol.Type.Image);
            cmd.SetU64(img, Protocol.Prop.Image, stripes);
            cmd.SetI32(img, Protocol.Prop.FillMode, 0);
            cmd.SetPlace(img, 0, 0, CellW, CellH);
            cmd.Reparent(img, fx, 0);
            return img;
        }

        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        // row 0: zoom at the red | blue edge
        uint[] edge = new uint[3];
        for (int i = 0; i < 3; i++)
        {
            edge[i] = NewEffect(i, 0, onSource, 0.5f, 0.5f, 0.04f);
            RedBlueGroup(edge[i]);
        }
        Uniforms(edge[0], Protocol.UniformSet.Clear, 0.5f, 0.5f, 0.04f, "source", S(1, 0, 0, 0)); // Point
        Uniforms(edge[1], Protocol.UniformSet.Clear, 0.5f, 0.5f, 0.04f, "source", S(0, 0, 0, 0)); // Linear
        Uniforms(edge[2], Protocol.UniformSet.Clear, 0.5f, 0.5f, 0.04f, null, 0);                 // none
        // plain element: mipmaps on its own layer
        uint plain = NewEffect(3, 0, onSource, 0.5f, 0.5f, 1f);
        uint plainIn = next++;
        Rect(plainIn, 0, 0, CellW, CellH, 1, 0, 0, plain, 0);
        cmd.SetRef(plain, Protocol.Prop.Source, plainIn);
        Uniforms(plain, Protocol.UniformSet.Clear, 0.5f, 0.5f, 1f, "source", S(0, 2, 0, 0));
        // row 1: wrap of an element texture (uv * 2: 0..2 across the cell)
        uint[] wrap = new uint[4];
        ulong[] wrapState = { S(0, 0, 0, 0), S(0, 0, 1, 1), S(0, 0, 2, 2), 0 };
        for (int i = 0; i < 4; i++)
        {
            wrap[i] = NewEffect(i, 1, onOverlay, 1f, 0.5f, 2f);
            uint img = StripesImage(wrap[i]);
            Uniforms(wrap[i], Protocol.UniformSet.Clear, 1f, 0.5f, 2f, wrapState[i] == 0 ? null : "overlay", wrapState[i], "overlay", img);
        }
        // row 2: minified stripes, Wrap; without / with mipmaps
        uint[] mip = new uint[2];
        for (int i = 0; i < 2; i++)
        {
            mip[i] = NewEffect(i, 2, onSource, 0.5f, 0.5f, 14f);
            StripeGroup(mip[i]);
            Uniforms(mip[i], Protocol.UniformSet.Clear, 0.5f, 0.5f, 14f, "source", i == 0 ? S(0, 0, 1, 1) : S(0, 2, 1, 1));
        }
        cmd.Bind(edge[0], 1, 0);
        Apply("sampler scene");
        s.Pump(10);

        // --- row 0: filter ---
        using (var a = s.Grab(w, "sampler_filter.png"))
        {
            var (x0, y0) = Cell(0, 0);
            int cy = (int)(y0 + CellH / 2);
            Color Left(int cell) => Px(a, (int)Cell(cell, 0).x + 80 - 3, cy);
            Color Right(int cell) => Px(a, (int)Cell(cell, 0).x + 80 + 3, cy);
            gpu(Near(Left(0), 255, 0, 0) && Near(Right(0), 0, 0, 255), $"Point: hard red | blue edge ({Left(0)}, {Right(0)})");
            gpu(Left(1).B > 25 && Left(1).R < 240 && Right(1).R > 25 && Right(1).B < 240, $"Linear: the edge is blended ({Left(1)}, {Right(1)})");
            gpu(Near(Left(2), 255, 0, 0) && Near(Right(2), 0, 0, 255), $"no sampler: a layer is read Nearest, as before ({Left(2)}, {Right(2)})");
            var f = Px(a, (int)Cell(3, 0).x + 80, cy);
            gpu(Near(f, 255, 0, 0), $"plain element with mip filter shows the red rectangle ({f})");
        }
        s.Check(Err(1).Length == 0, $"valid sampler states: no error ({Err(1)})");

        // --- row 1: address ---
        using (var a = s.Grab(w, "sampler_wrap.png"))
        {
            int cy = (int)(Cell(0, 1).y + CellH / 2);
            Color At(int cell, float t) => Px(a, (int)(Cell(cell, 1).x + t * CellW), cy);
            gpu(Near(At(0, 0.1f), 255, 0, 0) && Near(At(0, 0.6f), 0, 0, 255) && Near(At(0, 0.9f), 0, 0, 255),
                $"Clamp: the edge texel continues ({At(0, 0.1f)}, {At(0, 0.6f)}, {At(0, 0.9f)})");
            gpu(Near(At(1, 0.6f), 255, 0, 0) && Near(At(1, 0.9f), 0, 0, 255), $"Wrap: the texture tiles ({At(1, 0.6f)}, {At(1, 0.9f)})");
            gpu(Near(At(2, 0.6f), 0, 0, 255) && Near(At(2, 0.9f), 255, 0, 0), $"Mirror: every second tile is mirrored ({At(2, 0.6f)}, {At(2, 0.9f)})");
            gpu(Near(At(3, 0.6f), 0, 0, 255) && Near(At(3, 0.9f), 0, 0, 255), $"no sampler: Clamp as before ({At(3, 0.6f)}, {At(3, 0.9f)})");
        }

        // --- row 2: mipmaps ---
        using (var a = s.Grab(w, "sampler_mip.png"))
        {
            int cy = (int)(Cell(0, 2).y + CellH / 2);
            int Off(int cell)
            {
                int n = 0;
                for (int x = 20; x < 150; x += 3)
                    if (!Near(Px(a, (int)Cell(cell, 2).x + x, cy), 128, 0, 128, 40)) n++;
                return n;
            }
            int off0 = Off(0), off1 = Off(1);
            gpu(off0 > 8, $"no mipmaps: the stripes alias ({off0} of 44 samples off the mean)");
            gpu(off1 == 0, $"mip filter Linear: every pixel is the mean of the stripes ({off1} of 44 samples off)");
        }

        // --- live: Point -> Linear (diff entry), back by CLEAR; a name the shader does not have; out of range ---
        cmd.Reset();
        cmd.BeginUniforms(edge[0], 0);
        cmd.UniformSampler("source", S(0, 0, 0, 0));
        cmd.EndUniforms();
        Apply("sampler Point -> Linear");
        s.Pump(6);
        using (var a = s.Grab(w, "sampler_live_linear.png"))
        {
            int cy = (int)(Cell(0, 0).y + CellH / 2);
            var c = Px(a, (int)Cell(0, 0).x + 80 - 3, cy);
            gpu(c.B > 25 && c.R < 240, $"live change Point -> Linear without rebuild ({c})");
        }
        cmd.Reset();
        Uniforms(edge[0], Protocol.UniformSet.Clear, 0.5f, 0.5f, 0.04f, null, 0);
        Apply("sampler removed (CLEAR)");
        s.Pump(6);
        using (var a = s.Grab(w, "sampler_live_removed.png"))
        {
            int cy = (int)(Cell(0, 0).y + CellH / 2);
            var c = Px(a, (int)Cell(0, 0).x + 80 - 3, cy);
            gpu(Near(c, 255, 0, 0), $"sampler removed: back to the layer as it is ({c})");
        }
        cmd.Reset();
        cmd.BeginUniforms(edge[0], 0);
        cmd.UniformSampler("nope", S(0, 0, 0, 0));
        cmd.EndUniforms();
        Apply("sampler state of an unknown name");
        s.Pump(4);
        var err = Err(1);
        s.Check(err.Contains("nope") && err.Contains("sampler2D"), $"unknown sampler name -> EV_ERROR ({err})");
        cmd.Reset();
        cmd.BeginUniforms(edge[0], Protocol.UniformSet.Clear);
        cmd.UniformSampler("source", S(0, 3, 0, 0));
        cmd.EndUniforms();
        var rc = ApplyRc();
        s.Check(rc == -7, $"out of range state (mip 3) -> apply error -7 ({rc})");
        Teardown();
    }

    private static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
