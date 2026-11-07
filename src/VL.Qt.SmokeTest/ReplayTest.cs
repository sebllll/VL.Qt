using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using VL.Qt;

// Replays command traces recorded with VLQT_TRACE_COMMANDS (plan 01e): file = repeated "u32 length (LE) + frame bytes".
// The trace has no window id, so all frames are applied to one 800x600 window (traces of one window replay exactly).
static class ReplayTest
{
    const float U = 100f;

    /// <summary>Reads a length-prefixed trace file. Throws <see cref="InvalidDataException"/> on a truncated or oversized frame.</summary>
    public static List<byte[]> ReadFrames(string path)
    {
        var frames = new List<byte[]>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[4];
        while (true)
        {
            var n = fs.Read(head, 0, 4);
            if (n == 0) break;
            if (n < 4) throw new InvalidDataException($"trace truncated in length prefix of frame {frames.Count}");
            var len = BitConverter.ToUInt32(head, 0);
            if (len > fs.Length - fs.Position)
                throw new InvalidDataException($"frame {frames.Count}: length {len} exceeds remaining {fs.Length - fs.Position} bytes");
            var buf = new byte[len];
            fs.ReadExactly(buf);
            frames.Add(buf);
        }
        return frames;
    }

    public static void WriteFrames(string path, IEnumerable<byte[]> frames)
    {
        using var fs = File.Create(path);
        foreach (var f in frames)
        {
            fs.Write(BitConverter.GetBytes((uint)f.Length));
            fs.Write(f);
        }
    }

