using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Paths;

namespace VL.Qt.Tests.Paths
{
    public class BezierGeometryTests
    {
        private static readonly Vector2[] anchors = { new(0, 0), new(100, 0), new(100, 100) };
        private static readonly Vector2[] none = Array.Empty<Vector2>();
        private static readonly HandleMode[] noModes = Array.Empty<HandleMode>();

        private static QtPath Build(Vector2[] a, Vector2[] inH, Vector2[] outH, HandleMode[] modes, bool closed) =>
            BezierGeometry.Build(QtPath.Empty, a, inH, outH, modes, closed);

        private static (byte[] verbs, float[] points) Flat(QtPath p)
        {
            var f = p.Flatten();
            return (f.Verbs.AsSpan().ToArray(), f.Points.AsSpan().ToArray());
        }

        // Point layout of an open path: 0 = start, then per segment c1, c2, end (1-3 segment 0, 4-6 segment 1).
        private static void AssertPoint(float[] pts, int pointIndex, float x, float y)
        {
            Assert.That(pts[pointIndex * 2], Is.EqualTo(x).Within(1e-4f), $"x of point {pointIndex}");
            Assert.That(pts[pointIndex * 2 + 1], Is.EqualTo(y).Within(1e-4f), $"y of point {pointIndex}");
        }

        [Test]
        public void FreeUsesHandlesAsGiven()
        {
            var inH = new Vector2[] { new(0, 0), new(-10, 5), new(0, 0) };
            var outH = new Vector2[] { new(20, 0), new(7, 7), new(0, 0) };
            var (verbs, pts) = Flat(Build(anchors, inH, outH, new[] { HandleMode.Free }, false));
            Assert.That(verbs, Is.EqualTo(new[] { Protocol.Verb.Move, Protocol.Verb.Cubic, Protocol.Verb.Cubic }));
            AssertPoint(pts, 0, 0, 0);
            AssertPoint(pts, 1, 20, 0);    // a0 + out0
            AssertPoint(pts, 2, 90, 5);    // a1 + in1
            AssertPoint(pts, 3, 100, 0);
            AssertPoint(pts, 4, 107, 7);   // a1 + out1
            AssertPoint(pts, 5, 100, 100); // a2 + in2 (zero)
            AssertPoint(pts, 6, 100, 100);
        }

        [Test]
        public void EmptyModesMeanFree()
        {
            var inH = new Vector2[] { new(0, 0), new(-10, 5), new(0, 0) };
            var outH = new Vector2[] { new(20, 0), new(7, 7), new(0, 0) };
            var a = Flat(Build(anchors, inH, outH, noModes, false));
            var b = Flat(Build(anchors, inH, outH, new[] { HandleMode.Free }, false));
            Assert.That(a.points, Is.EqualTo(b.points));
        }

        [Test]
        public void MirroredNegatesOutHandle()
        {
            var inH = new Vector2[] { new(0, 0), new(99, 99), new(0, 0) };
            var outH = new Vector2[] { new(0, 0), new(7, 3), new(0, 0) };
            var (_, pts) = Flat(Build(anchors, inH, outH, new[] { HandleMode.Mirrored }, false));
            AssertPoint(pts, 2, 93, -3);   // a1 - out1, the given in handle is ignored
            AssertPoint(pts, 4, 107, 3);
        }

        [Test]
        public void AlignedPointsOppositeToOutAndKeepsInLength()
        {
            var inH = new Vector2[] { new(0, 0), new(0, 10), new(0, 0) };   // length 10, own direction ignored
            var outH = new Vector2[] { new(0, 0), new(3, 4), new(0, 0) };   // length 5
            var (_, pts) = Flat(Build(anchors, inH, outH, new[] { HandleMode.Aligned }, false));
            // in = -out / |out| * |in| = (-6, -8)
            AssertPoint(pts, 2, 94, -8);
            AssertPoint(pts, 4, 103, 4);
        }

