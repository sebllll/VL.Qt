using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Color = System.Drawing.Color;
using RectangleF = Stride.Core.Mathematics.RectangleF;
using VPath = VL.Lib.IO.Path;
using static Pixels;

// Video element: QtMultimedia MediaPlayer + VideoOutput (FFmpeg backend) in QtNative, EV_MEDIA back channel,
// Video node through the reconciler. The player runs on its own (wall) clock, so this test pumps frames in real time.
// Assets (self-made with ffmpeg testsrc/testsrc2, H.264 baseline, no audio): assets/testsrc_160x90_2s.mp4,
// assets/testsrc_96x96_1s.mp4. Grabs go to <OutDir>/115_video*.png. Values are DIP.
[TestFixture, NonParallelizable]
public static class VideoTest
{
    private const string AssetA = "testsrc_160x90_2s.mp4", AssetB = "testsrc_96x96_1s.mp4";
    private const uint VideoId = 1, ImageId = 2;
    private const uint Handle = 21;

    private static string? FindAsset(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "src", "VL.Qt.SmokeTest", "assets", name);
            if (File.Exists(p))
                return p;
        }
        return null;
    }


    /// <summary>Number of differing pixels in the DIP region (8 px grid).</summary>
    private static int Diff(Bitmap a, Bitmap b, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y += 8)
            for (int x = x0; x < x1; x += 8)
            {
                Color p = Px(a, x, y), q = Px(b, x, y);
                if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 30) n++;
            }
        return n;
    }

    /// <summary>Video element.</summary>
    [Test, Category("video")]
    public static void Video() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        void gpu(bool ok, string what) => s.NeedsRhi(ok, what, "VideoOutput draws nothing");
        string? a = FindAsset(AssetA), b = FindAsset(AssetB);
        s.Check(a != null && b != null, "test videos found");
        if (a == null || b == null)
            return;

        var w = Native.vlqt_window_create(800, 600, "VL.Qt video test");
        s.Check(w != IntPtr.Zero, "window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var buf = new byte[1 << 16];
        float pos = 0, dur = 0, mw = 0, mh = 0;
        int mediaRecords = 0;
        float firstPos = -1, firstDur = -1; // first EV_MEDIA record of Handle
        var error = ""; // last EV_ERROR text of Handle

        int Apply()
        {
            var rc = Native.Apply(w, cmd.EndFrame());
            cmd.Reset();
            return rc;
        }
        void CheckApply(string what)
        {
            var rc = Apply();
            s.Check(rc == 0, $"{what} ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        }
        // One real-time frame: pump + poll (EV_MEDIA / EV_MEASURED of Handle).
        void Frame()
        {
            s.Pump(1);
            int n = Native.Poll(w, buf);
            if (MeasureTest.ErrorText(buf.AsSpan(0, Math.Max(n, 0)), Handle) is { } e) error = e;
            foreach (var r in MeasureTest.Parse(buf.AsSpan(0, Math.Max(n, 0)), s.Check))
            {
                if (r.Handle != Handle) continue;
                if (r.Event == Protocol.Ev.Media)
                {
                    pos = r.V[0];
                    dur = r.V[1];
                    if (mediaRecords++ == 0) { firstPos = pos; firstDur = dur; }
                }
                if (r.Event == Protocol.Ev.Measured) { mw = r.V[0]; mh = r.V[1]; }
            }
            Thread.Sleep(15);
        }
        bool WaitFor(Func<bool> cond, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Frame();
                if (cond()) return true;
            }
            return false;
        }
        void Run(int ms) => WaitFor(() => false, ms);

        // --- 1) Load + play: duration after the asynchronous load, natural size, pixels change, position rises.
        cmd.Reset();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(VideoId, Protocol.Type.Video);
        cmd.SetPlace(VideoId, 20, 20, 320, 180);
        cmd.SetI32(VideoId, Protocol.Prop.FillMode, 0);
        cmd.SetString(VideoId, Protocol.Prop.Source, a);
        cmd.SetBool(VideoId, Protocol.Prop.Loop, true);
        cmd.SetBool(VideoId, Protocol.Prop.Playing, true);
        cmd.Reparent(VideoId, 0, 0);
        cmd.Bind(VideoId, Handle, 0);
        var loadWatch = Stopwatch.StartNew();
        CheckApply("create video, source, loop, playing");
        Frame();
        // QtNative samples the media state per frame: a fast load (CI runner) is done before the first sample, then the
        // first record is already the loaded state. Either way no undefined values.
        s.Check(mediaRecords > 0 && (firstDur == 0 ? firstPos == 0 : Math.Abs(firstDur - 2f) < 0.1f && firstPos >= 0 && firstPos < 0.2f),
            $"defined default before loading: position 0, duration 0 or already loaded ({firstPos}, {firstDur})");
        bool loaded = WaitFor(() => dur > 0, 5000);
        s.Info($"video load until duration > 0: {loadWatch.ElapsedMilliseconds} ms");
        s.Check(loaded && Math.Abs(dur - 2f) < 0.1f, $"duration after loading ~2 s ({dur})");
        WaitFor(() => mw > 0, 2000);
        s.Check(mw == 160 && mh == 90, $"measured size = natural video size 160x90 ({mw}x{mh})");

        Run(300);
        var p0 = pos;
        using (var g0 = s.Grab(w, "115_video_t0.png"))
        {
            Run(400);
            using var g1 = s.Grab(w, "115_video_t1.png");
            gpu(!IsBg(Px(g0, 180, 110)) && IsBg(Px(g0, 360, 110)) && IsBg(Px(g0, 180, 210)),
                $"video drawn inside Bounds only ({Px(g0, 180, 110)}, {Px(g0, 360, 110)})");
            int diff = Diff(g0, g1, 24, 24, 336, 196);
            gpu(diff > 20, $"pixels change over time while playing ({diff} cells differ)");
        }
        s.Check(pos > p0 + 0.2f, $"position rises while playing ({p0:F3} -> {pos:F3})");

        // --- 2) Loop: position wraps around the end and playback continues.
        bool wrapped = false;
        float last = pos;
        WaitFor(() => { if (pos < last - 0.5f) wrapped = true; last = pos; return wrapped; }, 3500);
        s.Check(wrapped, $"loop wraps the position back to the start (now {pos:F3})");

        // --- 3) Rate 2: position advances about twice as fast (positive deltas, wraps skipped).
        float Advance(int ms)
        {
            float sum = 0, prev = pos;
            var sw = Stopwatch.StartNew();
            WaitFor(() => { if (pos > prev) sum += pos - prev; prev = pos; return false; }, ms);
            return (float)(sum / sw.Elapsed.TotalSeconds);
        }
        float speed1 = Advance(600);
        cmd.SetF32(VideoId, Protocol.Prop.PlaybackRate, 2);
        CheckApply("rate 2");
        Run(100);
        float speed2 = Advance(600);
        s.Info($"playback speed: rate 1 {speed1:F2} s/s, rate 2 {speed2:F2} s/s");
        s.Check(speed1 > 0.6f && speed2 > 1.4f * speed1, $"rate 2 plays faster ({speed1:F2} -> {speed2:F2} s/s)");
        cmd.SetF32(VideoId, Protocol.Prop.PlaybackRate, 1);
        CheckApply("rate 1");

        // --- 4) Pause: position and pixels stay; play resumes.
        cmd.SetBool(VideoId, Protocol.Prop.Playing, false);
        CheckApply("playing false");
        Run(200);
        var paused = pos;
        using (var g0 = s.Grab(w, "115_video_paused0.png"))
        {
            Run(400);
            using var g1 = s.Grab(w, "115_video_paused1.png");
            s.Check(pos == paused && Diff(g0, g1, 24, 24, 336, 196) == 0, $"paused: position and pixels stay ({paused:F3} -> {pos:F3})");
            gpu(!IsBg(Px(g1, 180, 110)), "paused video keeps its frame");
        }
        cmd.SetBool(VideoId, Protocol.Prop.Playing, true);
        CheckApply("playing true");
        s.Check(WaitFor(() => pos > paused + 0.1f || pos < paused - 0.5f, 1500), $"play resumes ({paused:F3} -> {pos:F3})");

        // --- 5) Loop off during playback: the running pass plays to its end, then playback stops (no further
        // pre-buffered passes). Switched off mid-pass; stopped = position at the end and unchanged for 500 ms.
        int wraps = 0;
        long StopAfter(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            float prev = pos;
            long stillSince = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Frame();
                if (pos < prev - 0.5f) wraps++;
                if (pos != prev) { prev = pos; stillSince = sw.ElapsedMilliseconds; }
                else if (pos > dur - 0.2f && sw.ElapsedMilliseconds - stillSince > 500) return stillSince;
            }
            return -1;
        }
        s.Check(WaitFor(() => pos > 0.8f && pos < 1.2f, 3000), $"mid-pass before loop off ({pos:F3})");
        float remaining = dur - pos;
        cmd.SetBool(VideoId, Protocol.Prop.Loop, false);
        CheckApply("loop false");
        long stopMs = StopAfter(2 * 2000 + 2500);
        s.Info($"loop switched off during playback ({remaining:F2} s left): stopped after {stopMs} ms, {wraps} wrap(s)");
        s.Check(stopMs >= 0 && wraps == 0 && stopMs < remaining * 1000 + 700 && Math.Abs(pos - dur) < 0.2f,
            $"loop off during playback: the running pass ends, then stop ({pos:F3}, duration {dur:F3}, {stopMs} ms, {wraps} wraps)");
        // Play again after the end: restarts at 0 and stops after exactly one pass (loop was off from the start).
        cmd.SetBool(VideoId, Protocol.Prop.Playing, false);
        CheckApply("playing false at the end");
        Frame();
        cmd.SetBool(VideoId, Protocol.Prop.Playing, true);
        CheckApply("playing true at the end");
        s.Check(WaitFor(() => pos < 1f, 1000), $"play after the end restarts at 0 ({pos:F3})");
        stopMs = StopAfter(4500);
        s.Info($"without loop from the start: stopped after {stopMs} ms");
        s.Check(stopMs >= 1000 && stopMs < 2800 && Math.Abs(pos - dur) < 0.2f, $"without loop playback stops after one pass ({pos:F3}, {stopMs} ms)");

        // --- 5b) End of stream: Qt default ClearOutput empties the element, KeepLastFrame keeps the last frame.
        Frame();
        using (var gc = s.Grab(w, "115_video_end_clear.png"))
            gpu(IsBg(Px(gc, 180, 110)), $"keepLastFrame off (Qt default): output cleared at the end ({Px(gc, 180, 110)})");
        cmd.SetBool(VideoId, Protocol.Prop.KeepLastFrame, true);
        cmd.SetBool(VideoId, Protocol.Prop.Playing, false);
        CheckApply("keepLastFrame true");
        Frame();
        cmd.SetBool(VideoId, Protocol.Prop.Playing, true);
        CheckApply("playing true with keepLastFrame");
        s.Check(WaitFor(() => pos < 1f, 1000), $"play after the end restarts at 0 ({pos:F3})");
        stopMs = StopAfter(4500);
        s.Check(stopMs >= 0 && Math.Abs(pos - dur) < 0.2f, $"keepLastFrame: playback stops at the end ({pos:F3}, {stopMs} ms)");
        Frame();
        using (var gk = s.Grab(w, "115_video_end_keep.png"))
            gpu(!IsBg(Px(gk, 180, 110)), $"keepLastFrame on: last frame stays at the end ({Px(gk, 180, 110)})");

        // --- 6) Volume and errors.
        cmd.SetF32(VideoId, Protocol.Prop.Volume, 0.5f);
        CheckApply("volume 0.5");
        void Expect(int code, string what, Action write)
        {
            write();
            var rc = Apply();
            s.Check(rc == code, $"{what} -> {code} ({rc}: {Native.LastError()})");
        }
        Expect(-7, "volume 2", () => cmd.SetF32(VideoId, Protocol.Prop.Volume, 2));
        Expect(-7, "rate 0", () => cmd.SetF32(VideoId, Protocol.Prop.PlaybackRate, 0));
        Expect(-7, "fillMode 3 (no Tile)", () => cmd.SetI32(VideoId, Protocol.Prop.FillMode, 3));
        Expect(-7, "playing as i32", () => cmd.SetI32(VideoId, Protocol.Prop.Playing, 1));
        Expect(-6, "radius on video", () => cmd.SetF32(VideoId, Protocol.Prop.Radius, 1));
        cmd.Create(ImageId, Protocol.Type.Image);
        cmd.Reparent(ImageId, 0, 1);
        CheckApply("image for -6");
        Expect(-6, "playing on image", () => cmd.SetBool(ImageId, Protocol.Prop.Playing, true));
        cmd.Destroy(ImageId);
        CheckApply("destroy image");

        // --- 7) File change: duration and size of the new file, playback continues with Playing.
        cmd.SetString(VideoId, Protocol.Prop.Source, b);
        cmd.SetBool(VideoId, Protocol.Prop.Loop, true);
        CheckApply("source B");
        s.Check(WaitFor(() => Math.Abs(dur - 1f) < 0.1f, 5000), $"file change: duration of B ~1 s ({dur})");
        s.Check(WaitFor(() => mw == 96 && mh == 96, 2000), $"file change: measured size 96x96 ({mw}x{mh})");
        var pb = pos;
        Run(300);
        s.Check(pos != pb, $"file change: new file plays ({pb:F3} -> {pos:F3})");

        // --- 8) Missing file: EV_ERROR with the path (shown at the Video node), duration back to 0, no crash;
        // a valid file loads again and clears the error.
        s.Check(error == "", $"no error for valid files ({error})");
        var missing = Path.Combine(s.OutDir, "missing_vlqt_115.mp4");
        cmd.SetString(VideoId, Protocol.Prop.Source, missing);
        CheckApply("missing source");
        WaitFor(() => error.Contains("missing_vlqt_115"), 3000);
        s.Check(error.Contains("missing_vlqt_115"), $"missing file -> EV_ERROR with the path ({error})");
        s.Check(!error.Contains("file:"), $"error shows a local path, not a URL ({error})");
        s.Check(dur == 0, $"missing file: duration 0 ({dur})");
        cmd.SetString(VideoId, Protocol.Prop.Source, a);
        CheckApply("source A again");
        s.Check(WaitFor(() => dur > 1.9f, 5000), $"valid file after a missing one loads ({dur})");
        s.Check(error == "", $"source change clears the error ({error})");
        cmd.SetString(VideoId, Protocol.Prop.Source, "");
        CheckApply("empty source");
        s.Check(WaitFor(() => dur == 0, 2000), $"empty source: duration 0 ({dur})");
        Run(200);
        using (var ge = s.Grab(w, "115_video_empty.png"))
            s.Check(IsBg(Px(ge, 180, 110)), $"empty source clears the output despite keepLastFrame ({Px(ge, 180, 110)})");
        cmd.Destroy(VideoId);
        CheckApply("destroy video");

        // --- 9) Leaks: create + play + file change + destroy, many cycles. Handles/threads/private bytes must not grow.
        void Cycle(uint id)
        {
            dur = 0;
            cmd.Create(id, Protocol.Type.Video);
            cmd.SetPlace(id, 400, 20, 160, 90);
            cmd.SetString(id, Protocol.Prop.Source, a);
            cmd.SetBool(id, Protocol.Prop.Playing, true);
            cmd.Reparent(id, 0, 0);
            cmd.Bind(id, Handle, 0);
            Apply();
            WaitFor(() => dur > 0, 3000);
            Run(100);
            cmd.SetString(id, Protocol.Prop.Source, b);
            Apply();
            WaitFor(() => Math.Abs(dur - 1f) < 0.1f, 3000);
            Run(100);
            cmd.Destroy(id);
            Apply();
            Run(50);
        }
        static (int handles, int threads, long priv) Usage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var p = Process.GetCurrentProcess();
            return (p.HandleCount, p.Threads.Count, p.PrivateMemorySize64);
        }
        for (uint i = 0; i < 3; i++) Cycle(100 + i); // warm-up: plugins, decoders, caches
        Run(300);
        var u0 = Usage();
        const int Cycles = 12;
        for (uint i = 0; i < Cycles; i++) Cycle(200 + i);
        Run(500);
        var u1 = Usage();
        s.Info($"leak check over {Cycles} cycles (create, play, file change, destroy): handles {u0.handles} -> {u1.handles}, " +
            $"threads {u0.threads} -> {u1.threads}, private {u0.priv / 1048576.0:F1} -> {u1.priv / 1048576.0:F1} MB");
        s.Check(u1.handles - u0.handles < 40, $"no handle leak ({u0.handles} -> {u1.handles})");
        s.Check(u1.threads - u0.threads <= 3, $"no thread leak ({u0.threads} -> {u1.threads})");
        s.Check(u1.priv - u0.priv < 40L << 20, $"no memory leak ({(u1.priv - u0.priv) / 1048576.0:F1} MB)");

        Native.vlqt_window_destroy(w);
        s.Pump(2);

        RunNode(s, a, b);
    }

    private static IDisposable NoWarning(string _) => throw new InvalidOperationException("unexpected node warning");

    /// <summary>Video node → Reconciler → QtNative → poll → SinkRegistry → node outputs Position/Duration.</summary>
    private static void RunNode(Scenario s, string a, string b)
    {
        var w = Native.vlqt_window_create(800, 600, "VL.Qt video node test");
        s.Check(w != IntPtr.Zero, "node: window_create");
        Native.vlqt_window_show(w);
        var cmd = new CommandWriter();
        var rec = new Reconciler(cmd, new IdAllocator(), new SceneRegistry());
        var node = new VideoNode(NoWarning);
        var buf = new byte[1 << 16];
        float pos = 0, dur = 0;
        Vector2 size = default;

        void Frame(string path, bool play)
        {
            var e = node.Update(new Vector2(3.2f, 1.8f), Matrix.Translation(0.2f, 0.2f, 0), RectangleAnchor.TopLeft, new VPath(path), play, true, true, 1, 1,
                VideoFillMode.Fit, 1, false, false, null, null, out pos, out dur, out size, out _);
            cmd.Reset();
            rec.Update(e, Space.DIPTopLeft);
            if (cmd.Length > 0)
            {
                var rc = Native.Apply(w, cmd.EndFrame());
                s.Check(rc == 0, $"node: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            }
            s.Pump(1);
            int n = Native.Poll(w, buf);
            rec.Sinks.Dispatch(buf.AsSpan(0, Math.Max(n, 0)));
            Thread.Sleep(15);
        }
        bool WaitFor(string path, Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { Frame(path, true); if (cond()) return true; }
            return false;
        }

        Frame(a, true);
        s.Check(pos == 0 && dur == 0, $"node: outputs 0 before loading ({pos}, {dur})");
        s.Check(WaitFor(a, () => dur > 1.9f && pos > 0.3f, 5000), $"node: Duration ~2 s and Position rising arrive at the node ({pos:F3}, {dur:F3})");
        s.Check(Math.Abs(size.X - 1.6f) < 1e-4f && Math.Abs(size.Y - 0.9f) < 1e-4f, $"node: Measured Size = natural size in units ({size})");
        using (var g = s.Grab(w, "115_video_node.png"))
            s.Check(!IsBg(Px(g, 180, 110)), $"node: video visible ({Px(g, 180, 110)})");
        s.Check(WaitFor(b, () => Math.Abs(dur - 1f) < 0.1f, 5000), $"node: file change via Path pin ({dur:F3})");

        node.Dispose();
        cmd.Reset();
        rec.Clear();
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_destroy(w);
        s.Pump(2);
    }
}
