using System;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// NDIAG-01 regression: the pump includes the VSync wait (QRhi::beginFrame) and the present, but over budget counts only Qt
// work (vlqt_perf_sample.work = pump - vsync - present) above the budget, and the budget is vvvv's frame interval
// (vlqt_app_set_frame_budget; E13, C4). Four parts: (1) vlqt_perf_eval on synthetic pumps of 18 ms of which 14 ms are
// VSync wait (Qt work 3.5 ms): no pump is over budget. (2) The budget follows vlqt_app_set_frame_budget. (3) A VSync window
// with a static scene: over budget stays 0. (4) A VSync window with a changing scene: the sample's work is pump - vsync -
// present, and the counter counts exactly the pumps whose work is above the budget.
[TestFixture, NonParallelizable]
public static class PerfOverBudgetTest
{
    /// <summary>NDIAG-01: over budget counts Qt work only, not the VSync wait and the present.</summary>
    [Test, Category("perf-over-budget")]
    public static void PerfOverBudget() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        const double budget = 1000.0 / 60;
        var samples = new Native.vlqt_perf_sample[20];
        var t = new double[20];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new Native.vlqt_perf_sample { frame_interval = 16.6f, pump = 18, vsync = 14, sync = 0.5f, render = 1, present = 0.5f, events = 2, swaps = 1 };
            t[i] = i * budget;
        }
        var rc = NativeTest.vlqt_test_perf_eval(samples, t, samples.Length, budget, out var st);
        s.Check(rc == 0 && st.over_budget == 0,
            $"eval: 20 pumps of 18 ms with 14 ms VSync wait (Qt work 3.5 ms) are not over budget (rc {rc}, over budget {st.over_budget})");
        for (int i = 0; i < samples.Length; i++)
            samples[i].pump = 20; // Qt work 5.5 ms: over a budget of 5 ms
        rc = NativeTest.vlqt_test_perf_eval(samples, t, samples.Length, 5, out st);
        s.Check(rc == 0 && st.over_budget == 20, $"eval: the same pumps with Qt work 5.5 ms are over a 5 ms budget (rc {rc}, over budget {st.over_budget})");

        s.Check(Native.vlqt_app_set_frame_budget(0) == Native.Result.BadArgument && Native.vlqt_app_set_frame_budget(double.NaN) == Native.Result.BadArgument
              && Native.vlqt_app_set_frame_budget(1000) == Native.Result.BadArgument, "frame budget: 0, NaN and 1000 ms rejected (BadArgument)");

        var w = Native.vlqt_window_create(float.NaN, float.NaN, 640, 480, "VL.Qt perf over budget (VSync)", 1);
        s.Check(w != SceneHandle.Null, "VSync window created " + (w == SceneHandle.Null ? Native.LastError() : ""));
        if (w == SceneHandle.Null)
            return;
        var cmd = new CommandWriter();
        cmd.ClearColor(0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1);
        cmd.Create(1, Protocol.ElementType.Rectangle);
        cmd.SetColor(1, Protocol.PropertyId.Color, 1, 0.6f, 0, 1);
        cmd.SetPlace(1, 100, 200, 100, 60);
        cmd.Reparent(1, 0, 0);
        Native.Apply(w, cmd.EndFrame());
        Native.vlqt_window_show(w);
        for (int i = 0; i < 10; i++)
        {
            System.Threading.Thread.Sleep(16);
            s.Pump(1);
        }

        // Qt renders a VSync window on the update its VBlank thread posts: pump at the host's frame interval like vvvv's
        // frame timer, back-to-back pumps would never see one.
        void HostFrame()
        {
            System.Threading.Thread.Sleep(16);
            s.Pump(1);
        }

        // The budget is the frame interval the host hands over before the pump.
        s.Check(Native.vlqt_app_set_frame_budget(1000.0 / 30) == 0, "frame budget 30 Hz set");
        s.Pump(1);
        Native.vlqt_scene_get_perf_stats(w, out var a);
        Native.vlqt_app_set_frame_budget(budget);
        s.Pump(1);
        Native.vlqt_scene_get_perf_stats(w, out var b);
        s.Check(Math.Abs(a.budget_ms - 1000.0 / 30) < 1e-9 && Math.Abs(b.budget_ms - budget) < 1e-9,
            $"budget_ms follows vlqt_app_set_frame_budget ({a.budget_ms:F3} ms at 30 Hz, {b.budget_ms:F3} ms at 60 Hz)");

        // Static scene.
        Native.vlqt_scene_get_perf_stats(w, out a);
        for (int i = 0; i < 60; i++)
            HostFrame();
        Native.vlqt_scene_get_perf_stats(w, out b);
        s.Check(b.over_budget - a.over_budget == 0, $"static scene with VSync: over budget stays 0 ({b.over_budget - a.over_budget} of {b.pumps - a.pumps} pumps, idle {b.idle - a.idle})");

        // Changing scene: every pump renders and waits for the refresh.
        Native.vlqt_scene_get_perf_stats(w, out a);
        int pumpOver = 0, workOver = 0, swaps = 0, read = 0, badWork = 0, n = 60;
        double vsyncSum = 0, workSum = 0, pumpSum = 0;
        for (int i = 0; i < n; i++)
        {
            cmd.Reset();
            cmd.SetPlace(1, 100 + 50 * MathF.Sin(i * 0.2f), 200, 100, 60);
            Native.Apply(w, cmd.EndFrame());
            HostFrame();
            if (Native.vlqt_scene_get_perf_sample(w, out var p) != 0)
                continue;
            var work = p.work;
            if (Math.Abs(work - Math.Max(0, p.pump - p.vsync - p.present)) > 1e-3f)
                badWork++;
            read++;
            swaps += p.swaps;
            pumpSum += p.pump;
            vsyncSum += p.vsync;
            workSum += work;
            if (p.pump > b.budget_ms)
                pumpOver++;
            if (work > b.budget_ms)
                workOver++;
        }
        Native.vlqt_scene_get_perf_stats(w, out b);
        var counted = b.over_budget - a.over_budget;
        s.Info($"changing scene with VSync: budget {b.budget_ms:F2} ms, {n} pumps ({read} samples read, {swaps} swaps), pump avg {pumpSum / Math.Max(read, 1):F2} ms, vsync wait avg {vsyncSum / Math.Max(read, 1):F2} ms, Qt work avg {workSum / Math.Max(read, 1):F2} ms, pump > budget {pumpOver}, Qt work > budget {workOver}, counter {counted} of {b.pumps - a.pumps} pumps");
        s.Check(badWork == 0, $"changing scene with VSync: sample work = pump - vsync - present ({badWork} of {read} samples differ)");
        // Every pump's sample was read (read == n), so the counter must match the pumps whose work is over the budget.
        s.Check(read == n ? counted == workOver : counted <= workOver,
            $"changing scene with VSync: over budget counts only pumps whose Qt work is above the budget (counter {counted}, Qt work over {workOver}, pump over {pumpOver}, {read} of {n} samples read)");

        cmd.Reset();
        cmd.Destroy(1);
        Native.Apply(w, cmd.EndFrame());
        s.Pump(2);
        Native.vlqt_scene_destroy(w);
    }
}
