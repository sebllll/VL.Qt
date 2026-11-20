using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;

/// <summary>
/// Wave 1, task W1-17 (issue #171): two measurements that decide later work. Both are explicit (a measurement, not a gate)
/// and warn-only: they never fail on a number.
/// C16 / NODE-02: does <c>Clip</c> ever change a pixel on Rectangle and Circle? Rectangle and Circle with fill, stroke and a
/// DropShadow, Clip off against Clip on, grabbed and compared pixel by pixel.
/// E8 / NRES-02: main-thread cost of replacing the pixels of an image resource every frame (what a video source does):
/// <c>vlqt_res_set_pixels</c> + pump + resource poll per frame, against the same loop with an unchanged image. The delta is the
/// cost of the per-frame upload. 1080p and 4K, BGRA (the format of VL.Video), paced at 60 Hz like the vvvv frame clock.
/// Parameters (run settings or VLQT_SMOKE_&lt;NAME&gt;): <c>VideoUploadRuns</c> (default 5), <c>VideoUploadFrames</c> (default 240).
/// </summary>
[TestFixture, NonParallelizable]
public static class ClipVideoPerfTest
{
    /// <summary>C16: Clip off against Clip on for Rectangle and Circle, with and without DropShadow.</summary>
    [Test, Explicit("measurement W1-17"), Category("clip-grab")]
    public static void ClipGrab() => Scenario.Run(RunClip);

    /// <summary>E8: main-thread cost of a new frame per image resource (video), 1080p and 4K.</summary>
    [Test, Explicit("measurement W1-17"), Category("video-upload-perf"), Category("Perf")]
    public static void VideoUploadPerf() => Scenario.Run(RunVideo);

    // ---- C16 ----

    static void RunClip(Scenario s)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt clip grab");
        s.Check(w != IntPtr.Zero, "clip-grab: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var rect = new RectangleNode(_ => Disposable());
        var circle = new CircleNode(_ => Disposable());
        var shadow = new DropShadowNode();

        // Units are 1/100 DIP (DIPTopLeft): a 200 x 150 DIP rectangle and a circle of radius 100 DIP at (300, 200) and (300, 200).
        Effect? Shadow(bool on) => on ? shadow.Update(null, new Color4(0, 0, 0, 1), new Vector2(0.15f, 0.15f), 1, 1, true, default) : null;
        Element Rect(bool clip, bool stroke, bool fx)
        {
            var origin = Matrix.Translation(3, 2, 0);
            return rect.Update(new Vector2(2, 1.5f), origin, RectangleAnchor.TopLeft, new Color4(1, 0.6f, 0, 1), 0.3f,
                new Color4(0, 0, 1, 1), stroke ? 0.08f : 0, 1, clip, false, null, Shadow(fx), out _, out _);
        }
        Element Circle(bool clip, bool stroke, bool fx)
        {
            var origin = Matrix.Translation(3, 2, 0);
            return circle.Update(0.9f, origin, RectangleAnchor.TopLeft, new Color4(1, 0.6f, 0, 1),
                new Color4(0, 0, 1, 1), stroke ? 0.08f : 0, 1, clip, false, null, Shadow(fx), out _, out _);
        }

        Bitmap Frame(Element root, string name)
        {
            cmd.Reset();
            cmd.ClearColor(0.9f, 0.9f, 0.9f, 1);
            rec.Update(root, Space.DIPTopLeft);
            if (cmd.Length > 0)
                s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"clip-grab: apply {name} {Native.LastError()}");
            s.Pump(6);
            return s.Grab(w, name + ".png");
        }

        int Diff(Bitmap a, Bitmap b)
        {
            int n = 0;
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                {
                    Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                    if (Math.Abs(p.R - q.R) > 2 || Math.Abs(p.G - q.G) > 2 || Math.Abs(p.B - q.B) > 2) n++;
                }
            return n;
        }

        foreach (var (kind, make) in new (string, Func<bool, bool, bool, Element>)[] { ("rectangle", Rect), ("circle", Circle) })
            foreach (var (stroke, fx) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var label = $"{kind}{(stroke ? " stroke" : "")}{(fx ? " dropshadow" : "")}";
                var file = $"clip_{kind}{(stroke ? "_stroke" : "")}{(fx ? "_shadow" : "")}";
                using var off = Frame(make(false, stroke, fx), file + "_off");
                using var on = Frame(make(true, stroke, fx), file + "_on");
                var diff = Diff(off, on);
                s.Info($"clip-grab: {label}: Clip off vs on differs in {diff} of {off.Width * off.Height} pixels");
                s.Perf($"clip-grab {label} differing pixels", diff, "px");
            }

