using System;
using NUnit.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using VL.Qt.Wire;
using VL.Qt;

// Replays command traces recorded with VLQT_TRACE_COMMANDS (plan 01e): file = repeated "u32 length (LE) + frame bytes".
// The trace has no window id, so all frames are applied to one 800x600 window (traces of one window replay exactly).
[TestFixture, NonParallelizable]
public static class ReplayTest
{
    private const float U = 100f;

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
    internal static int Apply(SceneHandle w, List<byte[]> frames, Action<int> pump, Action<string> report, Action<int>? afterFrame = null)
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
    private static int s_overlay; // --overlay

    // Phases a spike is attributed to: C# apply, advance + managed pump overhead (apply+pump minus native pump),
    // then the native PerfSample phases of that pump.
    private static readonly string[] s_phases = { "apply", "advance", "events", "sync", "render", "present" };

    private static int RunPaced(List<byte[]> frames, int loops, Action<int> pump, out SceneHandle last)
    {
        const double SpikeMs = 8.0;
        const double PresentSlowMs = 4.0;
        var spikeCount = new int[s_phases.Length];   // steady spikes per dominant phase
        var spikePhaseMs = new double[s_phases.Length]; // summed phase time over steady spikes
        double spikeTotalMs = 0, presentMax = 0;
        long presentSlow = 0, sampled = 0;
        const int WarmupFrames = 10; // window creation + first renders are not steady state
        using var _ = PumpTest.HighResTimer();
        long period = Stopwatch.Frequency / 60;
        int errors = 0, frameNo = 0, spikes = 0, steadySpikes = 0;
        double pumpSum = 0, pumpMax = 0, applySum = 0, applyMax = 0, steadySum = 0, steadyMax = 0, lateMax = 0;
        long steadyN = 0;
        var steadyAll = new List<double>(); // for the median (overlay cost)
        SceneHandle w = SceneHandle.Null;
        var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
        var gcPause = GC.GetTotalPauseDuration();
        var deadline = Stopwatch.GetTimestamp();
        for (int round = 0; round < loops; round++)
        {
            if (w != SceneHandle.Null) { Native.vlqt_scene_destroy(w); pump(5); }
            w = NativeCalls.CreateWindow(800, 600, "VL.Qt replay");
            Native.vlqt_window_show(w);
            if (s_overlay != 0) Native.vlqt_scene_set_overlay(w, s_overlay);
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
                // Phases of this pump from the native sample.
                var hasSample = Native.vlqt_scene_get_perf_sample(w, out var ps) == 0;
                if (steady && hasSample)
                {
                    sampled++;
                    if (ps.present > presentMax) presentMax = ps.present;
                    if (ps.present > PresentSlowMs) presentSlow++;
                }
                if (total > SpikeMs)
                {
                    spikes++;
                    if (steady) steadySpikes++;
                    Console.WriteLine($"REPLAY spike round {round} frame {i}: {total:F1} ms (apply {apply:F1}, advance+processEvents {pm:F1}); GC so far gen0 {GC.CollectionCount(0) - gc0}, gen1 {GC.CollectionCount(1) - gc1}, gen2 {GC.CollectionCount(2) - gc2}{(steady ? "" : " [warmup]")}");
                    if (hasSample)
                    {
                        Console.WriteLine($"REPLAY spike phases: native pump {ps.pump:F1} = events {ps.events:F1} + sync {ps.sync:F1} + render {ps.render:F1} + present {ps.present:F1}; gpu {ps.gpu:F1}, swaps {ps.swaps}, frame {ps.frame_interval:F1}");
                        if (steady)
                        {
                            ReadOnlySpan<double> phase = [apply, Math.Max(0, pm - ps.pump), ps.events, ps.sync, ps.render, ps.present];
                            var dominant = 0;
                            for (int k = 0; k < phase.Length; k++)
                            {
                                spikePhaseMs[k] += phase[k];
                                if (phase[k] > phase[dominant]) dominant = k;
                            }
                            spikeCount[dominant]++;
                            spikeTotalMs += total;
                        }
                    }
                }
                if (i % 60 == 59 && Native.vlqt_scene_get_perf(w, out var p) == 0)
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
        if (steadySpikes > 0)
        {
            var sb = new System.Text.StringBuilder("REPLAY spike attribution (steady spikes by dominant phase):");
            for (int k = 0; k < s_phases.Length; k++)
                sb.Append($" {s_phases[k]} {spikeCount[k]}");
            sb.Append("; share of spike time:");
            for (int k = 0; k < s_phases.Length; k++)
                sb.Append($" {s_phases[k]} {100 * spikePhaseMs[k] / spikeTotalMs:F0} %");
            Console.WriteLine(sb.ToString());
        }
        if (sampled > 0)
            Console.WriteLine($"REPLAY present steady: max {presentMax:F2} ms, > {PresentSlowMs:F0} ms in {presentSlow} of {sampled} pumps");
        Console.WriteLine($"REPLAY paced GC: gen0 +{GC.CollectionCount(0) - gc0}, gen1 +{GC.CollectionCount(1) - gc1}, gen2 +{GC.CollectionCount(2) - gc2}, pause {(GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds:F1} ms");
        if (Native.vlqt_scene_get_perf(w, out var fp) == 0)
            Console.WriteLine($"REPLAY paced last window ring (60 pumps): sync {fp.sync_ms:F2}, render {fp.render_ms:F2}, present {fp.present_ms:F2}, native pump avg {fp.pump_ms:F2} max {fp.pump_ms_max:F2}, swaps/pump {fp.swaps_per_pump:F2}");
        if (Native.vlqt_scene_get_perf_stats(w, out var st) == 0)
            Console.WriteLine($"REPLAY paced last window stats (budget {st.budget_ms:F2} ms): pump p50/p95/p99/max {st.pump_p50:F2}/{st.pump_p95:F2}/{st.pump_p99:F2}/{st.pump_max:F2}, frame {st.frame_p50:F2}/{st.frame_p95:F2}/{st.frame_p99:F2}/{st.frame_max:F2}; over budget {st.over_budget}, long frames {st.long_frames}, idle {st.idle}, multi swap {st.multi_swap} of {st.pumps} pumps");
        last = w;
        return errors;
    }

    /// <summary>CLI mode: <c>--replay &lt;trace&gt; [outDir]</c>. Exit 0 = every frame applied without error.</summary>
    public static int RunCli(string tracePath, string outDir, Action<int> pump, string[] args)
    {
        List<byte[]> frames;
        if (tracePath == "anim" || tracePath.StartsWith("anim:"))
        {
            // Built-in animated trace: "anim[:frames[:rects]]", written to outDir for reuse.
            var p = tracePath.Split(':');
            var count = p.Length > 1 && int.TryParse(p[1], out var fc) && fc > 0 ? fc : 600;
            var rects = p.Length > 2 && int.TryParse(p[2], out var rc) && rc > 0 ? rc : 300;
            frames = AnimatedScenario(count, rects);
            WriteFrames(Path.Combine(outDir, "replay-anim.bin"), frames);
        }
        else
        {
            try { frames = ReadFrames(tracePath); }
            catch (Exception e) when (e is IOException or InvalidDataException) { Console.WriteLine("REPLAY FAILED: " + e.Message); return 2; }
        }
        Console.WriteLine($"REPLAY {tracePath}: {frames.Count} frames");

        // "--load N": N threads spinning on the CPU while replaying, to provoke scheduling/present spikes.
        var ldi = Array.IndexOf(args, "--load");
        var loadThreads = 0;
        if (ldi >= 0 && (ldi + 1 >= args.Length || !int.TryParse(args[ldi + 1], out loadThreads) || loadThreads < 0))
        { Console.WriteLine("REPLAY FAILED: --load expects N >= 0"); return 2; }
        using var load = new CpuLoad(loadThreads);

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

        // "--paced": vvvv-like pacing (60 fps, Win32 message loop between pumps), prints swaps per pump.
        // "--loop N" (with --paced): plays the trace N times in a row (fresh window per round) to catch rare spikes.
        var paced = Array.IndexOf(args, "--paced") >= 0;
        // "--overlay F" (with --paced): vlqt_scene_set_overlay flags per window, e.g. 18 = F5 Summary, 34 = Timeline,
        // to measure the overlay's pump cost against 0.
        var oi = Array.IndexOf(args, "--overlay");
        if (oi >= 0 && (oi + 1 >= args.Length || !int.TryParse(args[oi + 1], out s_overlay)))
        { Console.WriteLine("REPLAY FAILED: --overlay expects flags"); return 2; }
        var loops = 1;
        var li = Array.IndexOf(args, "--loop");
        if (li >= 0 && (li + 1 >= args.Length || !int.TryParse(args[li + 1], out loops) || loops < 1))
        { Console.WriteLine("REPLAY FAILED: --loop expects N >= 1"); return 2; }

        int errors;
        SceneHandle w;
        if (paced) errors = RunPaced(frames, loops, pump, out w);
        else
        {
            w = NativeCalls.CreateWindow(800, 600, "VL.Qt replay");
            if (w == SceneHandle.Null) { Console.WriteLine("REPLAY FAILED: window_create " + Native.LastError()); return 1; }
            Native.vlqt_window_show(w);
            errors = Apply(w, frames, pump, m => Console.WriteLine("REPLAY ERROR " + m));
        }
        pump(5);
        var png = Path.Combine(outDir, "replay.png");
        if (NativeTest.vlqt_test_window_grab(w, png) == 0) Console.WriteLine("REPLAY grab " + png);
        else { errors++; Console.WriteLine("REPLAY ERROR grab: " + Native.LastError()); }
        Native.vlqt_scene_destroy(w);
        Console.WriteLine(errors == 0 ? "REPLAY OK" : $"REPLAY FAILED ({errors} errors)");
        return errors == 0 ? 0 : 1;
    }

    /// <summary>Busy threads at normal priority for <c>--load</c>; stopped on dispose.</summary>
    private sealed class CpuLoad : IDisposable
    {
        private readonly Thread[] _threads;
        private volatile bool _stop;

        public CpuLoad(int n)
        {
            _threads = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                _threads[i] = new Thread(() => { while (!_stop) Thread.SpinWait(1000); }) { IsBackground = true };
                _threads[i].Start();
            }
            if (n > 0) Console.WriteLine($"REPLAY load: {n} spinning threads");
        }

        public void Dispose()
        {
            _stop = true;
            foreach (var t in _threads) t.Join();
        }
    }

