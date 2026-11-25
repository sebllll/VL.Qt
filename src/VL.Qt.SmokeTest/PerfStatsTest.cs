using System;
using NUnit.Framework;
using VL.Qt;
using VL.Qt.Interop;

/// <summary>
/// percentiles and counters of vlqt_perf_stats on known samples (vlqt_perf_eval), plus the live window API
/// (vlqt_scene_get_perf_sample / vlqt_scene_get_perf_stats).
/// </summary>
[TestFixture, NonParallelizable]
public static class PerfStatsTest
{
    /// <summary>Performance statistics of a window.</summary>
    [Test, Category("perfstats")]
    public static void PerfStatistics() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        // 100 pumps, one every 200 ms (0 .. 19.8 s, "now" = 19.8 s -> last 10 s = pumps 50..99).
        // pump = frame = i + 1 ms; swaps 0 for i % 10 == 0, 2 for i % 10 == 5, else 1.
        const double budget = 1000.0 / 60;
        var samples = new Native.vlqt_perf_sample[100];
        var t = new double[100];
        for (int i = 0; i < 100; i++)
        {
            var swaps = i % 10 == 0 ? 0 : i % 10 == 5 ? 2 : 1;
            samples[i] = new Native.vlqt_perf_sample { pump = i + 1, frame_interval = swaps > 0 ? i + 1 : 0, swaps = swaps };
            t[i] = i * 200.0;
        }
        var rc = NativeTest.vlqt_test_perf_eval(samples, t, samples.Length, budget, out var st);
        // frame intervals only between consecutive frames -> pumps after an idle one (i % 10 == 1) do not count.
        s.Check(rc == 0 && st.budget_ms == budget && st.samples == 100 && st.frames == 80,
            $"perfstats: eval ok, budget, 100 samples, 80 consecutive frames (rc {rc}, {st.samples}, {st.frames})");
        s.Check(st.pump_p50 == 50 && st.pump_p95 == 95 && st.pump_p99 == 99 && st.pump_max == 100,
            $"perfstats: pump p50/p95/p99/max = 50/95/99/100 ({st.pump_p50}/{st.pump_p95}/{st.pump_p99}/{st.pump_max})");
        // Frames: 1..100 without x1 and x2 (80 values, 8 per decade): ranks 40 / 76 / 80 -> 50 / 96 / 100.
        s.Check(st.frame_p50 == 50 && st.frame_p95 == 96 && st.frame_p99 == 100 && st.frame_max == 100,
            $"perfstats: frame p50/p95/p99/max = 50/96/100/100 ({st.frame_p50}/{st.frame_p95}/{st.frame_p99}/{st.frame_max})");
        // Over budget (Qt work = pump here: no vsync wait, no present): pump 17..100 = 84 (recent 51..100 = 50). Long frames (> 25 ms, swap in this and the previous
        // pump): i 25..99 minus i % 10 == 0 or 1 = 61 (recent i 50..99: 40). Idle / multi: 10 each (recent 5).
        s.Check(st.pumps == 100 && st.over_budget == 84 && st.long_frames == 61 && st.idle == 10 && st.multi_swap == 10,
            $"perfstats: counters since start 100/84/61/10/10 ({st.pumps}/{st.over_budget}/{st.long_frames}/{st.idle}/{st.multi_swap})");
        s.Check(st.recent_pumps == 50 && st.recent_over_budget == 50 && st.recent_long_frames == 40 && st.recent_idle == 5 && st.recent_multi_swap == 5,
            $"perfstats: counters last 10 s 50/50/40/5/5 ({st.recent_pumps}/{st.recent_over_budget}/{st.recent_long_frames}/{st.recent_idle}/{st.recent_multi_swap})");

