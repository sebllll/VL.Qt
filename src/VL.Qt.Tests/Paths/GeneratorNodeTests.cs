using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Nodes;
using VL.Qt.Paths;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Paths
{
    public class GeneratorNodeTests
    {
        private static Spread<Vector2> Points(params (float x, float y)[] p)
        {
            var b = new SpreadBuilder<Vector2>();
            foreach (var (x, y) in p)
                b.Add(new Vector2(x, y));
            return b.ToSpread();
        }

        private static Spread<HandleMode> Modes(params HandleMode[] m) => Spread.Create(m);

        private static readonly (float, float)[] s_pts = { (0, 0), (10, 5), (20, -5), (30, 0) };

        // ---- Bezier (Handles) ----

        [Test]
        public void Bezier_ChangeDetection()
        {
            var node = new BezierHandlesNode();
            var input = QtPath.Empty.LineTo(1, 1);
            var anchors = Points(s_pts);
            var inH = Points((-2, 0), (-2, 0), (-2, 0), (-2, 0));
            var outH = Points((2, 0), (2, 0), (2, 0), (2, 0));
            var a = node.Update(input, anchors, inH, outH, false, null);

            Assert.That(node.Update(input, anchors, inH, outH, false, null), Is.SameAs(a));
            // New spread references with the same content.
            Assert.That(node.Update(input, Points(s_pts), Points((-2, 0), (-2, 0), (-2, 0), (-2, 0)), Points((2, 0), (2, 0), (2, 0), (2, 0)), false, null), Is.SameAs(a));
            Assert.That(node.Update(input, anchors, inH, outH, false, Spread<HandleMode>.Empty), Is.SameAs(a));

            var closed = node.Update(input, anchors, inH, outH, true, null);
            Assert.That(closed, Is.Not.SameAs(a));
            var mirrored = node.Update(input, anchors, inH, outH, true, Modes(HandleMode.Mirrored));
            Assert.That(mirrored, Is.Not.SameAs(closed));
            Assert.That(node.Update(input, anchors, inH, outH, true, Modes(HandleMode.Mirrored)), Is.SameAs(mirrored));
            Assert.That(node.Update(input, anchors, inH, Points((3, 0), (2, 0), (2, 0), (2, 0)), true, Modes(HandleMode.Mirrored)), Is.Not.SameAs(mirrored));
            Assert.That(node.Update(QtPath.Empty.LineTo(1, 1), anchors, inH, outH, false, null), Is.Not.SameAs(a));
        }

        [Test]
        public void Bezier_FewerThanTwoAnchors_ReturnsInput()
        {
            var node = new BezierHandlesNode();
            var input = QtPath.Empty.LineTo(1, 1);
            Assert.That(node.Update(input, Points((5, 5)), null, null, false, null), Is.SameAs(input));
            Assert.That(node.Update(input, null, null, null, false, null), Is.SameAs(input));
        }

        [Test]
        public void Bezier_NullInputIsEmptyPath_DefaultsAreOpenAndFree()
        {
            var node = new BezierHandlesNode();
            var path = node.Update(null, Points((0, 0), (10, 0)), Points((0, 0), (-1, 0)), Points((1, 0), (0, 0)), false, null);
            Assert.That(path.Flatten().Verbs, Is.EqualTo(new[] { Protocol.Verb.Move, Protocol.Verb.Cubic }));
            Assert.That(path.Flatten().Points, Is.EqualTo(new[] { 0f, 0f, 1f, 0f, 9f, 0f, 10f, 0f }));
        }

        // ---- Spline (CatmullRom) ----

        [Test]
        public void CatmullRom_ChangeDetection()
        {
            var node = new SplineCatmullRomNode();
            var input = QtPath.Empty.LineTo(1, 1);
            var pts = Points(s_pts);
            var a = node.Update(input, pts, false, 0.5f, 0f);

            Assert.That(node.Update(input, pts, false, 0.5f, 0f), Is.SameAs(a));
            Assert.That(node.Update(input, Points(s_pts), false, 0.5f, 0f), Is.SameAs(a));
            Assert.That(node.Update(input, pts, false, 0.5f, 0f), Is.SameAs(a));
            Assert.That(node.Update(input, pts, true, 0.5f, 0f), Is.Not.SameAs(a));
            Assert.That(node.Update(input, pts, false, 0f, 0f), Is.Not.SameAs(a));
            var b = node.Update(input, pts, false, 0f, 0.5f);
            Assert.That(node.Update(input, pts, false, 0f, 0.5f), Is.SameAs(b));
            Assert.That(node.Update(input, Points((0, 0), (10, 5), (20, -5), (31, 0)), false, 0f, 0.5f), Is.Not.SameAs(b));
            Assert.That(node.Update(QtPath.Empty.LineTo(1, 1), Points((0, 0), (10, 5), (20, -5), (31, 0)), false, 0f, 0.5f), Is.Not.SameAs(b));
        }

        [Test]
        public void CatmullRom_FewerThanTwoPoints_ReturnsInput()
        {
            var node = new SplineCatmullRomNode();
            var input = QtPath.Empty.LineTo(1, 1);
            Assert.That(node.Update(input, Points((5, 5)), false, 0.5f, 0f), Is.SameAs(input));
            Assert.That(node.Update(input, null, false, 0.5f, 0f), Is.SameAs(input));
        }

        [Test]
        public void CatmullRom_DefaultsAreOpenCentripetalNoTension()
        {
            var node = new SplineCatmullRomNode();
            var pts = Points(s_pts);
            var viaDefaults = node.Update(null, pts, false, 0.5f, 0f);
            var explicitPath = SplineGeometry.CatmullRom(QtPath.Empty, pts.AsSpan(), false, 0.5f, 0f);
            Assert.That(viaDefaults.ContentEquals(explicitPath), Is.True);
            Assert.That(viaDefaults.Flatten().Verbs, Has.Length.EqualTo(4)); // Move + 3 cubics
        }

        // ---- Spline (BSpline) ----

        [Test]
        public void BSpline_ChangeDetection()
        {
            var node = new SplineBSplineNode();
            var input = QtPath.Empty.LineTo(1, 1);
            var pts = Points(s_pts);
            var a = node.Update(input, pts, false);

            Assert.That(node.Update(input, pts, false), Is.SameAs(a));
            Assert.That(node.Update(input, Points(s_pts), false), Is.SameAs(a));
            var closed = node.Update(input, pts, true);
            Assert.That(closed, Is.Not.SameAs(a));
            Assert.That(node.Update(input, Points((0, 0), (10, 5), (20, -5), (31, 0)), true), Is.Not.SameAs(closed));
            Assert.That(node.Update(QtPath.Empty.LineTo(1, 1), pts, true), Is.Not.SameAs(closed));
        }

        [Test]
        public void BSpline_FewerThanTwoPoints_ReturnsInput()
        {
            var node = new SplineBSplineNode();
            var input = QtPath.Empty.LineTo(1, 1);
            Assert.That(node.Update(input, Points((5, 5)), false), Is.SameAs(input));
            Assert.That(node.Update(input, null, false), Is.SameAs(input));
        }

        [Test]
        public void BSpline_OpenCurveIsClampedToEndPoints()
        {
            var path = new SplineBSplineNode().Update(null, Points(s_pts), false).Flatten();
            var p = path.Points;
            Assert.That((p[0], p[1]), Is.EqualTo((0f, 0f)));
            Assert.That((p[^2], p[^1]), Is.EqualTo((30f, 0f)));
        }
    }
}
