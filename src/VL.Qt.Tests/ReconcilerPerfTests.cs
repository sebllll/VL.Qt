using System;
using System.Collections.Immutable;
using System.Diagnostics;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>
    /// 04e: steady-state performance of Reconciler → real <see cref="CommandWriter"/> (the path a renderer takes
    /// per frame: Reset, Update, EndFrame). The allocation tests are deterministic and hard; the timing tests (category
    /// <c>Perf</c>) go through <see cref="PerfLimit"/>: scaled by VLQT_PERF_FACTOR, warn-only with VLQT_PERF_WARN_ONLY (TEST-09).
    /// </summary>
    public class ReconcilerPerfTests
    {
        const int N = 1000;
        const int Frames = 1000;

        // Plan 04: Update < 0.3 ms. Checked against the median: it is ~4x below the limit (~0.08 ms), while the mean
        // of a shared CI runner is pushed over it by single spikes (0.31 ms seen on linux-x64 with a median of the same size).
        const double MaxMsPerFrame = 0.3;

        /// <summary>One frame of a prepared scenario plus the commands it must write.</summary>
        sealed record Scenario(string Label, Action Step, Func<int> Commands, int ExpectedCommands);

        static Scenario Alternating(string label, Element a, Element b, int expectedSets)
        {
            var writer = new CommandWriter();
            var rec = new Reconciler(writer, new IdAllocator(), new SceneRegistry());
            rec.Update(a, Space.DIPTopLeft);
            Assert.That(writer.Count, Is.GreaterThan(N * 3), "first frame creates the whole tree");
            int frame = 0;
            Action step = () =>
            {
                writer.Reset();
                rec.Update((frame++ & 1) == 0 ? b : a, Space.DIPTopLeft);
                writer.EndFrame();
            };
            return Warm(new Scenario(label, step, () => writer.Count, expectedSets));
        }

        static Scenario Warm(Scenario s)
        {
            for (int i = 0; i < 200; i++) // warm-up (JIT, buffer growth)
            {
                s.Step();
                Assert.That(s.Commands(), Is.EqualTo(s.ExpectedCommands), $"{s.Label} warm-up frame {i}");
            }
            return s;
        }

        /// <summary>Median ms per frame, best of up to three rounds (a real regression stays above the limit in every round).</summary>
        static double MedianMs(Scenario s, double limit)
        {
            var best = double.MaxValue;
            var times = new double[Frames];
            for (int round = 0; round < 3 && !(best < limit * PerfLimit.Factor); round++)
            {
                for (int i = 0; i < Frames; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    s.Step();
                    times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (s.Commands() != s.ExpectedCommands)
                        Assert.Fail($"{s.Label} frame {i}: {s.Commands()} commands, expected {s.ExpectedCommands}");
                }
                Array.Sort(times);
                best = Math.Min(best, times[Frames / 2]);
            }
            TestContext.Out.WriteLine($"{s.Label} median {best:F4} ms/frame (last round: p99 {times[Frames * 99 / 100]:F4}, max {times[^1]:F4})");
            return best;
        }

        // Allocations: the fewest of up to five attempts, so a one-off of the runtime on this thread (tiered JIT, framework lazy init)
        // does not fail the test; a real per-frame allocation shows in every attempt (see Allocations.Fewest).
        static void AssertNoAllocation(Scenario s) =>
            Assert.That(Allocations.Fewest(s.Step, Frames), Is.EqualTo(0), s.Label);

        static void AssertFast(Scenario s) => PerfLimit.Below(MedianMs(s, MaxMsPerFrame), MaxMsPerFrame, s.Label + " ms/frame");

        static Element[] Rects(Func<int, Color4> fill)
        {
            var children = new Element[N];
            for (int i = 0; i < N; i++)
                children[i] = new Rectangle { Size = new Vector2(0.01f, 0.01f), Fill = fill(i) };
            return children;
        }

        static Element[] WithOneChanged(Element[] children, Element changed)
        {
            var copy = (Element[])children.Clone();
            copy[N / 2] = changed;
            return copy;
        }

        static Scenario GroupOneColorChange()
        {
            var children = Rects(_ => Color4.White);
            var changed = new Rectangle { Size = children[N / 2].Size, Fill = new Color4(1, 0, 0, 1) };
            var a = new Group { Children = ImmutableArray.Create(children) };
            var b = new Group { Children = ImmutableArray.Create(WithOneChanged(children, changed)) };
            return Alternating("Group, 1000 rectangles, 1 color change/frame:", a, b, expectedSets: 1);
        }

        static Scenario ColumnOneSizeChange()
        {
            // Layout children: only the size goes to Qt (04c), the changed child sends one Size SET.
            var children = Rects(_ => Color4.White);
            var changed = new Rectangle { Size = new Vector2(0.02f, 0.01f), Fill = Color4.White };
            var a = new Column { Spacing = 0.01f, Children = ImmutableArray.Create(children) };
            var b = new Column { Spacing = 0.01f, Children = ImmutableArray.Create(WithOneChanged(children, changed)) };
            return Alternating("Column, 1000 rectangles, 1 size change/frame:", a, b, expectedSets: 1);
        }

        static Scenario GroupOneContentChange()
        {
            // String SETs are UTF-8 encoded into the buffer without allocation.
            var children = new Element[N];
            for (int i = 0; i < N; i++)
                children[i] = new Text { Size = new Vector2(1, 0.01f), Content = "Text " + i };
            var changed = new Text { Size = children[N / 2].Size, Content = "Geändert ✓" };
            var a = new Group { Children = ImmutableArray.Create(children) };
            var b = new Group { Children = ImmutableArray.Create(WithOneChanged(children, changed)) };
            return Alternating("Group, 1000 texts, 1 content change/frame:", a, b, expectedSets: 1);
        }

        static Scenario GroupUnchanged()
        {
            // Two alternating roots with the same children: the root is diffed, every child is skipped by reference; no command.
            var arr = ImmutableArray.Create(Rects(_ => Color4.White));
            return Alternating("Group, 1000 rectangles, unchanged:", new Group { Children = arr }, new Group { Children = arr }, expectedSets: 0);
        }

        static Scenario BoundChildrenOneChange()
        {
            // A fresh Group per frame; one bound child alternates its description, the sink slot stays the same.
            var sink = new ElementSink();
            var children = new Element[N];
            var alt = new Element[N];
            for (int i = 0; i < N; i++)
            {
                children[i] = new Rectangle { Size = new Vector2(1, 1), Sink = new SinkSlot(sink, i) };
                alt[i] = new Rectangle { Size = new Vector2(2, 1), Sink = new SinkSlot(sink, i) };
            }
            var writer = new CommandWriter();
            var rec = new Reconciler(writer, new IdAllocator(), new SceneRegistry());
            var builder = ImmutableArray.Create(children).ToBuilder();
            rec.Update(new Group { Children = builder.ToImmutable() }, Space.DIPTopLeft);
            int f = 0;
            Action step = () =>
            {
                int i = f++ % N;
                builder[i] = ReferenceEquals(builder[i], children[i]) ? alt[i] : children[i];
                writer.Reset();
                rec.Update(new Group { Children = builder.ToImmutable() }, Space.DIPTopLeft);
                writer.EndFrame();
            };
            for (int i = 0; i < 2 * N; i++)
                step(); // every registry slot used once
            return new Scenario("Group, 1000 bound children, 1 change/frame:", step, () => writer.Count, 1);
        }

        [Test]
        public void Group_1000Rectangles_OneChangePerFrame_NoAllocations() => AssertNoAllocation(GroupOneColorChange());

        [Test, Category("Perf")]
        public void Group_1000Rectangles_OneChangePerFrame_Fast() => AssertFast(GroupOneColorChange());

        [Test]
        public void Column_1000Rectangles_OneSizeChangePerFrame_NoAllocations() => AssertNoAllocation(ColumnOneSizeChange());

        [Test, Category("Perf")]
        public void Column_1000Rectangles_OneSizeChangePerFrame_Fast() => AssertFast(ColumnOneSizeChange());

        [Test]
        public void Group_1000Texts_OneContentChangePerFrame_NoAllocations() => AssertNoAllocation(GroupOneContentChange());

        [Test, Category("Perf")]
        public void Group_1000Texts_OneContentChangePerFrame_Fast() => AssertFast(GroupOneContentChange());

        [Test]
        public void Group_1000Rectangles_Unchanged_NoAllocations() => AssertNoAllocation(GroupUnchanged());

        [Test, Category("Perf")]
        public void Group_1000Rectangles_Unchanged_Fast() => AssertFast(GroupUnchanged());

        // The allocation check of the bound variant stays in ReconcilerSceneTests (it subtracts the test's own Group allocations).
        [Test, Category("Perf")]
        public void Group_1000BoundChildren_OneChangePerFrame_Fast() => AssertFast(BoundChildrenOneChange());
    }
}