    /// <summary>
    /// Applies the frames one by one to a window, pumping between frames; every error is reported with its frame number.
    /// Returns the number of failed frames.
    /// </summary>
    public static int Apply(IntPtr w, List<byte[]> frames, Action<int> pump, Action<string> report, Action<int>? afterFrame = null)
    {
        int errors = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var rc = Native.Apply(w, frames[i]);
            if (rc != 0)
            {
                errors++;
                report($"frame {i}: apply returned {rc}: {Native.LastError()}");
            }
            pump(1);
            afterFrame?.Invoke(i);
        }
        return errors;
    }

    /// <summary>
    /// Paced replay at an exact 60 fps (absolute schedule, message loop + spin between pumps). Times apply and pump (advance + processEvents)
    /// per frame and prints pump avg/max, spikes above 8 ms (with GC deltas) and the native sync/render/present averages.
    /// Leaves the last window open and returns it.
    /// </summary>
    static int s_overlay; // --overlay

    static int RunPaced(List<byte[]> frames, int loops, Action<int> pump, out IntPtr last)
    {
        const double SpikeMs = 8.0;
        const int WarmupFrames = 10; // window creation + first renders are not steady state
        using var _ = PumpTest.HighResTimer();
        long period = Stopwatch.Frequency / 60;
        int errors = 0, frameNo = 0, spikes = 0, steadySpikes = 0;
        double pumpSum = 0, pumpMax = 0, applySum = 0, applyMax = 0, steadySum = 0, steadyMax = 0, lateMax = 0;
        long steadyN = 0;
        var steadyAll = new List<double>(); // for the median (overlay cost, #244)
        IntPtr w = IntPtr.Zero;
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var gcPause = GC.GetTotalPauseDuration();
        var deadline = Stopwatch.GetTimestamp();
        for (int round = 0; round < loops; round++)
        {
            if (w != IntPtr.Zero) { Native.vlqt_window_destroy(w); pump(5); }
            w = Native.vlqt_window_create(800, 600, "VL.Qt replay");
            Native.vlqt_window_show(w);
            if (s_overlay != 0) Native.vlqt_window_set_overlay(w, s_overlay);
            for (int i = 0; i < frames.Count; i++, frameNo++)
            {
                deadline += period;
                PumpTest.ForeignMessageLoopUntil(deadline);
                var late = Stopwatch.GetElapsedTime(deadline).TotalMilliseconds;
                if (late > lateMax) lateMax = late;
                if (late > 16.7) deadline = Stopwatch.GetTimestamp(); // do not catch up with a burst

                var t0 = Stopwatch.GetTimestamp();
                var rc = Native.Apply(w, frames[i]);
                var t1 = Stopwatch.GetTimestamp();
                if (rc != 0) { errors++; Console.WriteLine($"REPLAY ERROR frame {i}: apply returned {rc}: {Native.LastError()}"); }
                pump(1);
                var t2 = Stopwatch.GetTimestamp();

                var apply = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                var pm = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                var total = apply + pm;
                pumpSum += total; applySum += apply;
                if (total > pumpMax) pumpMax = total;
                if (apply > applyMax) applyMax = apply;
                var steady = i >= WarmupFrames;
                if (steady) { steadyN++; steadySum += total; steadyAll.Add(total); if (total > steadyMax) steadyMax = total; }
                if (total > SpikeMs)
                {
                    spikes++;
                    if (steady) steadySpikes++;
                    Console.WriteLine($"REPLAY spike round {round} frame {i}: {total:F1} ms (apply {apply:F1}, advance+processEvents {pm:F1}); GC so far gen0 {GC.CollectionCount(0) - gc0}, gen1 {GC.CollectionCount(1) - gc1}, gen2 {GC.CollectionCount(2) - gc2}{(steady ? "" : " [warmup]")}");
                    // Phases of this pump from the native sample (#243).
                    if (Native.vlqt_window_get_perf_sample(w, out var ps) == 0)
                        Console.WriteLine($"REPLAY spike phases: native pump {ps.pump:F1} = events {ps.events:F1} + sync {ps.sync:F1} + render {ps.render:F1} + present {ps.present:F1}; gpu {ps.gpu:F1}, swaps {ps.swaps}, frame {ps.frame:F1}");
                }
                if (i % 60 == 59 && Native.vlqt_window_get_perf(w, out var p) == 0)
                    Console.WriteLine($"REPLAY round {round} frame {i}: swaps/pump {p.swaps_per_pump:F2}, frame {p.frame_ms:F2} ms (max {p.frame_ms_max:F2}), sync {p.sync_ms:F2} render {p.render_ms:F2} present {p.present_ms:F2}, native pump avg {p.pump_ms:F2} max {p.pump_ms_max:F2}");
            }
        }
        Console.WriteLine($"REPLAY paced: {frameNo} frames ({loops} x {frames.Count}); apply+pump avg {pumpSum / frameNo:F2} ms, max {pumpMax:F2} ms; apply avg {applySum / frameNo:F2}, max {applyMax:F2}");
        if (steadyN > 0) Console.WriteLine($"REPLAY paced steady (frames >= {WarmupFrames} of each round): avg {steadySum / steadyN:F2} ms, max {steadyMax:F2} ms; spikes > {SpikeMs:F0} ms: {steadySpikes} (all frames: {spikes}); max schedule lateness {lateMax:F1} ms");
        if (steadyN > 0)
        {
            steadyAll.Sort();
            Console.WriteLine($"REPLAY paced steady median {steadyAll[steadyAll.Count / 2]:F3} ms, p90 {steadyAll[steadyAll.Count * 9 / 10]:F3} ms (apply+pump, overlay flags {s_overlay})");
        }
        Console.WriteLine($"REPLAY paced GC: gen0 +{GC.CollectionCount(0) - gc0}, gen1 +{GC.CollectionCount(1) - gc1}, gen2 +{GC.CollectionCount(2) - gc2}, pause {(GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds:F1} ms");
        if (Native.vlqt_window_get_perf(w, out var fp) == 0)
            Console.WriteLine($"REPLAY paced last window ring (60 pumps): sync {fp.sync_ms:F2}, render {fp.render_ms:F2}, present {fp.present_ms:F2}, native pump avg {fp.pump_ms:F2} max {fp.pump_ms_max:F2}, swaps/pump {fp.swaps_per_pump:F2}");
        if (Native.vlqt_window_get_perf_stats(w, out var st) == 0)
            Console.WriteLine($"REPLAY paced last window stats (budget {st.budget_ms:F2} ms): pump p50/p95/p99/max {st.pump_p50:F2}/{st.pump_p95:F2}/{st.pump_p99:F2}/{st.pump_max:F2}, frame {st.frame_p50:F2}/{st.frame_p95:F2}/{st.frame_p99:F2}/{st.frame_max:F2}; over budget {st.over_budget}, long frames {st.long_frames}, idle {st.idle}, multi swap {st.multi_swap} of {st.pumps} pumps");
        last = w;
        return errors;
    }

    /// <summary>CLI mode: <c>--replay &lt;trace&gt; [outDir]</c>. Exit 0 = every frame applied without error.</summary>
    public static int RunCli(string tracePath, string outDir, Action<int> pump, string[] args)
    {
        List<byte[]> frames;
        try { frames = ReadFrames(tracePath); }
        catch (Exception e) when (e is IOException or InvalidDataException) { Console.WriteLine("REPLAY FAILED: " + e.Message); return 2; }
        Console.WriteLine($"REPLAY {tracePath}: {frames.Count} frames");

        // Optional "--frames a-b": replay only frames a..b (inclusive, 0-based), e.g. to cut one window's scenario out of a mixed trace.
        var fi = Array.IndexOf(args, "--frames");
        if (fi >= 0 && fi + 1 < args.Length)
        {
            var p = args[fi + 1].Split('-');
            if (p.Length != 2 || !int.TryParse(p[0], out var a) || !int.TryParse(p[1], out var b) || a < 0 || b < a)
            { Console.WriteLine("REPLAY FAILED: --frames expects a-b"); return 2; }
            b = Math.Min(b, frames.Count - 1);
            frames = frames.GetRange(a, Math.Max(0, b - a + 1));
            Console.WriteLine($"REPLAY frames {a}-{b} ({frames.Count})");
        }

        // "--paced": vvvv-like pacing (60 fps, Win32 message loop between pumps), prints swaps per pump (#137).
        // "--loop N" (with --paced): plays the trace N times in a row (fresh window per round) to catch rare spikes (#140).
        var paced = Array.IndexOf(args, "--paced") >= 0;
        // "--overlay F" (with --paced): vlqt_window_set_overlay flags per window, e.g. 18 = F5 Summary, 34 = Timeline,
        // to measure the overlay's pump cost against 0 (#244).
        var oi = Array.IndexOf(args, "--overlay");
        if (oi >= 0 && (oi + 1 >= args.Length || !int.TryParse(args[oi + 1], out s_overlay)))
        { Console.WriteLine("REPLAY FAILED: --overlay expects flags"); return 2; }
        var loops = 1;
        var li = Array.IndexOf(args, "--loop");
        if (li >= 0 && (li + 1 >= args.Length || !int.TryParse(args[li + 1], out loops) || loops < 1))
        { Console.WriteLine("REPLAY FAILED: --loop expects N >= 1"); return 2; }

        int errors;
        IntPtr w;
        if (paced) errors = RunPaced(frames, loops, pump, out w);
        else
        {
            w = Native.vlqt_window_create(800, 600, "VL.Qt replay");
            if (w == IntPtr.Zero) { Console.WriteLine("REPLAY FAILED: window_create " + Native.LastError()); return 1; }
            Native.vlqt_window_show(w);
            errors = Apply(w, frames, pump, m => Console.WriteLine("REPLAY ERROR " + m));
        }
        pump(5);
        var png = Path.Combine(outDir, "replay.png");
        if (Native.vlqt_window_grab(w, png) == 0) Console.WriteLine("REPLAY grab " + png);
        else { errors++; Console.WriteLine("REPLAY ERROR grab: " + Native.LastError()); }
        Native.vlqt_window_destroy(w);
        Console.WriteLine(errors == 0 ? "REPLAY OK" : $"REPLAY FAILED ({errors} errors)");
        return errors == 0 ? 0 : 1;
    }

    /// <summary>The test-rect scenario of the smoke test as a frame sequence (same commands as Program's section 2).</summary>
    static List<byte[]> TestRectScenario()
    {
        var c = new CommandWriter();
        var frames = new List<byte[]>();
        c.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        c.Create(1, Protocol.Type.Rectangle);
        c.SetPlace(1, 1 * U, 1 * U, 3 * U, 2 * U);
        c.SetColor(1, Protocol.Prop.Color, 1, 0.6f, 0, 1);
        c.Reparent(1, 0, 0);
        frames.Add(c.EndFrame().ToArray());
        c.Reset(); // empty frame
        frames.Add(c.EndFrame().ToArray());
        c.Create(2, Protocol.Type.Item);
        c.SetPlace(2, 4 * U, 3 * U, 3 * U, 2 * U);
        c.Reparent(2, 0, 0);
        c.SetPlace(1, 0.5f * U, 0.5f * U, 1 * U, 1 * U);
        c.SetColor(1, Protocol.Prop.Color, 0, 0, 1, 1);
        c.Reparent(1, 2, 0);
        frames.Add(c.EndFrame().ToArray());
        return frames;
    }

    static bool SamePixels(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    /// <summary>Part of the full smoke test: live scenario vs. replay of its trace file must give the same grab.</summary>
    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        var frames = TestRectScenario();

        var live = Native.vlqt_window_create(800, 600, "VL.Qt replay live");
        Native.vlqt_window_show(live);
        int liveErrors = Apply(live, frames, pump, _ => { });
        pump(5);
        using var liveGrab = grab(live, Path.Combine(outDir, "replay_live.png"));
        Native.vlqt_window_destroy(live);
        check(liveErrors == 0, "replay: live scenario applies without errors");

        var tracePath = Path.Combine(outDir, "replay-testrect.bin");
        WriteFrames(tracePath, frames);
        var read = ReadFrames(tracePath);
        check(read.Count == frames.Count && read[0].AsSpan().SequenceEqual(frames[0]) && read[2].AsSpan().SequenceEqual(frames[2]),
            $"replay: trace roundtrip ({read.Count} frames)");

        var rw = Native.vlqt_window_create(800, 600, "VL.Qt replay");
        Native.vlqt_window_show(rw);
        var msgs = new List<string>();
        int errors = Apply(rw, read, pump, msgs.Add);
        pump(5);
        using var replayGrab = grab(rw, Path.Combine(outDir, "replay_replay.png"));
        Native.vlqt_window_destroy(rw);
        check(errors == 0, "replay: trace applies without errors");
        check(SamePixels(liveGrab, replayGrab), "replay: grab identical to live scenario");

        // Error reporting with frame number: unknown id in frame 1 is reported, later frames still apply.
        var bad = new CommandWriter();
        bad.SetPlace(777, 0, 0, 1, 1);
        var broken = new List<byte[]> { frames[0], bad.EndFrame().ToArray(), frames[2] };
        var bw = Native.vlqt_window_create(800, 600, "VL.Qt replay broken");
        Native.vlqt_window_show(bw);
        msgs.Clear();
        int badErrors = Apply(bw, broken, pump, msgs.Add);
        Native.vlqt_window_destroy(bw);
        check(badErrors == 1 && msgs.Count == 1 && msgs[0].StartsWith("frame 1:") && msgs[0].Contains("777"),
            $"replay: error reported with frame number ({string.Join("; ", msgs)})");

        // Truncated trace is rejected.
        var cut = Path.Combine(outDir, "replay-truncated.bin");
        var all = File.ReadAllBytes(tracePath);
        File.WriteAllBytes(cut, all.AsSpan(0, all.Length - 3).ToArray());
        bool threw = false;
        try { ReadFrames(cut); } catch (InvalidDataException) { threw = true; }
        check(threw, "replay: truncated trace rejected");
    }
}