        // #261 on-demand: a frame every 8th pump (16 ms pumps, frame = 128 ms after 7 idle pumps) -> no long frames,
        // no frame intervals; a following animated burst (frame 16 ms each pump) counts normally.
        samples = new Native.vlqt_perf_sample[80];
        t = new double[80];
        for (int i = 0; i < 80; i++)
        {
            var animated = i >= 64;
            var swaps = animated || i % 8 == 7 ? 1 : 0;
            samples[i] = new Native.vlqt_perf_sample { pump = 0.2f, frame_interval = swaps == 0 ? 0 : animated ? 16 : 128, swaps = swaps };
            t[i] = i * 16.0;
        }
        rc = NativeTest.vlqt_test_perf_eval(samples, t, samples.Length, budget, out st);
        s.Check(rc == 0 && st.long_frames == 0 && st.idle == 56 && st.frames == 16 && st.frame_max == 16,
            $"perfstats: on demand -> long frames 0, idle 56, 16 consecutive frames, frame max 16 (long {st.long_frames}, idle {st.idle}, frames {st.frames}, max {st.frame_max})");

        // NDIAG-02: frame statistics (vlqt_perf: fps, frame_ms) only from consecutive frames. 60 pumps of 16 ms frames,
        // with and without a 2 s idle gap (the frame after it has interval 2000 and a previous pump without a frame).
        var anim = new Native.vlqt_perf_sample[60];
        for (int i = 0; i < 60; i++)
            anim[i] = new Native.vlqt_perf_sample { pump = 1, sync = 0.2f, render = 0.4f, present = 0.1f, frame_interval = 16, swaps = 1 };
        rc = NativeTest.vlqt_test_perf_summary_eval(anim, anim.Length, out var pf);
        s.Check(rc == 0 && pf.samples == 60 && pf.frame_ms == 16 && pf.frame_ms_max == 16 && Math.Abs(pf.fps - 62.5) < 1e-9,
            $"perfstats: summary of 60 frames of 16 ms -> 16 ms, 62.5 fps (rc {rc}, frame {pf.frame_ms}, max {pf.frame_ms_max}, fps {pf.fps})");
        var gap = (Native.vlqt_perf_sample[])anim.Clone();
        for (int i = 30; i < 40; i++) gap[i] = new Native.vlqt_perf_sample { pump = 0.1f, swaps = 0 }; // idle pumps
        gap[40] = gap[40] with { frame_interval = 2000 }; // first frame after the idle pumps
        rc = NativeTest.vlqt_test_perf_summary_eval(gap, gap.Length, out var pg);
        s.Check(rc == 0 && pg.frame_ms == 16 && pg.frame_ms_max == 16 && Math.Abs(pg.fps - 62.5) < 1e-9,
            $"perfstats: an idle gap does not change frame_ms, frame_ms_max or fps (rc {rc}, frame {pg.frame_ms}, max {pg.frame_ms_max}, fps {pg.fps})");
        // Phases per pump that rendered: render 0.4 in 50 of the 60 pumps -> 0.4, not 0.4 * 50 / 60.
        s.Check(Math.Abs(pg.render_ms - 0.4) < 1e-6 && Math.Abs(pg.sync_ms - 0.2) < 1e-6 && Math.Abs(pg.present_ms - 0.1) < 1e-6,
            $"perfstats: phase averages per pump that rendered (render {pg.render_ms}, sync {pg.sync_ms}, present {pg.present_ms})");
        // Idle scene: a frame every 8th pump only (on demand) -> no consecutive frame, fps 0.
        var demand = new Native.vlqt_perf_sample[60];
        for (int i = 0; i < 60; i++)
            demand[i] = i % 8 == 7 ? new Native.vlqt_perf_sample { pump = 1, frame_interval = 128, swaps = 1 } : new Native.vlqt_perf_sample { pump = 0.1f };
        rc = NativeTest.vlqt_test_perf_summary_eval(demand, demand.Length, out var pd);
        s.Check(rc == 0 && pd.fps == 0 && pd.frame_ms == 0 && pd.frame_ms_max == 0,
            $"perfstats: an idle / on-demand scene has fps 0, frame_ms 0 (rc {rc}, fps {pd.fps}, frame {pd.frame_ms}, max {pd.frame_ms_max})");
        rc = NativeTest.vlqt_test_perf_summary_eval(anim, 0, out pd);
        s.Check(rc == 0 && pd.samples == 0 && pd.fps == 0, "perfstats: summary without samples -> zeros");

