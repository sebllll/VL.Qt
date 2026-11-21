using System;
using System.Linq;
using NUnit.Framework;

namespace VL.Qt.Tests
{
    /// <summary>PerfStats additively carries percentiles, counters, budget and host values (apply/reconcile, ops, GC).</summary>
    public class PerfStatsTests
    {
        private static Native.vlqt_perf Perf() => new()
        {
            fps = 60, frame_ms = 16.6, frame_ms_max = 20, pump_ms = 2, pump_ms_max = 5,
            sync_ms = 0.5, render_ms = 0.7, present_ms = 0.3, gpu_ms = 1, gpu_ms_max = 2, swaps_per_pump = 1, samples = 60,
        };

        [Test]
        public void From_MapsStatsAndHost_KeepsSummary()
        {
            var stats = new Native.vlqt_perf_stats
            {
                budget_ms = 16.667,
                frame_p50 = 16, frame_p95 = 17, frame_p99 = 18, frame_max = 30,
                pump_p50 = 1, pump_p95 = 3, pump_p99 = 4, pump_max = 9,
                samples = 240, frames = 230,
                pumps = 1000, over_budget = 3, long_frames = 2, idle = 10, multi_swap = 1,
                recent_pumps = 600, recent_over_budget = 1, recent_long_frames = 1, recent_idle = 4, recent_multi_swap = 0,
            };
            var host = new Native.vlqt_host_pump_stats
            {
                apply_ms = 0.25, apply_ms_max = 0.5, reconcile_ms = 0.75, reconcile_ms_max = 1.5,
                ops = 42, gc0_per_s = 2, gc1_per_s = 0.5, gc2_per_s = 0, gc_pause_ms_per_s = 1.25, alloc_bytes = 4096, samples = 60,
                vvvv_ms = 6.5, vvvv_ms_max = 12, wait_ms = 8.25, wait_ms_max = 9,
            };

            var p = PerfStats.From(Perf(), stats, host);

            // Existing values unchanged (same as the old one-argument From).
            Assert.That(p.Fps, Is.EqualTo(60f));
            Assert.That(p.PumpMsMax, Is.EqualTo(5f));
            Assert.That(p.SwapsPerPump, Is.EqualTo(1f));

            Assert.That(p.BudgetMs, Is.EqualTo(16.667f).Within(1e-4));
            Assert.That((p.FrameMsP50, p.FrameMsP95, p.FrameMsP99), Is.EqualTo((16f, 17f, 18f)));
            Assert.That((p.PumpMsP50, p.PumpMsP95, p.PumpMsP99), Is.EqualTo((1f, 3f, 4f)));
            Assert.That((p.Pumps, p.OverBudget, p.LongFrames, p.IdlePumps, p.MultiSwaps), Is.EqualTo((1000L, 3L, 2L, 10L, 1L)));
            Assert.That((p.RecentPumps, p.RecentOverBudget, p.RecentLongFrames, p.RecentIdlePumps, p.RecentMultiSwaps),
                Is.EqualTo((600L, 1L, 1L, 4L, 0L)));
            Assert.That((p.ApplyMs, p.ApplyMsMax, p.ReconcileMs, p.ReconcileMsMax), Is.EqualTo((0.25f, 0.5f, 0.75f, 1.5f)));
            Assert.That(p.Ops, Is.EqualTo(42f));
            Assert.That((p.Gen0PerSecond, p.Gen1PerSecond, p.Gen2PerSecond), Is.EqualTo((2f, 0.5f, 0f)));
            Assert.That(p.GcPauseMsPerSecond, Is.EqualTo(1.25f));
            Assert.That(p.AllocatedBytesPerFrame, Is.EqualTo(4096f));
            Assert.That((p.VvvvMs, p.VvvvMsMax, p.WaitMs, p.WaitMsMax), Is.EqualTo((6.5f, 12f, 8.25f, 9f)));
        }

        [Test]
        public void OldFromAndEmpty_NewValuesZero_RecordEquality()
        {
            var old = PerfStats.From(Perf());
            Assert.That(old.Pumps, Is.Zero);
            Assert.That(old.BudgetMs, Is.Zero);
            Assert.That(PerfStats.Empty.ApplyMs, Is.Zero);
            Assert.That(PerfStats.From(Perf(), default, default), Is.EqualTo(old), "zero stats/host = the old record");

            var stats = new Native.vlqt_perf_stats { pumps = 1 };
            Assert.That(PerfStats.From(Perf(), stats, default), Is.Not.EqualTo(old), "new values take part in equality");
        }

        private sealed class Window : IQtFrameParticipant
        {
            private readonly Native.vlqt_perf_sample? _sample;
            public Window(Native.vlqt_perf_sample? sample) { _sample = sample; }
            public void ApplyCommands() { }
            public void AfterEvents() { }
            public bool TryGetLastSample(out Native.vlqt_perf_sample sample)
            {
                sample = _sample ?? default;
                return _sample.HasValue;
            }
        }

