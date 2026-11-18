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
    /// per frame: Reset, Update, EndFrame). The fake-sink measurements of 04b–04d cover the diff alone.
    /// </summary>
    public class ReconcilerPerfTests
    {
        const int N = 1000;
        const int Frames = 1000;

        // Plan 04: Update < 0.3 ms. The limit is checked against the median: it is ~4x below the limit (~0.08 ms), while the mean
        // of a shared CI runner is pushed over it by single spikes (0.31 ms seen on linux-x64 with a median of the same size).
        const double MaxMsPerFrame = 0.3;

        static (double medianMsPerFrame, long allocated, int bytesPerFrame) Measure(string label, Element a, Element b, int expectedSets)
        {
            var writer = new CommandWriter();
            var rec = new Reconciler(writer, new IdAllocator(), new SceneRegistry());
            rec.Update(a, Space.DIPTopLeft);
            Assert.That(writer.Count, Is.GreaterThan(N * 3), "first frame creates the whole tree");
            for (int i = 0; i < 200; i++) // warm-up (JIT, buffer growth)
            {
                writer.Reset();
                rec.Update((i & 1) == 0 ? b : a, Space.DIPTopLeft);
                writer.EndFrame();
            }

            var times = new double[Frames];
            int bytes = 0;
            for (int i = 0; i < Frames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                writer.Reset();
                rec.Update((i & 1) == 0 ? b : a, Space.DIPTopLeft);
                bytes = writer.EndFrame().Length;
                times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (writer.Count != expectedSets)
                    Assert.Fail($"frame {i}: {writer.Count} commands, expected {expectedSets}");
            }

            // Allocations are measured in a separate loop with the fewest of up to five attempts: a one-off of the runtime on
            // this thread (tiered JIT, framework lazy init; seen on CI win-x64 as a few KB) must not fail the test, a real
            // per-frame allocation shows in every attempt (see Allocations.Fewest).
            int frame = 0;
            Action step = () =>
            {
                writer.Reset();
                rec.Update((frame++ & 1) == 0 ? b : a, Space.DIPTopLeft);
                writer.EndFrame();
            };
            long allocated = Allocations.Fewest(step, Frames);
            Array.Sort(times);
            double mean = 0;
            foreach (var t in times) mean += t;
            mean /= Frames;
            TestContext.Out.WriteLine($"{label} through CommandWriter: mean {mean:F4} ms, median {times[Frames / 2]:F4} ms, p99 {times[Frames * 99 / 100]:F4} ms, " +
                $"max {times[^1]:F4} ms/frame, {allocated} bytes allocated, {bytes} bytes/frame");
            return (times[Frames / 2], allocated, bytes);
        }

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

        [Test]
        public void Group_1000Rectangles_OneChangePerFrame_ThroughCommandWriter()
        {
            var children = Rects(_ => Color4.White);
            var changed = new Rectangle { Size = children[N / 2].Size, Fill = new Color4(1, 0, 0, 1) };
            var a = new Group { Children = ImmutableArray.Create(children) };
            var b = new Group { Children = ImmutableArray.Create(WithOneChanged(children, changed)) };

            var (ms, allocated, bytes) = Measure("Group, 1000 rectangles, 1 color change/frame:", a, b, expectedSets: 1);
            Assert.That(allocated, Is.EqualTo(0));
            Assert.That(ms, Is.LessThan(MaxMsPerFrame));
            Assert.That(bytes, Is.GreaterThan(0));
        }

        [Test]
        public void Column_1000Rectangles_OneSizeChangePerFrame_ThroughCommandWriter()
        {
            // Layout children: only the size goes to Qt (04c), the changed child sends one Size SET.
            var children = Rects(_ => Color4.White);
            var changed = new Rectangle { Size = new Vector2(0.02f, 0.01f), Fill = Color4.White };
            var a = new Column { Spacing = 0.01f, Children = ImmutableArray.Create(children) };
            var b = new Column { Spacing = 0.01f, Children = ImmutableArray.Create(WithOneChanged(children, changed)) };

            var (ms, allocated, _) = Measure("Column, 1000 rectangles, 1 size change/frame:", a, b, expectedSets: 1);
            Assert.That(allocated, Is.EqualTo(0));
            Assert.That(ms, Is.LessThan(MaxMsPerFrame));
        }

        [Test]
        public void Group_1000Texts_OneContentChangePerFrame_ThroughCommandWriter()
        {
            // String SETs are UTF-8 encoded into the buffer without allocation.
            var children = new Element[N];
            for (int i = 0; i < N; i++)
                children[i] = new Text { Size = new Vector2(1, 0.01f), Content = "Text " + i };
            var changed = new Text { Size = children[N / 2].Size, Content = "Geändert ✓" };
            var a = new Group { Children = ImmutableArray.Create(children) };
            var b = new Group { Children = ImmutableArray.Create(WithOneChanged(children, changed)) };

            var (ms, allocated, _) = Measure("Group, 1000 texts, 1 content change/frame:", a, b, expectedSets: 1);
            Assert.That(allocated, Is.EqualTo(0));
            Assert.That(ms, Is.LessThan(MaxMsPerFrame));
        }
    }
}
