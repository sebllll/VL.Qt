using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VL.Qt.Wire;
using VL.Qt;

/// <summary>
/// (images package 03): image resources of QtNative. PNG/JPG 1080p + 4K async to Ready, missing/broken file
/// Failed, every pixel format of the table pixel exact against a reference (readback, with row padding), one warning
/// per resource for 16 bit/float, replacement without an empty frame, release during the upload, sync wait, and the
/// perf runs (skipped with warn-only perf limits, CI): 20 x 4K async while an animation runs (hitches vs. background)
/// and set_pixels 1080p per frame. Without the shared upload QRhi (Linux, software backend) the CPU copy is the image:
/// same loads, info, readback, failures and replacement, but no stripes.
/// Numbers are printed as INFO for the PR; VLQT_IMG_UPLOAD=worker runs the same with the worker upload path.
/// </summary>
[TestFixture, NonParallelizable]
public static class ImageResourceTest
{
    private const uint Rect = 1;

    private sealed record Rec(ushort Ev, ulong Id, byte[] Payload);

    /// <summary>Image resources.</summary>
    [Test, Category("image-resource")]
    public static void ImageResource() => Scenario.Run(s => Run(s));

    private static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "image-resource");
        Directory.CreateDirectory(dir);
        var cmd = new CommandWriter();
        // Window with a moving rect. The perf parts pace the frames to 60 Hz like the vvvv frame clock (spin, the pump
        // itself never waits for VSync); a frame whose interval exceeds 1.5 x 16.7 ms is a hitch.
        var w = Native.vlqt_window_create(60, 60, 480, 270, "VL.Qt image resources", 0);
        s.Check(w != SceneHandle.Null, "window " + (w == SceneHandle.Null ? Native.LastError() : ""));
        cmd.Reset();
        cmd.ClearColor(0.1f, 0.1f, 0.1f, 1);
        cmd.Create(Rect, Protocol.ElementType.Rectangle);
        cmd.SetPlace(Rect, 20, 20, 80, 60);
        cmd.SetColor(Rect, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
        cmd.Reparent(Rect, 0, 0);
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_show(w);

        var events = new List<Rec>();
        var buf = new byte[64 * 1024];
        int frame = 0;
        var intervals = new List<double>();
        var sw = Stopwatch.StartNew();
        double last = 0, next = 0;
        bool paced = false;
        const double Period = 1000.0 / 60;

        void Frame()
        {
            if (paced)
            {
                next += Period;
                if (sw.Elapsed.TotalMilliseconds > next) next = sw.Elapsed.TotalMilliseconds; // behind: no catch-up burst
                while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50);
            }
            cmd.Reset();
            cmd.SetPlace(Rect, 20 + frame++ % 300, 20, 80, 60);
            Native.Apply(w, cmd.EndFrame());
            s.Pump(1);
            var now = sw.Elapsed.TotalMilliseconds;
            intervals.Add(now - last);
            last = now;
            var n = NativeCalls.ResPoll(buf, out var required);
            if (n == (int)Native.Result.BufferTooSmall)
            {
                buf = new byte[required * 2];
                n = NativeCalls.ResPoll(buf);
            }
            for (int at = 0; at < n;)
            {
                var len = BitConverter.ToUInt16(buf, at + 2);
                events.Add(new Rec(BitConverter.ToUInt16(buf, at), BitConverter.ToUInt64(buf, at + 4), buf.AsSpan(at + 12, len - 8).ToArray()));
                at += (4 + len + 3) & ~3;
            }
        }

        IEnumerable<Rec> Of(ulong id, ushort ev) => events.Where(e => e.Id == id && e.Ev == ev);
        byte Last(ulong id) => Of(id, Protocol.ResourceEventId.State).LastOrDefault()?.Payload[0] ?? 0;
        string Text(ulong id, ushort ev) => string.Join(" | ", Of(id, ev).Select(e => Encoding.UTF8.GetString(e.Payload)));
        (uint w, uint h, uint fmt, ulong bytes) Info(ulong id)
        {
            var p = Of(id, Protocol.ResourceEventId.Info).LastOrDefault()?.Payload;
            return p == null ? default : (BitConverter.ToUInt32(p, 0), BitConverter.ToUInt32(p, 4), BitConverter.ToUInt32(p, 8), BitConverter.ToUInt64(p, 12));
        }
        bool Settle(int timeoutMs, IEnumerable<ulong> ids)
        {
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < timeoutMs)
            {
                Frame();
                if (ids.All(i => Last(i) is Protocol.ResourceState.Ready or Protocol.ResourceState.Failed))
                    return true;
            }
            return false;
        }
        ulong Create()
        {
            var rc = Native.vlqt_res_create(Protocol.ResourceKind.Image, out var id);
            if (rc != 0) s.Check(false, $"create ({rc})");
            return id;
        }
        byte[] Read(ulong id, out int rw, out int rh)
        {
            var rc = NativeCalls.ReadPixels(id, Span<byte>.Empty, out rw, out rh, out var required);
            if (rc != (int)Native.Result.BufferTooSmall) return [];
            var px = new byte[required];
            return NativeCalls.ReadPixels(id, px, out rw, out rh) == px.Length ? px : [];
        }
        static bool Near(byte[] px, int width, int x, int y, int r, int g, int b, int tol)
        {
            var i = (y * width + x) * 4;
            return px.Length > i + 3 && Math.Abs(px[i] - r) <= tol && Math.Abs(px[i + 1] - g) <= tol && Math.Abs(px[i + 2] - b) <= tol;
        }

        for (int i = 0; i < 10; i++) Frame();
        // Decodes run FIFO; with a single worker (2-core CI runners) a leftover job of an earlier scenario delays every load.
        var leftover = new Native.vlqt_res_info[64];
        var leftoverCount = Native.ResList(leftover);
        s.Check(leftoverCount == 0, $"no resources left from earlier scenarios ({leftoverCount})");
        // Upload path: GPU textures only with the shared upload QRhi (Windows D3D11); otherwise (Linux, software backend)
        // the CPU copy is the image, Ready right after the decode, without stripes.
        var gpuUpload = NativeTest.vlqt_test_device_info(SceneHandle.Null, out _, out var uploadRhi) == 0 && uploadRhi != IntPtr.Zero;
        s.Info($"upload path: {(gpuUpload ? "GPU (shared upload QRhi)" : "CPU copy (no upload QRhi)")}");
        s.Check(Native.vlqt_image_load(1, "x.png", 0) == Native.Result.UnknownId, "load_image on an unknown id -> UnknownId");
        var testKind = Native.vlqt_res_create(TestProtocol.ResourceKind.Test, out var tid) == 0 ? tid : 0;
        s.Check(Native.vlqt_image_load(testKind, "x.png", 0) == Native.Result.BadArgument, "load_image on a test resource -> BadArgument (wrong kind)");
        Native.vlqt_res_release(testKind);

        // 1. Files: PNG/JPG 1080p + 4K async, missing and broken file.
        var files = new List<(string path, int w, int h, int tol)>();
        foreach (var (name, fw, fh) in new[] { ("p1080", 1920, 1080), ("p4k", 3840, 2160) })
            foreach (var ext in new[] { "png", "jpg" })
            {
                var path = Path.Combine(dir, $"{name}.{ext}");
                s.Check(NativeTest.vlqt_test_write_image(path, fw, fh, 90) == 0, $"test image {name}.{ext} written");
                files.Add((path, fw, fh, ext == "jpg" ? 12 : 0));
            }
        var broken = Path.Combine(dir, "broken.png");
        File.WriteAllBytes(broken, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6, 7, 8]);
        var fileIds = files.Select(f => { var id = Create(); Native.vlqt_image_load(id, f.path, 0); return id; }).ToList();
        var missing = Create();
        s.Check(Native.vlqt_image_load(missing, Path.Combine(dir, "does-not-exist.png"), 0) == 0, "load of a missing file starts (fails async)");
        var brokenId = Create();
        Native.vlqt_image_load(brokenId, broken, 0);
        s.Check(Settle(20000, fileIds.Append(missing).Append(brokenId)), "all file loads settle");
        for (int i = 0; i < files.Count; i++)
        {
            var (path, fw, fh, tol) = files[i];
            var name = Path.GetFileName(path);
            var info = Info(fileIds[i]);
            s.Check(Last(fileIds[i]) == Protocol.ResourceState.Ready && info.w == fw && info.h == fh,
                $"{name} Ready {info.w}x{info.h}, format {info.fmt}, {info.bytes >> 20} MB ({Text(fileIds[i], Protocol.ResourceEventId.Error)})");
            var px = Read(fileIds[i], out var rw, out var rh);
            s.Check(rw == fw && Near(px, rw, fw / 8, fh / 2, 255, 0, 0, tol) && Near(px, rw, fw * 3 / 8, fh / 2, 0, 255, 0, tol)
                && Near(px, rw, fw * 5 / 8, fh / 2, 0, 0, 255, tol) && Near(px, rw, fw * 7 / 8, fh / 2, 255, 255, 255, tol),
                $"{name} readback shows the red/green/blue/white stripes");
        }
        s.Check(Last(missing) == Protocol.ResourceState.Failed && Text(missing, Protocol.ResourceEventId.Error).Length > 0,
            $"missing file -> Failed ({Text(missing, Protocol.ResourceEventId.Error)})");
        s.Check(Last(brokenId) == Protocol.ResourceState.Failed && Text(brokenId, Protocol.ResourceEventId.Error).Length > 0,
            $"broken file -> Failed ({Text(brokenId, Protocol.ResourceEventId.Error)})");

        // 2. Pixel formats: 4 pixels per row (red, green, blue, a colour with alpha 128), 3 rows, 8 bytes padding.
        (byte r, byte g, byte b, byte a)[] colours = [(255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (200, 100, 50, 128)];
        static byte Pm(byte c, byte a) => (byte)((c * a + 127) / 255);
        var formatCases = new List<(string name, int fmt, int bpp, bool pm, bool warn, Func<(byte r, byte g, byte b, byte a), byte[]> enc, Func<(byte r, byte g, byte b, byte a), (int r, int g, int b, int a)> exp, int tol)>
        {
            ("R8G8B8A8 premultiplied", 7, 4, true, false, c => [Pm(c.r, c.a), Pm(c.g, c.a), Pm(c.b, c.a), c.a], c => (Pm(c.r, c.a), Pm(c.g, c.a), Pm(c.b, c.a), c.a), 0),
            ("R8G8B8A8", 7, 4, false, false, c => [c.r, c.g, c.b, c.a], c => (Pm(c.r, c.a), Pm(c.g, c.a), Pm(c.b, c.a), c.a), 1),
            ("B8G8R8A8 premultiplied", 9, 4, true, false, c => [Pm(c.b, c.a), Pm(c.g, c.a), Pm(c.r, c.a), c.a], c => (Pm(c.r, c.a), Pm(c.g, c.a), Pm(c.b, c.a), c.a), 0),
            ("B8G8R8A8", 9, 4, false, false, c => [c.b, c.g, c.r, c.a], c => (Pm(c.r, c.a), Pm(c.g, c.a), Pm(c.b, c.a), c.a), 1),
            ("R8G8B8X8", 6, 4, false, false, c => [c.r, c.g, c.b, 0x11], c => (c.r, c.g, c.b, 255), 0),
            ("B8G8R8X8", 8, 4, false, false, c => [c.b, c.g, c.r, 0x11], c => (c.r, c.g, c.b, 255), 0),
            ("R8G8B8", 4, 3, false, false, c => [c.r, c.g, c.b], c => (c.r, c.g, c.b, 255), 0),
            ("B8G8R8", 5, 3, false, false, c => [c.b, c.g, c.r], c => (c.r, c.g, c.b, 255), 0),
            ("R8", 1, 1, false, false, c => [c.g], c => (c.g, c.g, c.g, 255), 0),
            ("R16", 2, 2, false, true, c => BitConverter.GetBytes((ushort)(c.g * 257)), c => (c.g, c.g, c.g, 255), 0),
            ("R32F", 3, 4, false, true, c => BitConverter.GetBytes(c.g / 255f), c => (c.g, c.g, c.g, 255), 0),
            ("R32G32F", 11, 8, false, true, c => [.. BitConverter.GetBytes(c.r / 255f), .. BitConverter.GetBytes(c.g / 255f)], c => (c.r, c.g, 0, 255), 0),
            ("R32G32B32A32F", 10, 16, false, true, c => [.. BitConverter.GetBytes(c.r / 255f), .. BitConverter.GetBytes(c.g / 255f), .. BitConverter.GetBytes(c.b / 255f), .. BitConverter.GetBytes(c.a / 255f)],
                c => (Pm(c.r, c.a), Pm(c.g, c.a), Pm(c.b, c.a), c.a), 1),
            ("R16G16B16A16F", 12, 8, false, true, c => [.. BitConverter.GetBytes((Half)(c.r / 255f)), .. BitConverter.GetBytes((Half)(c.g / 255f)), .. BitConverter.GetBytes((Half)(c.b / 255f)), .. BitConverter.GetBytes((Half)(c.a / 255f))],
                c => (Pm(c.r, c.a), Pm(c.g, c.a), Pm(c.b, c.a), c.a), 1),
        };
        const int Pw = 4, Ph = 3, Pad = 8;
        byte[] Pixels(int bpp, Func<(byte r, byte g, byte b, byte a), byte[]> enc, out int rowBytes)
        {
            rowBytes = Pw * bpp + Pad;
            var data = Enumerable.Repeat((byte)0xEE, rowBytes * Ph).ToArray();
            for (int y = 0; y < Ph; y++)
                for (int x = 0; x < Pw; x++)
                    enc(colours[x]).CopyTo(data, y * rowBytes + x * bpp);
            return data;
        }
        var deepIds = new List<ulong>();
        foreach (var (name, fmt, bpp, pm, warn, enc, exp, tol) in formatCases)
        {
            var id = Create();
            var data = Pixels(bpp, enc, out var rowBytes);
            var desc = new Native.vlqt_pixel_desc { width = Pw, height = Ph, row_bytes = rowBytes, format = fmt, premultiplied = pm ? 1 : 0 };
            var rc = Native.ImageSetPixels(id, desc, data);
            var state = Native.vlqt_res_wait(id, 5000);
            Frame();
            var px = Read(id, out var rw, out var rh);
            var bad = new List<string>();
            for (int y = 0; y < Ph; y++)
                for (int x = 0; x < Pw; x++)
                {
                    var e = exp(colours[x]);
                    var i = (y * Pw + x) * 4;
                    if (px.Length < i + 4 || Math.Abs(px[i] - e.r) > tol || Math.Abs(px[i + 1] - e.g) > tol || Math.Abs(px[i + 2] - e.b) > tol || Math.Abs(px[i + 3] - e.a) > tol)
                        bad.Add(px.Length < i + 4 ? $"({x},{y}) missing" : $"({x},{y}) {px[i]},{px[i + 1]},{px[i + 2]},{px[i + 3]} != {e}");
                }
            s.Check(rc == 0 && state == Protocol.ResourceState.Ready && rw == Pw && rh == Ph && bad.Count == 0,
                $"{name} pixel exact{(tol > 0 ? $" (±{tol}, premultiply)" : "")} with row padding (rc {rc}, state {state}{(bad.Count > 0 ? ", " + string.Join("; ", bad.Take(3)) : "")})");
            var warnings = Of(id, Protocol.ResourceEventId.Warning).Count();
            s.Check(warnings == (warn ? 1 : 0), $"{name}: {warnings} warning(s), expected {(warn ? 1 : 0)} {Text(id, Protocol.ResourceEventId.Warning)}");
            if (warn) deepIds.Add(id);
            else Native.vlqt_res_release(id);
        }
        // A deep resource replaced twice more: still exactly one warning.
        var deep = deepIds[0];
        var deepCase = formatCases.First(c => c.warn);
        for (int i = 0; i < 2; i++)
        {
            var data = Pixels(deepCase.bpp, deepCase.enc, out var rb);
            Native.ImageSetPixels(deep, new Native.vlqt_pixel_desc { width = Pw, height = Ph, row_bytes = rb, format = deepCase.fmt }, data);
            Native.vlqt_res_wait(deep, 5000);
            Frame();
        }
        s.Check(Of(deep, Protocol.ResourceEventId.Warning).Count() == 1, $"{deepCase.name} replaced twice: still one warning");
        foreach (var id in deepIds) Native.vlqt_res_release(id);
        foreach (var fmt in new[] { 0, 13 })
        {
            var id = Create();
            var rc = Native.ImageSetPixels(id, new Native.vlqt_pixel_desc { width = 2, height = 2, format = fmt }, new byte[64]);
            var state = Native.vlqt_res_wait(id, 5000);
            Frame();
            s.Check(rc == 0 && state == Protocol.ResourceState.Failed && Text(id, Protocol.ResourceEventId.Error).Contains("format"),
                $"unknown pixel format {fmt} -> Failed ({Text(id, Protocol.ResourceEventId.Error)})");
            Native.vlqt_res_release(id);
        }
        var shortId = Create();
        s.Check(Native.ImageSetPixels(shortId, new Native.vlqt_pixel_desc { width = 4, height = 4, format = 7 }, new byte[10]) == Native.Result.BadArgument,
            "set_pixels with too few bytes -> BadArgument");
        Native.vlqt_res_release(shortId);

        // 3. Replacement: the old content stays readable until the new one is uploaded, no STATE record, new INFO.
        byte[] Solid(int pw, int ph, byte r, byte g, byte b)
        {
            var d = new byte[pw * ph * 4];
            for (int i = 0; i < d.Length; i += 4) { d[i] = r; d[i + 1] = g; d[i + 2] = b; d[i + 3] = 255; }
            return d;
        }
        var rep = Create();
        Native.ImageSetPixels(rep, new Native.vlqt_pixel_desc { width = 64, height = 32, format = 7, premultiplied = 1 }, Solid(64, 32, 255, 0, 0));
        Native.vlqt_res_wait(rep, 5000);
        Frame();
        var statesBefore = Of(rep, Protocol.ResourceEventId.State).Count();
        Native.ImageSetPixels(rep, new Native.vlqt_pixel_desc { width = 128, height = 64, format = 7, premultiplied = 1 }, Solid(128, 64, 0, 255, 0));
        var old = Read(rep, out var ow, out _);
        s.Check(ow == 64 && Near(old, ow, 10, 10, 255, 0, 0, 0), "replacement: old content still there right after set_pixels");
        var t0 = Stopwatch.StartNew();
        while (Info(rep).w != 128 && t0.ElapsedMilliseconds < 5000) Frame();
        var neu = Read(rep, out var nw, out _);
        s.Check(nw == 128 && Near(neu, nw, 10, 10, 0, 255, 0, 0) && Of(rep, Protocol.ResourceEventId.State).Count() == statesBefore && Last(rep) == Protocol.ResourceState.Ready,
            $"replacement: new content {nw} wide after the upload, state stayed Ready without a STATE record");
        Native.vlqt_res_release(rep);

        // 4. Release during the upload (pixels: small budget = many stripes; files: during the worker upload) and sync wait.
        var stripes = gpuUpload && Environment.GetEnvironmentVariable("VLQT_IMG_UPLOAD") != "worker";
        Native.vlqt_res_set_budget(1 << 20);
        var rel = Create();
        Native.ImageSetPixels(rel, new Native.vlqt_pixel_desc { width = 3840, height = 2160, format = 7, premultiplied = 1 }, Solid(3840, 2160, 9, 9, 9));
        var t1 = Stopwatch.StartNew();
        bool Settled(ulong id) => Last(id) is Protocol.ResourceState.Ready or Protocol.ResourceState.Failed;
        while (Last(rel) != Protocol.ResourceState.Uploading && !Settled(rel) && t1.ElapsedMilliseconds < 10000) Frame();
        Frame();
        if (stripes)
            s.Check(Last(rel) == Protocol.ResourceState.Uploading, "4K pixels with 1 MB budget: still Uploading after a frame (stripes)");
        else if (!gpuUpload)
            s.Check(Last(rel) == Protocol.ResourceState.Ready, $"4K pixels without upload QRhi: Ready after the decode (CPU copy, no stripes; state {Last(rel)})");
        s.Check(Native.vlqt_res_release(rel) == 0, $"release {(stripes ? "while Uploading" : "after the upload")} (1 MB budget, 4K pixels)");
        var relFile = Create();
        Native.vlqt_image_load(relFile, files[2].path, 0);
        Frame();
        s.Check(Native.vlqt_res_release(relFile) == 0, "release while the 4K PNG is decoded/uploaded in the worker");
        for (int i = 0; i < 3; i++) Frame();
        var listed = new Native.vlqt_res_info[256];
        var cnt = Native.ResList(listed);
        var t2 = Stopwatch.StartNew();
        while (Native.ResList(listed) is var c && listed.Take(Math.Min(c, listed.Length)).Any(r => r.id == relFile) && t2.ElapsedMilliseconds < 5000) Frame();
        cnt = Native.ResList(listed);
        s.Check(!listed.Take(Math.Min(cnt, listed.Length)).Any(r => r.id == rel || r.id == relFile) && Native.vlqt_res_wait(rel, 0) == (int)Native.Result.UnknownId
            && !events.Any(e => e.Id == relFile && e.Ev == Protocol.ResourceEventId.State && e.Payload[0] == Protocol.ResourceState.Ready), "released during the upload: freed, id stale, no Ready");
        Native.vlqt_res_set_budget(8 << 20);
        var syncId = Create();
        var tw = Stopwatch.StartNew();
        Native.vlqt_image_load(syncId, files[2].path, 0);
        var syncState = Native.vlqt_res_wait(syncId, 10000);
        var syncMs = tw.Elapsed.TotalMilliseconds;
        s.Check(syncState == Protocol.ResourceState.Ready, $"vlqt_res_wait 4K PNG -> Ready in {syncMs:F0} ms (decode + full upload)");
        s.Info($"sync load 4K PNG: {syncMs:F0} ms");
        Native.vlqt_res_release(syncId);
        foreach (var id in fileIds.Append(missing).Append(brokenId)) Native.vlqt_res_release(id);
        for (int i = 0; i < 3; i++) Frame();

        // The perf runs (~10 s locally, much more on a 2-core runner) only where their limits count: with warn-only
        // (shared CI runners, VLQT_PERF_WARN_ONLY=1) they would only cost time in the 2-minute budget of the smoke job.
        if (PerfLimits.SkipHeavy)
            s.Skip("perf runs 20 x 4K async + set_pixels 1080p per frame: heavy perf measurement skipped on shared CI runners");
        else
            RunPerf();
        s.Check(!s.Log.Contains("without a resource"), "no worker result without a resource");
        Native.vlqt_scene_destroy(w);
        s.Pump(2);

        void RunPerf()
        {
            // 5. Perf: 20 x 4K async while the rect moves. Hitch = interval > 1.5 x median of the background run.
            var mode = !gpuUpload ? "CPU copy, no upload QRhi" : Environment.GetEnvironmentVariable("VLQT_IMG_UPLOAD") switch { "worker" => "worker", "stripes" => "stripes", _ => "auto: files worker, pixels stripes" };
            paced = true;
            next = sw.Elapsed.TotalMilliseconds;
            intervals.Clear();
            for (int i = 0; i < 300; i++) Frame();
            var background = intervals.Skip(1).ToList();
            var median = background.OrderBy(x => x).ElementAt(background.Count / 2);
            const double limit = Period * 1.5;
            var loadIds = Enumerable.Range(0, 20).Select(i => { var id = Create(); Native.vlqt_image_load(id, files[2 + (i & 1)].path, 0); return id; }).ToList();
            intervals.Clear();
            var tl = Stopwatch.StartNew();
            var allReady = Settle(60000, loadIds);
            var loadMs = tl.Elapsed.TotalMilliseconds;
            var loading = intervals.Skip(1).ToList();
            int Hitches(List<double> l) => l.Count(x => x > limit);
            double Rate(List<double> l) => l.Count == 0 ? 0 : 100.0 * Hitches(l) / l.Count;
            s.Check(allReady && loadIds.All(i => Last(i) == Protocol.ResourceState.Ready), $"x 4K async Ready in {loadMs:F0} ms ({mode})");
            s.Info($"perf 20x4K ({mode}): background {Hitches(background)}/{background.Count} hitches ({Rate(background):F1} %), "
                + $"load frames {Hitches(loading)}/{loading.Count} ({Rate(loading):F1} %), median {median:F2} ms, max load {loading.DefaultIfEmpty().Max():F1} ms");
            PerfLimits.Below(s, Rate(loading), Math.Max(Rate(background) * 3, 5), $"hitch rate while loading 20 x 4K ({mode}) in the range of the background, %");
            foreach (var id in loadIds) Native.vlqt_res_release(id);
            for (int i = 0; i < 3; i++) Frame();

            // 6. set_pixels 1080p per frame (video case).
            var video = Create();
            var vdata = Solid(1920, 1080, 0, 0, 255);
            var vdesc = new Native.vlqt_pixel_desc { width = 1920, height = 1080, format = 9, premultiplied = 1 };
            var setMs = new List<double>();
            intervals.Clear();
            for (int i = 0; i < 180; i++)
            {
                vdata[0] = (byte)i;
                var ts = Stopwatch.GetTimestamp();
                Native.ImageSetPixels(video, vdesc, vdata);
                setMs.Add(Stopwatch.GetElapsedTime(ts).TotalMilliseconds);
                Frame();
            }
            var vint = intervals.Skip(1).ToList();
            var vfps = 1000.0 / vint.Average();
            var infos = Of(video, Protocol.ResourceEventId.Info).Count();
            s.Info($"set_pixels 1080p per frame ({mode}): {vfps:F1} fps, set_pixels median {setMs.OrderBy(x => x).ElementAt(setMs.Count / 2):F2} ms, "
                + $"{infos} uploads in 180 frames, {Hitches(vint)} hitches");
            s.Check(infos > (stripes ? 170 : 100), $"set_pixels per frame: {infos} of 180 frames uploaded");
            PerfLimits.Below(s, 1000.0 / vfps, 1000.0 / 55, $"set_pixels 1080p per frame holds ~60 fps ({mode}), mean interval ms");
            Native.vlqt_res_release(video);
            paced = false;
            Frame();
        }
    }
}