        [Test]
        public void AlignedWithZeroOutKeepsIn()
        {
            var inH = new Vector2[] { new(0, 0), new(-10, 5), new(0, 0) };
            var (_, pts) = Flat(Build(anchors, inH, none, new[] { HandleMode.Aligned }, false));
            AssertPoint(pts, 2, 90, 5);
            Assert.That(pts, Has.None.Matches<float>(float.IsNaN));
        }

        [Test]
        public void ModesAreCyclic()
        {
            var outH = new Vector2[] { new(5, 0), new(5, 0), new(5, 0) };
            var inH = new Vector2[] { new(1, 1), new(1, 1), new(1, 1) };
            // one entry: anchor 1 and 2 are Mirrored
            var (_, pts) = Flat(Build(anchors, inH, outH, new[] { HandleMode.Mirrored }, false));
            AssertPoint(pts, 2, 95, 0);
            AssertPoint(pts, 5, 95, 100);
            // two entries: anchor 1 takes entry 1 (Free), anchor 2 takes entry 0 (Mirrored)
            var (_, pts2) = Flat(Build(anchors, inH, outH, new[] { HandleMode.Mirrored, HandleMode.Free }, false));
            AssertPoint(pts2, 2, 101, 1);
            AssertPoint(pts2, 5, 95, 100);
        }

        [Test]
        public void MissingHandlesAreCorners()
        {
            var outH = new Vector2[] { new(20, 0) };
            var (_, pts) = Flat(Build(anchors, none, outH, noModes, false));
            AssertPoint(pts, 1, 20, 0);
            AssertPoint(pts, 2, 100, 0);   // no in handle
            AssertPoint(pts, 4, 100, 0);   // no out handle at anchor 1
            AssertPoint(pts, 5, 100, 100);
        }

        [Test]
        public void OpenCurveHasMovePlusCubics()
        {
            var (verbs, pts) = Flat(Build(anchors, none, none, noModes, false));
            Assert.That(verbs, Is.EqualTo(new[] { Protocol.Verb.Move, Protocol.Verb.Cubic, Protocol.Verb.Cubic }));
            Assert.That(pts.Length / 2, Is.EqualTo(1 + 3 * 2));
        }

        [Test]
        public void ClosedCurveAddsSegmentToFirstAnchorAndClose()
        {
            var inH = new Vector2[] { new(-4, 0), new(0, 0), new(0, 0) };
            var outH = new Vector2[] { new(0, 0), new(0, 0), new(0, 8) };
            var (verbs, pts) = Flat(Build(anchors, inH, outH, noModes, true));
            Assert.That(verbs, Is.EqualTo(new[]
            {
                Protocol.Verb.Move, Protocol.Verb.Cubic, Protocol.Verb.Cubic, Protocol.Verb.Cubic, Protocol.Verb.Close,
            }));
            Assert.That(pts.Length / 2, Is.EqualTo(1 + 3 * 3));
            AssertPoint(pts, 7, 100, 108); // a2 + out2
            AssertPoint(pts, 8, -4, 0);    // a0 + in0
            AssertPoint(pts, 9, 0, 0);     // back to anchor 0
        }

        [Test]
        public void FewerThanTwoAnchorsReturnInputUnchanged()
        {
            var input = QtPath.Empty.MoveTo(1, 2).LineTo(3, 4);
            Assert.That(BezierGeometry.Build(input, Array.Empty<Vector2>(), none, none, noModes, false), Is.SameAs(input));
            Assert.That(BezierGeometry.Build(input, new Vector2[] { new(5, 5) }, none, none, noModes, true), Is.SameAs(input));
        }

        [Test]
        public void AppendsToInputPath()
        {
            var input = QtPath.Empty.MoveTo(1, 2).LineTo(3, 4);
            var (verbs, _) = Flat(BezierGeometry.Build(input, anchors, none, none, noModes, false));
            Assert.That(verbs.Length, Is.EqualTo(2 + 3));
        }
    }
}