        private sealed class Plain : IQtFrameParticipant
        {
            public void ApplyCommands() { }
            public void AfterEvents() { }
        }

        [Test]
        public void FormatPhases_ListsWindowsWithSample()
        {
            var s = new Native.vlqt_perf_sample { vsync = 14f, sync = 0.5f, render = 1.25f, present = 0.5f, gpu = 2f, events = 0.5f, swaps = 1 };
            Assert.That(PumpDiagnostics.FormatPhases(new IQtFrameParticipant[] { new Window(s) }),
                Is.EqualTo(" · Qt last pump: vsync wait 14.0, sync 0.5, render 1.3, present 0.5, gpu 2.0, events 0.5 ms (1 swaps)").Or
                  .EqualTo(" · Qt last pump: vsync wait 14.0, sync 0.5, render 1.2, present 0.5, gpu 2.0, events 0.5 ms (1 swaps)"));
            var two = PumpDiagnostics.FormatPhases(new IQtFrameParticipant[] { new Window(null), new Window(s), new Plain() });
            Assert.That(two, Does.StartWith(" · Qt last pump #2: vsync wait 14.0, sync 0.5"));
            Assert.That(PumpDiagnostics.FormatPhases(new IQtFrameParticipant[] { new Plain() }), Is.Empty);
        }

        [Test]
        public void SumWaitMs_SumsVsyncAndPresentOfTheWindowsWithSample()
        {
            var a = new Native.vlqt_perf_sample { vsync = 12f, present = 0.5f };
            var b = new Native.vlqt_perf_sample { present = 4f };
            var c = new Native.vlqt_perf_sample { vsync = 3f, sync = 7f, render = 9f, events = 5f };
            Assert.That(PumpDiagnostics.SumWaitMs(new IQtFrameParticipant[] { new Window(a), new Window(null), new Plain(), new Window(b), new Window(c) }), Is.EqualTo(19.5).Within(1e-6));
            Assert.That(PumpDiagnostics.SumWaitMs(Array.Empty<IQtFrameParticipant>()), Is.Zero);
        }

        [Test]
        public void ExceedsBudget_DoesNotCountTheWait()
        {
            // 60 Hz: budget 16.7 ms, threshold 8.35 ms of Qt work.
            Assert.That(PumpDiagnostics.ExceedsBudget(20, 0, 16.7), Is.True, "20 ms of work");
            Assert.That(PumpDiagnostics.ExceedsBudget(20, 15, 16.7), Is.False, "20 ms pump, 15 ms of it waiting for VSync in beginFrame and Present");
            Assert.That(PumpDiagnostics.ExceedsBudget(20, 10, 16.7), Is.True, "10 ms left over");
            Assert.That(PumpDiagnostics.ExceedsBudget(5, 30, 16.7), Is.False, "present longer than the pump (stale sample): clamped, no negative work");
            Assert.That(PumpDiagnostics.ExceedsBudget(9, 0, 16.7), Is.True);
            Assert.That(PumpDiagnostics.ExceedsBudget(8, 0, 16.7), Is.False);
        }

        [Test]
        public void VSync_IsOnByDefault()
        {
            var window = typeof(WindowScene).GetConstructors().Single().GetParameters().Single(p => p.Name == "vSync");
            Assert.That(window.DefaultValue, Is.True);

            var renderer = typeof(Nodes.RendererNode).GetConstructors().Single().GetParameters().Single(p => p.Name == "vSync");
            Assert.That(renderer.DefaultValue, Is.True);
            Assert.That(renderer.GetCustomAttributes(typeof(System.ComponentModel.DefaultValueAttribute), false).Cast<System.ComponentModel.DefaultValueAttribute>().Single().Value,
                Is.True, "the pin default in vvvv");
        }

        [Test]
        public void SplitOperations_ReadTheInitOnlyProperties()
        {
            var p = PerfStats.Empty with
            {
                BudgetMs = 16f, FrameMsP95 = 3f, PumpMsP99 = 4f, Pumps = 7, RecentMultiSwaps = 2,
                VvvvMs = 6.5f, WaitMsMax = 9f, ReconcileMs = 0.75f, Ops = 42f, Gen2PerSecond = 0.5f, AllocatedBytesPerFrame = 4096f,
            };

            p.SplitPercentiles(out var budget, out _, out var f95, out _, out _, out _, out var p99);
            Assert.That((budget, f95, p99), Is.EqualTo((16f, 3f, 4f)));

            p.SplitCounters(out var pumps, out _, out _, out _, out _, out _, out _, out _, out _, out var recentMulti);
            Assert.That((pumps, recentMulti), Is.EqualTo((7L, 2L)));

            p.SplitHost(out var vvvv, out _, out _, out var waitMax, out _, out _, out var reconcile, out _, out var ops);
            Assert.That((vvvv, waitMax, reconcile, ops), Is.EqualTo((6.5f, 9f, 0.75f, 42f)));

            p.SplitGc(out _, out _, out var gen2, out _, out var alloc);
            Assert.That((gen2, alloc), Is.EqualTo((0.5f, 4096f)));
        }
    }
}
