using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using VL.Qt;

/// <summary>
/// #361 (images package 04): one image resource in many Image elements and windows. 200 elements in 2 windows = one
/// upload (vlqt_res_list: bytes of one texture, 200 element references), a switch to a resource that is still loading
/// keeps the old image until it is Ready, a replacement of the same id shows up without an empty frame, destroying an
/// element while its resource loads leaks nothing, mipmaps draw. Perf (skipped with warn-only perf limits, CI): switching
/// between 50 preloaded 4K resources per frame, SET time and hitch rate against the background of the same run.
/// Without the shared device (Linux, software backend) the CPU copy is drawn: same checks, one texture per window.
/// </summary>
[TestFixture, NonParallelizable]
public static class ImageShareTest
{
    private const int Elements = 100; // per window

    /// <summary>Image sharing between windows.</summary>
    [Test, Category("image-share")]
    public static void ImageShare() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var dir = Path.Combine(s.OutDir, "image-share");
        Directory.CreateDirectory(dir);
        var stripesPath = ImageTest.FindAsset();
        s.Check(stripesPath != null, "test asset found");
        if (stripesPath == null)
            return;
        // 4 stripes red | green | blue | white (vlqt_test_write_image), 4K for the loads that take a few frames.
        var p4k = Path.Combine(dir, "p4k.png");
        s.Check(Native.vlqt_test_write_image(p4k, 3840, 2160, 90) == 0, "4K test image written");
        var gpu = Native.vlqt_test_device_info(IntPtr.Zero, out _, out var uploadRhi) == 0 && uploadRhi != IntPtr.Zero;
        s.Info($"texture path: {(gpu ? "shared device (wrapper per window)" : "CPU copy (texture per window)")}");

        var cmd = new CommandWriter();
        var w1 = Native.vlqt_window_create_at(40, 40, 400, 300, "VL.Qt image share 1", 0);
        var w2 = Native.vlqt_window_create_at(460, 40, 400, 300, "VL.Qt image share 2", 0);
        s.Check(w1 != IntPtr.Zero && w2 != IntPtr.Zero, "two windows");
        Native.vlqt_window_show(w1);
        Native.vlqt_window_show(w2);

        Native.vlqt_res_info Info(ulong id)
        {
            var list = new Native.vlqt_res_info[256];
            var n = Native.ResList(list);
            return list.Take(Math.Max(n, 0)).FirstOrDefault(r => r.id == id);
        }

        // 1. One resource in 100 elements per window (10 x 10 grid of 40 x 30).
        var stripes = ImageTest.Load(stripesPath, s.Check, "stripes");
        foreach (var w in new[] { w1, w2 })
        {
            cmd.Reset();
            cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
            for (uint i = 0; i < Elements; i++)
            {
                var id = 1 + i;
                cmd.Create(id, Protocol.Type.Image);
                cmd.SetPlace(id, i % 10 * 40, i / 10 * 30, 39, 29);
                cmd.SetBool(id, Protocol.Prop.Smooth, false);
                cmd.SetU64(id, Protocol.Prop.Image, stripes);
                cmd.Reparent(id, 0, 100000);
            }
            var rc = Native.Apply(w, cmd.EndFrame());
            s.Check(rc == 0, $"image elements ({rc}{(rc != 0 ? " " + Native.LastError() : "")})");
        }
        s.Pump(3);
        var info = Info(stripes);
        s.Check(info.element_refs == 2 * Elements && info.host_refs == 1, $"elements = 200 element references ({info.element_refs}, host {info.host_refs})");
        s.Check(!gpu || info.bytes == 30 * 10 * 4, $"one upload, VRAM once ({info.bytes} bytes for 30x10)");
        foreach (var (w, name) in new[] { (w1, "1"), (w2, "2") })
            using (var a = s.Grab(w, Path.Combine(dir, $"share_{name}.png")))
            {
                // Element 0 (0..39) and element 99 (360..399, 270..299): red left, blue right.
                Color Px(int x, int y) => a.GetPixel(x * a.Width / 400, y * a.Height / 300);
                s.Check(ImageTest.IsRed(Px(5, 15)) && ImageTest.IsBlue(Px(34, 15)) && ImageTest.IsRed(Px(365, 285)) && ImageTest.IsBlue(Px(394, 285)),
                    $"window {name} draws the shared image in the first and last element ({Px(5, 15)}, {Px(394, 285)})");
            }