    /// <summary>
    /// Animated scene for spike hunting: <paramref name="rects"/> semi-transparent rounded rectangles moving on
    /// circles plus 8 texts whose content changes every frame (text layout), in <paramref name="count"/> frames.
    /// </summary>
    private static List<byte[]> AnimatedScenario(int count, int rects)
    {
        const int Texts = 8;
        var c = new CommandWriter();
        var frames = new List<byte[]>(count);
        c.ClearColor(0.1f, 0.1f, 0.12f, 1);
        for (uint i = 0; i < rects; i++)
        {
            c.Create(1 + i, Protocol.ElementType.Rectangle);
            c.SetColor(1 + i, Protocol.PropertyId.Color, (i % 7) / 7f, (i % 5) / 5f, (i % 3) / 3f, 0.5f);
            c.SetF32(1 + i, Protocol.PropertyId.Radius, 12);
            c.Reparent(1 + i, 0, i);
        }
        for (uint t = 0; t < Texts; t++)
        {
            var id = (uint)(1 + rects + t);
            c.Create(id, Protocol.ElementType.Text);
            c.SetF32(id, Protocol.PropertyId.FontSize, 28);
            c.SetColor(id, Protocol.PropertyId.Color, 1, 1, 1, 1);
            c.Reparent(id, 0, (uint)rects + t);
        }
        frames.Add(c.EndFrame().ToArray());
        c.Reset();
        for (int f = 0; f < count; f++)
        {
            var time = f / 60f;
            for (uint i = 0; i < rects; i++)
            {
                var a = time * (0.5f + (i % 11) * 0.1f) + i;
                c.SetPlace(1 + i, 400 + 300 * MathF.Cos(a) - 60, 300 + 220 * MathF.Sin(a * 1.3f) - 40, 120, 80);
            }
            for (uint t = 0; t < Texts; t++)
            {
                var id = (uint)(1 + rects + t);
                c.SetPlace(id, 20, 20 + t * 70, 760, 60);
                c.SetString(id, Protocol.PropertyId.Text, $"frame {f} · line {t} · {time:F3} s");
            }
            frames.Add(c.EndFrame().ToArray());
            c.Reset();
        }
        return frames;
    }

