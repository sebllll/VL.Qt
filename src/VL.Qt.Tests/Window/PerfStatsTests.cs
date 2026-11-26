using System;
using System.Linq;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Tests.Support;

namespace VL.Qt.Tests.Window
{
    /// <summary>PerfStats: the main values and the groups (percentiles, counters, host, GC) from the native structs; the slow-frame warning helpers.</summary>
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

            Assert.That(p.Fps, Is.EqualTo(60f));
            Assert.That(p.PumpMsMax, Is.EqualTo(5f));
            Assert.That(p.SwapsPerPump, Is.EqualTo(1f));

            Assert.That(p.Percentiles.BudgetMs, Is.EqualTo(16.667f).Within(1e-4));
            Assert.That((p.Percentiles.FrameMsP50, p.Percentiles.FrameMsP95, p.Percentiles.FrameMsP99), Is.EqualTo((16f, 17f, 18f)));
            Assert.That((p.Percentiles.PumpMsP50, p.Percentiles.PumpMsP95, p.Percentiles.PumpMsP99), Is.EqualTo((1f, 3f, 4f)));
            Assert.That(p.Counters, Is.EqualTo(new PerfCounters(1000, 3, 2, 10, 1)));
            Assert.That(p.Recent, Is.EqualTo(new PerfCounters(600, 1, 1, 4, 0)));
            Assert.That(p.Host, Is.EqualTo(new HostPerfStats(6.5f, 12f, 8.25f, 9f, 0.25f, 0.5f, 0.75f, 1.5f, 42f)));
            Assert.That(p.Gc, Is.EqualTo(new GcPerfStats(2f, 0.5f, 0f, 1.25f, 4096f)));
        }

        [Test]
        public void From_MapsWork_PumpStaysWithTheWaits()
        {
            var perf = Perf();
            perf.vsync_ms = 0.25;
            perf.work_ms = 1.45; // pump 2 - vsync 0.25 - present 0.3
            var p = PerfStats.From(perf, default, default);
            Assert.That((p.PumpMs, p.VsyncMs, p.WorkMs), Is.EqualTo((2f, 0.25f, 1.45f)), "pump incl. waits, vsync wait and Qt work as QtNative measured them");
        }

        [Test]
        public void From_RecordStructEquality_ZeroIsDefault()
        {
            var perf = Perf();
            Assert.That(PerfStats.From(perf, default, default), Is.EqualTo(PerfStats.From(perf, default, default)));
            Assert.That(PerfStats.From(default, default, default), Is.EqualTo(default(PerfStats)));
            var stats = new Native.vlqt_perf_stats { pumps = 1 };
            Assert.That(PerfStats.From(perf, stats, default), Is.Not.EqualTo(PerfStats.From(perf, default, default)), "group values take part in equality");
        }

        // Native fields that PerfStats does not carry (counts of samples, maxima the pages and the F5 overlay use).
        private static readonly string[] s_unmapped =
        {
            "vlqt_perf.samples", "vlqt_perf_stats.frame_max", "vlqt_perf_stats.pump_max", "vlqt_perf_stats.samples",
            "vlqt_perf_stats.frames", "vlqt_host_pump_stats.ops_max", "vlqt_host_pump_stats.samples",
        };

        [Test]
        public void From_MapsEveryNativeField()
        {
            // Every native field gets its own number; each one must show up in the PerfStats tree, and every value of the
            // tree must come from one of them (a field added to a native struct or to PerfStats alone fails here).
            double next = 1;
            var expected = new System.Collections.Generic.Dictionary<double, string>();
            object Fill(object boxed)
            {
                foreach (var f in boxed.GetType().GetFields())
                {
                    var name = boxed.GetType().Name + "." + f.Name;
                    var value = next++;
                    if (Array.IndexOf(s_unmapped, name) < 0)
                        expected.Add(value, name);
                    f.SetValue(boxed, Convert.ChangeType(value, f.FieldType));
                }
                return boxed;
            }
            var perf = (Native.vlqt_perf)Fill(new Native.vlqt_perf());
            var stats = (Native.vlqt_perf_stats)Fill(new Native.vlqt_perf_stats());
            var host = (Native.vlqt_host_pump_stats)Fill(new Native.vlqt_host_pump_stats());

            var actual = new System.Collections.Generic.List<double>();
            void Collect(object boxed)
            {
                foreach (var f in boxed.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (f.Name == "EqualityContract")
                        continue;
                    var v = f.GetValue(boxed)!;
                    if (v is float or long)
                        actual.Add(Convert.ToDouble(v));
                    else
                        Collect(v);
                }
            }
            Collect(PerfStats.From(perf, stats, host));

            Assert.That(actual, Is.Unique, "two values of PerfStats come from the same native field");
            Assert.That(actual, Is.EquivalentTo(expected.Keys), () => "native fields without a PerfStats value, or PerfStats values without a native field: "
                + string.Join(", ", expected.Where(e => !actual.Contains(e.Key)).Select(e => e.Value)));
        }

        [Test]
        public void From_AllocatesNothing()
        {
            var perf = Perf();
            var stats = new Native.vlqt_perf_stats { pumps = 5 };
            var host = new Native.vlqt_host_pump_stats { ops = 3 };
            PerfStats last = default;
            Action frame = () => last = PerfStats.From(perf, stats, host);
            Assert.That(Allocations.Fewest(frame, 100), Is.EqualTo(0));
            Assert.That(last.Counters.Pumps, Is.EqualTo(5));
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
        public void MaxWorkMs_ReadsTheWorkQtNativeFilled()
        {
            // The slow-frame warning reads vlqt_perf_sample.work, the value QtNative's over budget counter judges; it does
            // not compute Qt work again from the phases (c has phases but work 0: nothing counts).
            var a = new Native.vlqt_perf_sample { pump = 18f, vsync = 14f, present = 0.5f, work = 3.5f };
            var b = new Native.vlqt_perf_sample { pump = 9f, work = 9f };
            var c = new Native.vlqt_perf_sample { pump = 30f, sync = 7f, render = 9f, events = 5f };
            Assert.That(PumpDiagnostics.MaxWorkMs(new IQtFrameParticipant[] { new Window(a), new Window(null), new Plain(), new Window(b), new Window(c) }), Is.EqualTo(9).Within(1e-6));
            Assert.That(PumpDiagnostics.MaxWorkMs(new IQtFrameParticipant[] { new Window(a) }), Is.EqualTo(3.5).Within(1e-6));
            Assert.That(PumpDiagnostics.MaxWorkMs(Array.Empty<IQtFrameParticipant>()), Is.Zero);
        }

        [Test]
        public void ExceedsBudget_JudgesQtWorkAgainstHalfTheBudget()
        {
            // 60 Hz: budget 16.7 ms, threshold 8.35 ms of Qt work.
            Assert.That(PumpDiagnostics.ExceedsBudget(9, 16.7), Is.True);
            Assert.That(PumpDiagnostics.ExceedsBudget(8, 16.7), Is.False);
            // An 18 ms pump with 14 ms VSync wait and 0.5 ms present has 3.5 ms of Qt work: no warning.
            Assert.That(PumpDiagnostics.ExceedsBudget(3.5, 16.7), Is.False);
            // 30 Hz vvvv frame: the same 9 ms of Qt work are within half the budget.
            Assert.That(PumpDiagnostics.ExceedsBudget(9, 1000.0 / 30), Is.False);
        }

        [Test]
        public void VSync_IsOnByDefault()
        {
            var window = typeof(WindowScene).GetConstructors().Single().GetParameters().Single(p => p.Name == "vSync");
            Assert.That(window.DefaultValue, Is.True);

            var renderer = typeof(VL.Qt.Nodes.RendererNode).GetConstructors().Single().GetParameters().Single(p => p.Name == "vSync");
            Assert.That(renderer.DefaultValue, Is.True);
            Assert.That(renderer.GetCustomAttributes(typeof(System.ComponentModel.DefaultValueAttribute), false).Cast<System.ComponentModel.DefaultValueAttribute>().Single().Value,
                Is.True, "the pin default in vvvv");
        }
    }
}