        // Positive control: the same grab and Clip setting must see a clip where content reaches outside the bounds. A parent
        // Rectangle 150 x 100 DIP with a child 150 x 100 DIP shifted by (100, 50); Clip off, then on, on the native element itself.
        void Control(bool clip)
        {
            cmd.Reset();
            if (!clip)
            {
                cmd.Create(900001, Protocol.Type.Rectangle);
                cmd.SetPlace(900001, 20, 20, 150, 100);
                cmd.SetColor(900001, Protocol.Prop.Color, 1, 0.6f, 0, 1);
                cmd.Reparent(900001, 0, 0);
                cmd.Create(900002, Protocol.Type.Rectangle);
                cmd.SetPlace(900002, 100, 50, 150, 100);
                cmd.SetColor(900002, Protocol.Prop.Color, 0, 0, 1, 1);
                cmd.Reparent(900002, 900001, 0);
            }
            else
                cmd.SetBool(900001, Protocol.Prop.Clip, true);
            s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"clip-grab: apply control clip={clip} {Native.LastError()}");
            s.Pump(6);
        }
        Control(false);
        using (var controlOff = s.Grab(w, "clip_control_off.png"))
        {
            Control(true);
            using var controlOn = s.Grab(w, "clip_control_on.png");
            var diff = Diff(controlOff, controlOn);
            s.Info($"clip-grab: control (child outside the parent): Clip off vs on differs in {diff} of {controlOff.Width * controlOff.Height} pixels (expected > 0)");
            s.Perf("clip-grab control differing pixels", diff, "px");
            s.Check(diff > 0, "clip-grab: positive control, Clip on cuts a child that reaches outside");
        }
        Native.vlqt_window_destroy(w);
        s.Pump(2);
    }

    static IDisposable Disposable() => new Nop();

    sealed class Nop : IDisposable
    {
        public void Dispose() { }
    }

    // ---- E8 ----

    static void RunVideo(Scenario s)
    {
        var runs = Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("VLQT_SMOKE_VIDEOUPLOADRUNS"), out var r) ? r : 5);
        var frames = Math.Max(60, int.TryParse(Environment.GetEnvironmentVariable("VLQT_SMOKE_VIDEOUPLOADFRAMES"), out var f) ? f : 240);
        var gpu = Native.vlqt_test_device_info(IntPtr.Zero, out _, out var uploadRhi) == 0 && uploadRhi != IntPtr.Zero;
        s.Info($"video-upload-perf: {(gpu ? "GPU upload (shared upload QRhi)" : "CPU copy, no upload QRhi")}, {runs} runs of {frames} frames, paced 60 Hz, "
            + $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, VLQT_IMG_UPLOAD={Environment.GetEnvironmentVariable("VLQT_IMG_UPLOAD") ?? "(auto)"}");

        using var timer = PumpTest.HighResTimer(); // 1 ms timer resolution like vvvv
        var w = Native.vlqt_window_create_at(60, 60, 960, 540, "VL.Qt video upload", 0);
        s.Check(w != IntPtr.Zero, "video-upload-perf: window " + (w == IntPtr.Zero ? Native.LastError() : ""));
        var cmd = new CommandWriter();
        cmd.ClearColor(0.1f, 0.1f, 0.1f, 1);
        Native.vlqt_window_show(w);
        var buf = new byte[64 * 1024];

        // Size, premultiplied: VL.Video frames are straight BGRA (VideoSourceToQtImageNode passes premultiplied false).
        var created = false;
        var cases = new[] { (1920, 1080, false), (1920, 1080, true), (3840, 2160, false) };
        foreach (var (width, height, premultiplied) in cases)
        {
            var data = new byte[width * height * 4];
            new Random(1).NextBytes(data);
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
            var desc = new Native.vlqt_pixel_desc { width = width, height = height, format = 9, premultiplied = premultiplied ? 1 : 0 };
            var name = $"{width}x{height}{(premultiplied ? " premultiplied" : "")}";

            Native.vlqt_res_create(Protocol.ResKind.Image, out var res);
            Native.ResSetPixels(res, desc, data);
            cmd.Reset();
            if (!created)
            {
                cmd.Create(1, Protocol.Type.Image);
                cmd.SetPlace(1, 0, 0, 960, 540);
                cmd.Reparent(1, 0, 0);
                created = true;
            }
            cmd.SetU64(1, Protocol.Prop.Image, res);
            s.Check(Native.Apply(w, cmd.EndFrame()) == 0, $"video-upload-perf: apply {name}");
            for (int i = 0; i < 30; i++) { s.Pump(1); Native.ResPoll(buf); }

            var staticRuns = new List<Result>();
            var videoRuns = new List<Result>();
            for (int run = 0; run < runs; run++)
            {
                staticRuns.Add(Measure(s, res, desc, data, buf, frames, newFrame: false));
                videoRuns.Add(Measure(s, res, desc, data, buf, frames, newFrame: true));
            }

            double Med(Func<Result, double> pick, List<Result> l) => Median(l.Select(pick).ToList());
            string Range(Func<Result, double> pick, List<Result> l) => $"{l.Min(pick):F2}..{l.Max(pick):F2}";
            var delta = videoRuns.Zip(staticRuns, (v, st) => v.TotalMedian - st.TotalMedian).ToList();
            s.Info($"video-upload-perf {name}: main thread per frame (median of {runs} runs of per-frame medians; range): "
                + $"static {Med(x => x.TotalMedian, staticRuns):F2} ms ({Range(x => x.TotalMedian, staticRuns)}), "
                + $"new frame {Med(x => x.TotalMedian, videoRuns):F2} ms ({Range(x => x.TotalMedian, videoRuns)}) "
                + $"[set_pixels {Med(x => x.SetMedian, videoRuns):F2}, pump {Med(x => x.PumpMedian, videoRuns):F2}, poll {Med(x => x.PollMedian, videoRuns):F2}], "
                + $"delta {Median(delta):F2} ms ({delta.Min():F2}..{delta.Max():F2}); p95 of the new-frame total {Med(x => x.TotalP95, videoRuns):F2} ms, "
                + $"max {videoRuns.Max(x => x.TotalMax):F1} ms; uploads (Info events) {Med(x => x.Infos, videoRuns):F0} of {frames} frames");
            s.Perf($"video-upload-perf {name} delta main thread per frame", Median(delta));
            s.Perf($"video-upload-perf {name} new-frame main thread per frame", Med(x => x.TotalMedian, videoRuns));
            s.Perf($"video-upload-perf {name} set_pixels", Med(x => x.SetMedian, videoRuns));

            Native.vlqt_res_release(res);
            for (int i = 0; i < 5; i++) { s.Pump(1); Native.ResPoll(buf); }
            Thread.Sleep(200);
        }
        Native.vlqt_window_destroy(w);
        s.Pump(2);
        // Let the process settle before it ends: a Video/Image resource still uploading when the process exits can leave an unkillable testhost.
        Thread.Sleep(2000);
    }

    readonly record struct Result(double SetMedian, double PumpMedian, double PollMedian, double TotalMedian, double TotalP95, double TotalMax, double Infos);

    static Result Measure(Scenario s, ulong res, Native.vlqt_pixel_desc desc, byte[] data, byte[] buf, int frames, bool newFrame)
    {
        const double period = 1000.0 / 60;
        var set = new List<double>(frames);
        var pump = new List<double>(frames);
        var poll = new List<double>(frames);
        var total = new List<double>(frames);
        int infos = 0;
        var clock = Stopwatch.StartNew();
        double next = clock.Elapsed.TotalMilliseconds;
        for (int i = 0; i < frames; i++)
        {
            next += period;
            if (clock.Elapsed.TotalMilliseconds > next) next = clock.Elapsed.TotalMilliseconds; // behind: no catch-up burst
            while (clock.Elapsed.TotalMilliseconds < next) Thread.SpinWait(50);

            var t0 = Stopwatch.GetTimestamp();
            if (newFrame)
            {
                data[0] = (byte)i;
                Native.ResSetPixels(res, desc, data);
            }
            var t1 = Stopwatch.GetTimestamp();
            s.Pump(1);
            var t2 = Stopwatch.GetTimestamp();
            var n = Native.ResPoll(buf);
            if (n < 0)
            {
                // 64 KB hold far more events than one frame produces; a bigger need is dropped, not counted.
                n = 0;
            }
            for (int at = 0; at < n;)
            {
                var ev = BitConverter.ToUInt16(buf, at);
                var len = BitConverter.ToUInt16(buf, at + 2);
                if (ev == Protocol.ResEv.Info) infos++;
                at += (4 + len + 3) & ~3;
            }
            var t3 = Stopwatch.GetTimestamp();
            if (i < 20) continue; // warm-up of the run
            set.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            pump.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            poll.Add(Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds);
            total.Add(Stopwatch.GetElapsedTime(t0, t3).TotalMilliseconds);
        }
        var sorted = total.OrderBy(x => x).ToList();
        return new Result(Median(set), Median(pump), Median(poll), Median(total), sorted[(int)(sorted.Count * 0.95)], sorted[^1], infos);
    }

    static double Median(List<double> l)
    {
        var o = l.OrderBy(x => x).ToList();
        return o.Count == 0 ? 0 : o.Count % 2 == 1 ? o[o.Count / 2] : (o[o.Count / 2 - 1] + o[o.Count / 2]) / 2;
    }
}