    /// <summary>The test-rect scenario of the smoke test as a frame sequence (same commands as Program's section 2).</summary>
    private static List<byte[]> TestRectScenario()
    {
        var c = new CommandWriter();
        var frames = new List<byte[]>();
        c.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        c.Create(1, Protocol.ElementType.Rectangle);
        c.SetPlace(1, 1 * U, 1 * U, 3 * U, 2 * U);
        c.SetColor(1, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
        c.Reparent(1, 0, 0);
        frames.Add(c.EndFrame().ToArray());
        c.Reset(); // empty frame
        frames.Add(c.EndFrame().ToArray());
        c.Create(2, Protocol.ElementType.Group);
        c.SetPlace(2, 4 * U, 3 * U, 3 * U, 2 * U);
        c.Reparent(2, 0, 0);
        c.SetPlace(1, 0.5f * U, 0.5f * U, 1 * U, 1 * U);
        c.SetColor(1, Protocol.PropertyId.Color, 0, 0, 1, 1);
        c.Reparent(1, 2, 0);
        frames.Add(c.EndFrame().ToArray());
        return frames;
    }

    private static bool SamePixels(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    /// <summary>Part of the full smoke test: live scenario vs. replay of its trace file must give the same grab.</summary>
    /// <summary>Command trace replay.</summary>
    [Test, Category("replay")]
    public static void Replay() => Scenario.Run(s => Run(s));

    /// <summary>Former --replay &lt;trace&gt; [--load N] [--frames a-b] [--paced] [--overlay N] [--loop N]: parameters ReplayTrace, ReplayArgs.</summary>
    [Test, Explicit("needs the parameter ReplayTrace"), Category("replay-cli")]
    public static void ReplayCli() => Scenario.Run(s => s.Check(SmokeParameters.ReplayTrace is { } t && RunCli(t, s.OutDir, s.Pump, SmokeParameters.ReplayArgs) == 0, "replay-cli (parameter ReplayTrace)"));

    private static void Run(Scenario s)
    {
        var frames = TestRectScenario();

        var live = NativeCalls.CreateWindow(800, 600, "VL.Qt replay live");
        Native.vlqt_window_show(live);
        int liveErrors = Apply(live, frames, s.Pump, _ => { });
        s.Pump(5);
        using var liveGrab = s.Grab(live, "replay_live.png");
        Native.vlqt_scene_destroy(live);
        s.Check(liveErrors == 0, "replay: live scenario applies without errors");

        var tracePath = Path.Combine(s.OutDir, "replay-testrect.bin");
        WriteFrames(tracePath, frames);
        var read = ReadFrames(tracePath);
        s.Check(read.Count == frames.Count && read[0].AsSpan().SequenceEqual(frames[0]) && read[2].AsSpan().SequenceEqual(frames[2]),
            $"replay: trace roundtrip ({read.Count} frames)");

        var rw = NativeCalls.CreateWindow(800, 600, "VL.Qt replay");
        Native.vlqt_window_show(rw);
        var msgs = new List<string>();
        int errors = Apply(rw, read, s.Pump, msgs.Add);
        s.Pump(5);
        using var replayGrab = s.Grab(rw, "replay_replay.png");
        Native.vlqt_scene_destroy(rw);
        s.Check(errors == 0, "replay: trace applies without errors");
        s.Check(SamePixels(liveGrab, replayGrab), "replay: grab identical to live scenario");

        // Error reporting with frame number: unknown id in frame 1 is reported, later frames still apply.
        var bad = new CommandWriter();
        bad.SetPlace(777, 0, 0, 1, 1);
        var broken = new List<byte[]> { frames[0], bad.EndFrame().ToArray(), frames[2] };
        var bw = NativeCalls.CreateWindow(800, 600, "VL.Qt replay broken");
        Native.vlqt_window_show(bw);
        msgs.Clear();
        int badErrors = Apply(bw, broken, s.Pump, msgs.Add);
        Native.vlqt_scene_destroy(bw);
        s.Check(badErrors == 1 && msgs.Count == 1 && msgs[0].StartsWith("frame 1:") && msgs[0].Contains("777"),
            $"replay: error reported with frame number ({string.Join("; ", msgs)})");

        // Truncated trace is rejected.
        var cut = Path.Combine(s.OutDir, "replay-truncated.bin");
        var all = File.ReadAllBytes(tracePath);
        File.WriteAllBytes(cut, all.AsSpan(0, all.Length - 3).ToArray());
        bool threw = false;
        try { ReadFrames(cut); } catch (InvalidDataException) { threw = true; }
        s.Check(threw, "replay: truncated trace rejected");
    }
}