        // Ring limit: 300 samples -> percentiles over the last 240 (pump 60..299), counters over all.
        samples = new Native.vlqt_perf_sample[300];
        t = new double[300];
        for (int i = 0; i < 300; i++) { samples[i] = new Native.vlqt_perf_sample { pump = i, frame_interval = 16, swaps = 1 }; t[i] = i * 16.0; }
        rc = NativeTest.vlqt_test_perf_eval(samples, t, samples.Length, budget, out st);
        s.Check(rc == 0 && st.samples == 240 && st.pumps == 300 && st.pump_p50 == 179 && st.pump_max == 299 && st.frame_p99 == 16 && st.long_frames == 0,
            $"perfstats: ring of 240 (samples {st.samples}, pumps {st.pumps}, p50 {st.pump_p50}, max {st.pump_max}, frame p99 {st.frame_p99})");
        rc = NativeTest.vlqt_test_perf_eval(samples, t, 0, budget, out st);
        s.Check(rc == 0 && st.samples == 0 && st.pumps == 0 && st.pump_max == 0, "perfstats: no samples -> zeros");

        // Live window: last sample and stats after a few pumps.
        var w = NativeCalls.CreateWindow(320, 240, "VL.Qt perfstats");
        rc = Native.vlqt_scene_get_perf_sample(w, out var ls);
        s.Check(rc == Native.Result.NoData, $"perfstats: no sample before the first pump (rc {rc})");
        Native.vlqt_window_show(w);
        s.Pump(20);
        rc = Native.vlqt_scene_get_perf_sample(w, out ls);
        var phases = ls.events + ls.vsync + ls.sync + ls.render + ls.present;
        s.Check(rc == 0 && ls.pump > 0 && ls.events >= 0 && phases >= ls.pump - 0.01f,
            $"perfstats: last sample, events = pump - frame phases (rc {rc}, pump {ls.pump:F2}, events {ls.events:F2}, sync {ls.sync:F2}, render {ls.render:F2}, present {ls.present:F2}, vsync {ls.vsync:F2}, swaps {ls.swaps})");
        rc = Native.vlqt_scene_get_perf_stats(w, out st);
        s.Check(rc == 0 && st.budget_ms >= 1 && st.budget_ms <= 1000.0 / 23 && st.pumps >= 20 && st.samples >= 20 && st.pump_max >= st.pump_p50,
            $"perfstats: window stats (rc {rc}, budget {st.budget_ms:F2} ms, pumps {st.pumps}, samples {st.samples}, pump p50 {st.pump_p50:F2} max {st.pump_max:F2})");
        s.Check(Native.vlqt_scene_get_perf_sample(SceneHandle.Null, out _) == Native.Result.NullHandle && Native.vlqt_scene_get_perf_stats(SceneHandle.Null, out _) == Native.Result.NullHandle,
            "perfstats: null window -> NullHandle");

