using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// example shader help/Qt/shaders/blur_mask.frag (separable Gaussian blur, radius per pixel from a grayscale mask),
// two chained ShaderEffects (pass 1 horizontal, pass 2 vertical). (1) Correctness: stripes + mask (left black, right white)
// -> left sharp, right soft. (2) Performance at 1920x1080 and 3840x2160 pixels, radius 0 / 8 / 32 / 64, CPU (sync + render)
// and GPU time per frame (QRhi timestamps of QtNative, 0 = not available), against the same scene without blur.
// Raw commands (DIP). Grab: <outDir>/blur-mask_result.png, table: build/smoke/blur-mask-perf.md (parent of the grabs folder).
[TestFixture, NonParallelizable]
public static class BlurMaskTest
{
    [Test, Category("blur-mask")]
    public static void BlurMask() => Scenario.Run(Run);

    private const uint Img = 1, Pass1 = 2, Pass2 = 3, Dot = 4;
    // CI (VLQT_PERF_WARN_ONLY, shared runners often without GPU = WARP): the numbers carry no meaning there, so only a
    // short run at 1920x1080 checks that the chain renders; the full measurement (both sizes, 60 frames) runs locally.
    private static bool Quick => PerfLimits.SkipHeavy;
    private static int Frames => Quick ? 5 : 60;
    private static int Warmup => Quick ? 2 : 15;