        // 2. Switch element 1 of window 1 to a 4K resource that is still loading: the stripes stay until it is Ready.
        Color P1(Bitmap a, int x, int y) => a.GetPixel(x * a.Width / 400, y * a.Height / 300);
        Native.vlqt_res_create(Protocol.ResKind.Image, out var big);
        Native.vlqt_res_load_image(big, p4k, 0);
        cmd.Reset();
        cmd.SetU64(1, Protocol.Prop.Image, big);
        s.Check(Native.Apply(w1, cmd.EndFrame()) == 0, "SET image to a loading resource");
        s.Pump(1);
        if (Native.vlqt_res_wait(big, 0) != Protocol.ResState.Ready)
            using (var a = s.Grab(w1, Path.Combine(dir, "switch_loading.png")))
                s.Check(ImageTest.IsRed(P1(a, 5, 15)) && ImageTest.IsBlue(P1(a, 34, 15)), $"old image stays while the new one loads ({P1(a, 34, 15)})");
        else
            s.Skip("old image while loading: the 4K load was already Ready");
        s.Check(Native.vlqt_res_wait(big, 20000) == Protocol.ResState.Ready, "4K resource Ready");
        s.Pump(2);
        using (var a = s.Grab(w1, Path.Combine(dir, "switch_ready.png")))
        {
            var white = P1(a, 34, 15);
            s.Check(white.R > 200 && white.G > 200 && white.B > 200, $"new image after Ready (white stripe {white})");
        }
        s.Check(Info(stripes).element_refs == 2 * Elements - 1 && Info(big).element_refs == 1,
            $"element references moved ({Info(stripes).element_refs}, {Info(big).element_refs})");

        // 3. Replacement of the same id (stripes -> 4K content) in all elements of window 2: no empty frame.
        Native.vlqt_res_load_image(stripes, p4k, 0);
        var emptyFrames = 0;
        var sw = Stopwatch.StartNew();
        while (Native.vlqt_res_wait(stripes, 0) == Protocol.ResState.Ready && sw.ElapsedMilliseconds < 20000)
        {
            s.Pump(1);
            using (var a = s.Grab(w2, Path.Combine(dir, "replace.png")))
            {
                var c = P1(a, 34, 15);
                if (ImageTest.IsBg(c)) emptyFrames++;
                if (c.R > 200 && c.G > 200 && c.B > 200) break; // new content drawn
            }
            if (sw.ElapsedMilliseconds > 15000) break;
        }
        using (var a = s.Grab(w2, Path.Combine(dir, "replace.png")))
        {
            var c = P1(a, 34, 15);
            s.Check(emptyFrames == 0 && c.R > 200 && c.G > 200 && c.B > 200, $"replacement drawn without an empty frame ({emptyFrames} empty, {c})");
        }

        // 4. Destroy an element while its resource loads, release the resource: no crash, nothing left.
        Native.vlqt_res_create(Protocol.ResKind.Image, out var loading);
        Native.vlqt_res_load_image(loading, p4k, 0);
        cmd.Reset();
        cmd.SetU64(2, Protocol.Prop.Image, loading);
        Native.Apply(w1, cmd.EndFrame());
        cmd.Reset();
        cmd.Destroy(2);
        s.Check(Native.Apply(w1, cmd.EndFrame()) == 0, "destroy an element whose resource loads");
        Native.vlqt_res_release(loading);
        for (int i = 0; i < 200 && Info(loading).id != 0; i++)
        {
            s.Pump(1);
            Thread.Sleep(5);
        }
        s.Check(Info(loading).id == 0, "resource freed after the element and host references are gone");