        // host frames. Eval: 4 frames of 250 ms, apply 1..4, ops 10..40, alloc 100..400, gen0 1 each, gen2 once.
        var hf = new Native.vlqt_host_pump[4];
        for (int i = 0; i < 4; i++)
            hf[i] = new Native.vlqt_host_pump { apply = i + 1, reconcile = 2 * (i + 1), host = 5, interval = 250, gc_pause = 0.5f,
                ops = 10 * (i + 1), gc0 = 1, gc2 = i == 3 ? 1 : 0, alloc_bytes = 100 * (i + 1),
                vvvv = 2 * (i + 1), wait = i == 2 ? 10 : 1 }; // vvvv 2..8, wait 1,1,10,1
        rc = NativeTest.vlqt_test_perf_host_eval(hf, 4, out var hs);
        s.Check(rc == 0 && hs.samples == 4 && hs.apply_ms == 2.5 && hs.apply_ms_max == 4 && hs.reconcile_ms == 5 && hs.reconcile_ms_max == 8
              && hs.host_ms == 5 && hs.ops == 25 && hs.ops_max == 40 && hs.gc0_per_s == 4 && hs.gc1_per_s == 0 && hs.gc2_per_s == 1
              && hs.gc_pause_ms_per_s == 2 && hs.alloc_bytes == 250 && hs.alloc_bytes_max == 400
              && hs.vvvv_ms == 5 && hs.vvvv_ms_max == 8 && hs.wait_ms == 3.25 && hs.wait_ms_max == 10,
            $"perfstats: host eval (rc {rc}, n {hs.samples}, apply {hs.apply_ms}/{hs.apply_ms_max}, ops {hs.ops}/{hs.ops_max}, gen0/1/2 per s {hs.gc0_per_s}/{hs.gc1_per_s}/{hs.gc2_per_s}, pause/s {hs.gc_pause_ms_per_s}, alloc {hs.alloc_bytes}/{hs.alloc_bytes_max}, vvvv {hs.vvvv_ms}/{hs.vvvv_ms_max}, wait {hs.wait_ms}/{hs.wait_ms_max})");
        rc = NativeTest.vlqt_test_perf_host_eval(hf, 0, out hs);
        s.Check(rc == 0 && hs.samples == 0 && hs.gc0_per_s == 0, "perfstats: host eval, no frames -> zeros");

        // Live: report after a pump, read back; the next pump without a report has none.
        var f = new Native.vlqt_host_pump { apply = 0.5f, host = 3, interval = 16, ops = 7, gc0 = 1, alloc_bytes = 1234, vvvv = 10.5f, wait = 2.25f };
        rc = Native.vlqt_scene_report_host_pump(w, f);
        var rc2 = Native.vlqt_scene_get_host_pump(w, out var back);
        Native.vlqt_scene_get_host_pump_stats(w, out hs);
        var ps = PerfStats.From(default, default, hs);
        s.Check(rc == 0 && rc2 == 0 && back.ops == 7 && back.alloc_bytes == 1234 && back.apply == 0.5f && hs.samples == 1 && hs.ops == 7
              && Math.Abs(hs.gc0_per_s - 62.5) < 1e-6 && back.vvvv == 10.5f && back.wait == 2.25f
              && hs.vvvv_ms == 10.5 && hs.vvvv_ms_max == 10.5 && hs.wait_ms == 2.25 && ps.VvvvMs == 10.5f && ps.VvvvMsMax == 10.5f && ps.WaitMs == 2.25f,
            $"perfstats: host frame report/read back (rc {rc}/{rc2}, ops {back.ops}, alloc {back.alloc_bytes}, stats n {hs.samples}, gen0/s {hs.gc0_per_s}, vvvv {back.vvvv}/{hs.vvvv_ms}/{ps.VvvvMs}, wait {back.wait}/{hs.wait_ms})");
        s.Pump(1);
        rc = Native.vlqt_scene_get_host_pump(w, out _);
        s.Check(rc == Native.Result.NoData, $"perfstats: next pump without report -> NoData (rc {rc})");
        s.Check(Native.vlqt_scene_report_host_pump(SceneHandle.Null, f) == Native.Result.NullHandle && Native.vlqt_scene_get_host_pump(SceneHandle.Null, out _) == Native.Result.NullHandle
              && Native.vlqt_scene_get_host_pump_stats(SceneHandle.Null, out _) == Native.Result.NullHandle,
            "perfstats: host API, null window -> NullHandle");
        var w2 = NativeCalls.CreateWindow(64, 64, "VL.Qt perfstats 2");
        s.Check(Native.vlqt_scene_report_host_pump(w2, f) == Native.Result.NoData, "perfstats: host report before the first pump -> NoData");
        Native.vlqt_scene_destroy(w2);

        Native.vlqt_scene_destroy(w);
        s.Pump(2);
    }
}