    private static string? FindFile(params string[] rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, Path.Combine(rel));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // --- minimal PNG writer (8 bit, grey or RGB), independent of System.Drawing (not on Linux) ---
    private static readonly uint[] s_crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in t.Concat(data)) crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        s.Write(len);
    }

    private static void WritePng(string path, int w, int h, int channels, Func<int, int, (byte r, byte g, byte b)> px)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
        {
            var row = new byte[1 + w * channels];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var (r, g, b) = px(x, y);
                    if (channels == 1) row[1 + x] = r;
                    else { row[1 + 3 * x] = r; row[2 + 3 * x] = g; row[3 + 3 * x] = b; }
                }
                z.Write(row);
            }
        }
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = (byte)(channels == 1 ? 0 : 2);
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    private sealed record Row(string Size, string Mode, string Case, double Wall, double Cpu, double Gpu, double GpuMax);

    private static void Run(Scenario s)
    {
        Action<bool, string> gpuCheck = (ok, what) => s.NeedsRhi(ok, what, "Shader needs a QRhi scenegraph");
        var shaderFile = FindFile("help", "Qt", "shaders", "blur_mask.frag");
        s.Check(shaderFile != null, $"blur_mask.frag found ({shaderFile})");
        if (shaderFile == null) return;
        Directory.CreateDirectory(s.OutDir);

        // Test images: stripes 40 px wide (black / white), mask split at x = 420 of 800, ramp mask for the measurements,
        // content for the measurements (RGB, gradient + checkers, so the blur has something to do).
        var stripes = Path.Combine(s.OutDir, "blur-mask_stripes.png");
        var maskSplit = Path.Combine(s.OutDir, "blur-mask_mask_split.png");
        var maskRamp = Path.Combine(s.OutDir, "blur-mask_mask_ramp.png");
        WritePng(stripes, 800, 600, 1, (x, y) => ((byte)((x / 40) % 2 == 0 ? 0 : 255), (byte)0, (byte)0));
        WritePng(maskSplit, 800, 8, 1, (x, y) => ((byte)(x < 420 ? 0 : 255), (byte)0, (byte)0));
        WritePng(maskRamp, 1024, 8, 1, (x, y) => ((byte)(x * 255 / 1023), (byte)0, (byte)0));
        string Content(int w, int h)
        {
            var p = Path.Combine(s.OutDir, $"blur-mask_content_{w}x{h}.png");
            if (!File.Exists(p))
                WritePng(p, w, h, 3, (x, y) => ((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)(((x / 32 + y / 32) & 1) * 200)));
            return p;
        }

        ulong LoadImage(string path)
        {
            Native.vlqt_res_create(Protocol.ResourceKind.Image, out var id);
            Native.vlqt_image_load(id, path, 0);
            s.Check(Native.vlqt_res_wait(id, 20000) == Protocol.ResourceState.Ready, $"image {Path.GetFileName(path)} Ready");
            return id;
        }
        var code = File.ReadAllText(shaderFile);
        Native.vlqt_res_create(Protocol.ResourceKind.Shader, out var shader);
        var rcShader = Native.vlqt_shader_load_source(shader, code, Protocol.ShaderStage.Fragment);
        var sShader = rcShader == 0 ? Native.vlqt_res_wait(shader, 20000) : Protocol.ResourceState.Failed;
        s.Check(sShader == Protocol.ResourceState.Ready, $"blur_mask.frag bakes -> Ready ({sShader}, rc {rcShader})");
        if (sShader != Protocol.ResourceState.Ready) return;

        var imgStripes = LoadImage(stripes);
        var imgMaskSplit = LoadImage(maskSplit);
        var imgMaskRamp = LoadImage(maskRamp);

        var cmd = new CommandWriter();
        int built = 0; // 0 nothing, 1 blur scene (Pass2 owns Pass1 and Img), 2 reference (Img)
        SceneHandle w = SceneHandle.Null;
        void Apply(string what)
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            if (rc != 0) s.Check(false, $"{what} ({rc}) {Native.LastError()}");
        }
        // Destroying the top element destroys its children (Pass2 -> Pass1 -> Img).
        void Teardown()
        {
            if (built == 0) return;
            cmd.Destroy(built == 1 ? Pass2 : Img);
            cmd.Destroy(Dot);
        }
        void Pass(uint id, uint child, float ew, float eh, ulong mask, float radius, float pxw, float pxh, float dx, float dy)
        {
            cmd.Create(id, Protocol.ElementType.Shader);
            cmd.SetPlace(id, 0, 0, ew, eh);
            cmd.SetU64(id, Protocol.PropertyId.FragmentShader, shader);
            cmd.Reparent(child, id, 0);
            cmd.SetRef(id, Protocol.PropertyId.Source, child);
            cmd.BeginUniforms(id, Protocol.UniformSet.Clear);
            cmd.UniformF32("radius", radius);
            cmd.UniformVec2("size", pxw, pxh);
            cmd.UniformVec2("direction", dx, dy);
            cmd.UniformU64("mask", mask);
            cmd.EndUniforms();
        }
        // blur = false: the image element directly in the window (reference). Dot: small rectangle that moves to force frames.
        void Build(float ew, float eh, float dpr, ulong image, ulong mask, float radius, bool blur)
        {
            cmd.Clear();
            Teardown();
            cmd.Create(Img, Protocol.ElementType.Image);
            cmd.SetU64(Img, Protocol.PropertyId.Image, image);
            cmd.SetI32(Img, Protocol.PropertyId.FillMode, 0);
            cmd.SetPlace(Img, 0, 0, ew, eh);
            if (blur)
            {
                float pxw = ew * dpr, pxh = eh * dpr;
                Pass(Pass1, Img, ew, eh, mask, radius, pxw, pxh, 1, 0);
                Pass(Pass2, Pass1, ew, eh, mask, radius, pxw, pxh, 0, 1);
                cmd.Reparent(Pass2, 0, 1000);
            }
            else cmd.Reparent(Img, 0, 1000);
            cmd.Create(Dot, Protocol.ElementType.Rectangle);
            cmd.SetPlace(Dot, 0, 0, 4, 4);
            cmd.SetColor(Dot, Protocol.PropertyId.Color, 1, 0, 0, 1);
            cmd.Reparent(Dot, 0, 2000);
            Apply("build");
            built = blur ? 1 : 2;
            s.Pump(6);
        }
        void SetRadius(float radius)
        {
            foreach (var id in new[] { Pass1, Pass2 })
            {
                cmd.BeginUniforms(id, 0);
                cmd.UniformF32("radius", radius);
                cmd.EndUniforms();
            }
        }

        // ---------------- (1) correctness ----------------
        w = NativeCalls.CreateWindow(800, 600, "VL.Qt blur mask test");
        s.Check(w != SceneHandle.Null, "window_create");
        Native.vlqt_window_show(w);
        s.Pump(4);
        float dpr;
        using (var probe = s.Grab(w, "blur-mask_probe.png"))
            dpr = Math.Max(1f, probe.Width / 800f);
        s.Info($"blur-mask: device pixel ratio {dpr:F2}");
        const float testRadius = 40;
        Build(800, 600, dpr, imgStripes, imgMaskSplit, testRadius, true);
        using (var a = s.Grab(w, "blur-mask_result.png"))
        {
            Color P(int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
            // left (mask black): edge at x = 200 stays sharp; right (mask white): edge at x = 600 is blurred
            var l1 = P(197, 300).R; var l2 = P(203, 300).R;
            var r1 = P(597, 300).R; var r2 = P(603, 300).R;
            // normalized blur: the mean over two full stripe periods (560..720, black | white) stays ~50 %. Not the stripe
            // centre: radius is in layer pixels, the stripes in DIP, so the centre value depends on the device pixel ratio.
            double sum = 0; int n = 0;
            for (int x = 560; x < 720; x++, n++) sum += P(x, 300).R;
            var mean = (int)Math.Round(sum / n);
            gpuCheck(l1 < 30 && l2 > 225, $"left (mask black) the stripe edge stays sharp ({l1} | {l2})");
            gpuCheck(r1 > 40 && r1 < 200 && r2 > 40 && r2 < 215 && Math.Abs(r1 - r2) < 120,
                $"right (mask white) the stripe edge is soft ({r1} | {r2})");
            gpuCheck(mean is > 110 and < 145, $"the blur is normalized, mean over two stripe periods ~50 % ({mean})");
            int grey = 0;
            for (int x = 440; x < 760; x += 4) if (P(x, 300).R is > 20 and < 235) grey++;
            s.Info($"blur-mask: result grab {a.Width}x{a.Height}, {grey} soft pixels of 80 sampled on the right");
        }
        // radius 0 returns the original pixels
        cmd.Clear();
        SetRadius(0);
        Apply("radius 0");
        s.Pump(4);
        using (var a = s.Grab(w, "blur-mask_radius0.png"))
        {
            Color P(int x, int y) => a.GetPixel(x * a.Width / 800, y * a.Height / 600);
            gpuCheck(P(597, 300).R < 30 && P(603, 300).R > 225, $"radius 0 -> original pixels ({P(597, 300).R} | {P(603, 300).R})");
        }
        cmd.Clear();
        Teardown();
        Apply("destroy");
        built = 0;
        s.Pump(3);
        Native.vlqt_scene_destroy(w);
        s.Pump(2);

        // ---------------- (2) performance ----------------
        var rows = new List<Row>();
        var gpuAvailable = false;
        var notes = new List<string>();
        foreach (var (pw, ph, name) in Quick ? new[] { (1920, 1080, "1920x1080") } : new[] { (1920, 1080, "1920x1080"), (3840, 2160, "3840x2160") })
        {
            float ew = pw / dpr, eh = ph / dpr;
            var content = LoadImage(Content(pw, ph));
            w = NativeCalls.CreateWindow((int)MathF.Round(ew), (int)MathF.Round(eh), "VL.Qt blur mask perf " + name);
            Native.vlqt_window_show(w);
            s.Pump(4);
            using (var probe = s.Grab(w, "blur-mask_probe.png"))
            {
                    s.Info($"blur-mask: {name}: window grab {probe.Width}x{probe.Height} (wanted {pw}x{ph})");
                if (probe.Width != pw || probe.Height != ph) notes.Add($"{name}: the window was limited by the screen to {probe.Width}x{probe.Height} px; the element and its two layers keep the full size, only the visible part of pass 2 is smaller than in a real {name} output, so its GPU times are a lower bound.");
            }

            Row Measure(string mode, string label, float radius, bool blur, bool dirty)
            {
                Build(ew, eh, dpr, content, imgMaskRamp, radius, blur);
                var wall = new List<double>(); var cpu = new List<double>(); var gpu = new List<double>();
                for (int i = 0; i < Warmup + Frames; i++)
                {
                    cmd.Clear();
                    cmd.SetPlace(Dot, i % 50, 0, 4, 4);
                    if (blur && dirty) SetRadius(radius + (i % 2) * 0.01f);
                    Apply("frame");
                    var sw = Stopwatch.StartNew();
                    s.Pump(1);
                    var ms = sw.Elapsed.TotalMilliseconds;
                    if (i < Warmup) continue;
                    if (Native.vlqt_scene_get_perf_sample(w, out var sample) == 0 && sample.swaps > 0)
                    {
                        wall.Add(ms); cpu.Add(sample.sync + sample.render); gpu.Add(sample.gpu);
                    }
                }
                var g = Median(gpu);
                if (gpu.Any(x => x > 0)) gpuAvailable = true;
                return new Row(name, mode, label, Median(wall), Median(cpu), g, gpu.Count == 0 ? 0 : gpu.Max());
            }

            rows.Add(Measure("-", "no blur", 0, false, true));
            foreach (var mode in new[] { "dirty", "cached" })
                foreach (var r in new[] { 0f, 8f, 32f, 64f })
                    rows.Add(Measure(mode, $"r{r:0}", r, true, mode == "dirty"));
            cmd.Clear();
            Teardown();
            Apply("destroy");
            built = 0;
            s.Pump(3);
            Native.vlqt_scene_destroy(w);
            Native.vlqt_res_release(content);
            s.Pump(2);
        }

        // ---------------- report ----------------
        var inv = CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("F2", inv);
        var md = new StringBuilder();
        md.AppendLine("# blur-mask performance");
        md.AppendLine();
        md.AppendLine($"Two chained ShaderEffects (blur_mask.frag, 2 x 25 taps), ramp mask, median of {Frames} frames, device pixel ratio {dpr:F2}. " +
                      "dirty = radius changes every frame (both passes render), cached = only the window frame (pass 2 draws, pass 1 and the image come from their cached layers). " +
                      "CPU = sync + render, GPU = QRhi timestamps (" + (gpuAvailable ? "available" : "not available") + "), wall = duration of the pump call (may include the vsync wait).");
        md.AppendLine();
        md.AppendLine("| size | mode | case | wall ms | CPU ms | GPU ms | GPU max ms |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rows)
            md.AppendLine($"| {r.Size} | {r.Mode} | {r.Case} | {F(r.Wall)} | {F(r.Cpu)} | {(gpuAvailable ? F(r.Gpu) : "n/a")} | {(gpuAvailable ? F(r.GpuMax) : "n/a")} |");
        foreach (var n in notes) { md.AppendLine(); md.AppendLine("Note: " + n); s.Info("blur-mask: " + n); }
        var mdDir = Path.GetFileName(s.OutDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) == "grabs" ? Path.GetDirectoryName(s.OutDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))! : s.OutDir; // build/smoke next to the log
        File.WriteAllText(Path.Combine(mdDir, "blur-mask-perf.md"), md.ToString());
        foreach (var r in rows)
        {
            var verdict = r.Cpu <= 16.7 && (!gpuAvailable || r.Gpu <= 16.7) ? "60 fps OK" : "below 60 fps";
            s.Info($"blur-mask: {r.Size} {r.Mode,-6} {r.Case,-7} wall {F(r.Wall)} ms, CPU {F(r.Cpu)} ms, GPU {(gpuAvailable ? F(r.Gpu) + " ms (max " + F(r.GpuMax) + ")" : "n/a")} -> {verdict}");
        }
        if (!gpuAvailable) s.Info("blur-mask: GPU time not measurable here (gpu_ms = 0), only CPU times");
        s.Check(rows.Count == (Quick ? 9 : 18) && rows.All(r => r.Wall > 0), $"all {rows.Count} measurements delivered frames");
        Native.vlqt_res_release(shader);
        foreach (var id in new[] { imgStripes, imgMaskSplit, imgMaskRamp }) Native.vlqt_res_release(id);
        s.Pump(2);
    }
}