        // 5. Mipmaps: the 4K image into a 40x30 element, smooth.
        var mips = ImageTest.Load(p4k, s.Check, "4K with mipmaps", Protocol.Img.Mipmaps);
        cmd.Reset();
        cmd.SetU64(3, Protocol.Prop.Image, mips);
        cmd.SetBool(3, Protocol.Prop.Smooth, true);
        s.Check(Native.Apply(w1, cmd.EndFrame()) == 0, "mipmapped resource set");
        s.Pump(2);
        using (var a = s.Grab(w1, Path.Combine(dir, "mipmaps.png")))
            s.Check(!ImageTest.IsBg(P1(a, 85, 15)), $"mipmapped resource draws ({P1(a, 85, 15)})");

        // 6. Perf: 50 preloaded 4K resources, one switched per frame (60 Hz pacing like ImageResourceTest).
        if (PerfLimits.SkipHeavy)
            s.Skip("perf run 50 x 4K switch per frame: heavy perf measurement skipped on shared CI runners");
        else
            RunPerf();

        Native.vlqt_window_destroy(w1);
        Native.vlqt_window_destroy(w2);
        foreach (var id in new[] { stripes, big, mips }) Native.vlqt_res_release(id);
        s.Pump(2);
        var left = new Native.vlqt_res_info[16];
        s.Check(Native.ResList(left) == 0, "no resources left (windows destroyed, host references released)");

        void RunPerf()
        {
            var ids = Enumerable.Range(0, 50).Select(_ => { Native.vlqt_res_create(Protocol.ResKind.Image, out var id); Native.vlqt_res_load_image(id, p4k, 0); return id; }).ToList();
            var ready = ids.All(id => Native.vlqt_res_wait(id, 60000) == Protocol.ResState.Ready);
            s.Check(ready, "x 4K preloaded");
            const double period = 1000.0 / 60;
            var clock = Stopwatch.StartNew();
            double next = clock.Elapsed.TotalMilliseconds, last = next;
            var intervals = new List<double>();
            var setMs = new List<double>();
            void Frame(int i, bool switching)
            {
                next += period;
                if (clock.Elapsed.TotalMilliseconds > next) next = clock.Elapsed.TotalMilliseconds;
                while (clock.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50);
                cmd.Reset();
                cmd.SetPlace(4, i % 360, 30, 39, 29); // something moves in both runs
                if (switching)
                    cmd.SetU64(1, Protocol.Prop.Image, ids[i % ids.Count]);
                var t = Stopwatch.GetTimestamp();
                Native.Apply(w1, cmd.EndFrame());
                if (switching)
                    setMs.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                s.Pump(1);
                var now = clock.Elapsed.TotalMilliseconds;
                intervals.Add(now - last);
                last = now;
            }
            for (int i = 0; i < 240; i++) Frame(i, false);
            var background = intervals.Skip(1).ToList();
            intervals.Clear();
            for (int i = 0; i < 240; i++) Frame(i, true);
            var switching = intervals.Skip(1).ToList();
            const double limit = period * 1.5;
            double Rate(List<double> l) => l.Count == 0 ? 0 : 100.0 * l.Count(x => x > limit) / l.Count;
            var setMedian = setMs.OrderBy(x => x).ElementAt(setMs.Count / 2);
            s.Info($"perf 50x4K switch per frame: apply with SET median {setMedian * 1000:F0} us, max {setMs.Max():F2} ms; "
                + $"hitches background {Rate(background):F1} %, switching {Rate(switching):F1} %");
            s.Perf("image-share.apply_set_image_median", setMedian);
            PerfLimits.Below(s, setMedian, 0.1, "apply with SET image (switch between preloaded 4K), median ms");
            PerfLimits.Below(s, Rate(switching), Math.Max(Rate(background) * 3, 5), "hitch rate while switching 50 x 4K per frame in the range of the background, %");
            cmd.Reset();
            cmd.SetU64(1, Protocol.Prop.Image, big);
            Native.Apply(w1, cmd.EndFrame());
            foreach (var id in ids) Native.vlqt_res_release(id);
            s.Pump(2);
        }
    }
}
