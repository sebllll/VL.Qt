using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Paths;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Paths
{
    public class SplineGeometryTests
    {
        private const float eps = 1e-4f;

        private static readonly Vector2[] s_zigzag =
        {
            new Vector2(0, 0), new Vector2(100, 40), new Vector2(150, -30), new Vector2(260, 10), new Vector2(300, 150),
        };

        private readonly record struct Cubic(Vector2 P0, Vector2 P1, Vector2 P2, Vector2 P3);

        private static (byte[] verbs, List<Cubic> cubics, Vector2 move) Read(QtPath path)
        {
            var flat = path.Flatten();
            var pts = flat.Points;
            var cubics = new List<Cubic>();
            var move = default(Vector2);
            var cur = default(Vector2);
            var pi = 0;
            foreach (var verb in flat.Verbs)
            {
                switch (verb)
                {
                    case Protocol.Verb.Move:
                        cur = move = new Vector2(pts[pi], pts[pi + 1]);
                        pi += 2;
                        break;
                    case Protocol.Verb.Line:
                        cur = new Vector2(pts[pi], pts[pi + 1]);
                        pi += 2;
                        break;
                    case Protocol.Verb.Cubic:
                        var c = new Cubic(cur, new Vector2(pts[pi], pts[pi + 1]), new Vector2(pts[pi + 2], pts[pi + 3]), new Vector2(pts[pi + 4], pts[pi + 5]));
                        cubics.Add(c);
                        cur = c.P3;
                        pi += 6;
                        break;
                }
            }

            return (flat.Verbs.ToArray(), cubics, move);
        }

        private static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = eps)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(tolerance), $"x of {actual}");
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(tolerance), $"y of {actual}");
        }

        private static void AssertFinite(QtPath path)
        {
            foreach (var v in path.Flatten().Points)
                Assert.That(float.IsFinite(v), Is.True, "non-finite coordinate");
        }

        // Same direction: parallel and pointing the same way.
        private static void AssertSameDirection(Vector2 a, Vector2 b)
        {
            Assert.That(a.Length(), Is.GreaterThan(eps));
            Assert.That(b.Length(), Is.GreaterThan(eps));
            var na = Vector2.Normalize(a);
            var nb = Vector2.Normalize(b);
            Assert.That(Vector2.Dot(na, nb), Is.EqualTo(1).Within(1e-4), $"{a} vs {b}");
        }

        // Points on segment p-q (within tolerance).
        private static bool OnSegment(Vector2 p, Vector2 q, Vector2 x)
        {
            var d = q - p;
            var t = Vector2.Dot(x - p, d) / d.LengthSquared();
            return t >= -eps && t <= 1 + eps && (p + d * t - x).Length() < 1e-3f;
        }

        private static byte[] Verbs(params byte[] verbs) => verbs;

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        public void CatmullRom_PassesThroughAllPoints(float alpha)
        {
            var (verbs, cubics, move) = Read(SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, false, alpha, 0));
            Assert.That(verbs, Is.EqualTo(Verbs(Protocol.Verb.Move, Protocol.Verb.Cubic, Protocol.Verb.Cubic, Protocol.Verb.Cubic, Protocol.Verb.Cubic)));
            Assert.That(move, Is.EqualTo(s_zigzag[0]));
            for (var i = 0; i < cubics.Count; i++)
            {
                Assert.That(cubics[i].P0, Is.EqualTo(s_zigzag[i]));
                Assert.That(cubics[i].P3, Is.EqualTo(s_zigzag[i + 1]));
            }
        }

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        public void CatmullRom_IsSmoothAtInnerPoints(float alpha)
        {
            var (_, cubics, _) = Read(SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, false, alpha, 0.3f));
            for (var i = 1; i < cubics.Count; i++)
                AssertSameDirection(cubics[i - 1].P3 - cubics[i - 1].P2, cubics[i].P1 - cubics[i].P0);
        }

        [Test]
        public void CatmullRom_UniformMatchesClassicTangent()
        {
            var (_, cubics, _) = Read(SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, false, 0, 0));
            // Inner tangent (P[i+1] − P[i−1]) / 2, control point at a third of it.
            AssertNear(s_zigzag[1] + (s_zigzag[2] - s_zigzag[0]) / 6, cubics[1].P1);
            AssertNear(s_zigzag[2] - (s_zigzag[3] - s_zigzag[1]) / 6, cubics[1].P2);
        }

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        public void CatmullRom_OpenEndsHaveTangentZero(float alpha)
        {
            var (_, cubics, _) = Read(SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, false, alpha, 0));
            TestContext.Out.WriteLine($"alpha {alpha}: start {cubics[0].P0}, first control {cubics[0].P1}, last control {cubics[^1].P2}, end {cubics[^1].P3}");
            Assert.That(cubics[0].P1, Is.EqualTo(cubics[0].P0));
            Assert.That(cubics[^1].P2, Is.EqualTo(cubics[^1].P3));
        }

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        public void CatmullRom_TensionOneGivesStraightLines(float alpha)
        {
            var (_, cubics, _) = Read(SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, true, alpha, 1));
            Assert.That(cubics, Has.Count.EqualTo(s_zigzag.Length));
            foreach (var c in cubics)
            {
                Assert.That(OnSegment(c.P0, c.P3, c.P1), Is.True, $"{c}");
                Assert.That(OnSegment(c.P0, c.P3, c.P2), Is.True, $"{c}");
            }
        }

        [TestCase(2f, 1f)]
        [TestCase(-1f, 0f)]
        [TestCase(float.NaN, 0f)]
        public void CatmullRom_TensionIsClampedToZeroToOne(float tension, float clamped)
        {
            var actual = SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, false, 0.5f, tension);
            var expected = SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, false, 0.5f, clamped);
            Assert.That(actual.ContentEquals(expected), Is.True);
            foreach (var f in actual.Flatten().Points)
                Assert.That(float.IsFinite(f), Is.True);
        }

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        public void CatmullRom_ClosedIsContinuousAtTheSeam(float alpha)
        {
            var path = SplineGeometry.CatmullRom(QtPath.Empty, s_zigzag, true, alpha, 0);
            var (verbs, cubics, move) = Read(path);
            Assert.That(verbs.Length, Is.EqualTo(1 + s_zigzag.Length + 1));
            Assert.That(verbs[^1], Is.EqualTo(Protocol.Verb.Close));
            Assert.That(cubics[^1].P3, Is.EqualTo(move));
            Assert.That(move, Is.EqualTo(s_zigzag[0]));
            AssertSameDirection(cubics[^1].P3 - cubics[^1].P2, cubics[0].P1 - cubics[0].P0);
        }

        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(1f)]
        public void CatmullRom_CoincidentPointsGiveNoNaN(float alpha)
        {
            var points = new[]
            {
                new Vector2(0, 0), new Vector2(0, 0), new Vector2(50, 50), new Vector2(50, 50), new Vector2(50, 50), new Vector2(100, 0), new Vector2(100, 0),
            };
            foreach (var closed in new[] { false, true })
            {
                var path = SplineGeometry.CatmullRom(QtPath.Empty, points, closed, alpha, 0);
                AssertFinite(path);
                var (verbs, cubics, _) = Read(path);
                // Zero-length spans are skipped: 0 → 50 → 100 (and back to 0 when closed).
                Assert.That(cubics, Has.Count.EqualTo(closed ? 3 : 2));
                Assert.That(verbs[^1], Is.EqualTo(closed ? Protocol.Verb.Close : Protocol.Verb.Cubic));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AllPointsEqual_GiveNoNaN(bool closed)
        {
            var points = new[] { new Vector2(5, 5), new Vector2(5, 5), new Vector2(5, 5), new Vector2(5, 5), new Vector2(5, 5) };
            Assert.That(SplineGeometry.CatmullRom(QtPath.Empty, points, closed, 0.5f, 0), Is.SameAs(QtPath.Empty));
            var bspline = SplineGeometry.BSpline(QtPath.Empty, points, closed);
            AssertFinite(bspline);
            foreach (var c in Read(bspline).cubics)
                AssertNear(new Vector2(5, 5), c.P3);
        }

        [Test]
        public void TwoPoints()
        {
            Vector2 a = new Vector2(10, 20), b = new Vector2(110, 70);
            var (verbs, cubics, move) = Read(SplineGeometry.CatmullRom(QtPath.Empty, new[] { a, b }, false, 0.5f, 0));
            Assert.That(verbs, Is.EqualTo(Verbs(Protocol.Verb.Move, Protocol.Verb.Cubic)));
            Assert.That(move, Is.EqualTo(a));
            Assert.That(cubics[0].P3, Is.EqualTo(b));
            Assert.That(OnSegment(a, b, cubics[0].P1) && OnSegment(a, b, cubics[0].P2), Is.True);

            (verbs, cubics, move) = Read(SplineGeometry.BSpline(QtPath.Empty, new[] { a, b }, false));
            Assert.That(verbs, Is.EqualTo(Verbs(Protocol.Verb.Move, Protocol.Verb.Cubic)));
            Assert.That(move, Is.EqualTo(a));
            Assert.That(cubics[0].P3, Is.EqualTo(b));
            Assert.That(OnSegment(a, b, cubics[0].P1) && OnSegment(a, b, cubics[0].P2), Is.True);

            foreach (var closed in new[] { false, true })
            {
                AssertFinite(SplineGeometry.CatmullRom(QtPath.Empty, new[] { a, b }, closed, 0.5f, 0));
                AssertFinite(SplineGeometry.BSpline(QtPath.Empty, new[] { a, b }, closed));
            }
        }

        [Test]
        public void FewerThanTwoPoints_ReturnTheInput()
        {
            var input = QtPath.Empty.MoveTo(1, 2).LineTo(3, 4);
            foreach (var points in new[] { Array.Empty<Vector2>(), new[] { new Vector2(7, 8) } })
                foreach (var closed in new[] { false, true })
                {
                    Assert.That(SplineGeometry.CatmullRom(input, points, closed, 0.5f, 0), Is.SameAs(input));
                    Assert.That(SplineGeometry.BSpline(input, points, closed), Is.SameAs(input));
                }
        }

        [Test]
        public void AppendsANewContour()
        {
            var input = QtPath.Empty.MoveTo(1, 2).LineTo(3, 4);
            var verbs = Read(SplineGeometry.BSpline(input, s_zigzag, false)).verbs;
            Assert.That(verbs.Take(3), Is.EqualTo(Verbs(Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Move)));
            verbs = Read(SplineGeometry.CatmullRom(input, s_zigzag, false, 0.5f, 0)).verbs;
            Assert.That(verbs.Take(3), Is.EqualTo(Verbs(Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Move)));
        }

        [Test]
        public void BSpline_OpenIsClampedToTheEndPoints()
        {
            var (verbs, cubics, move) = Read(SplineGeometry.BSpline(QtPath.Empty, s_zigzag, false));
            // n − 3 cubics.
            Assert.That(verbs, Is.EqualTo(Verbs(Protocol.Verb.Move, Protocol.Verb.Cubic, Protocol.Verb.Cubic)));
            Assert.That(move, Is.EqualTo(s_zigzag[0]));
            Assert.That(cubics[^1].P3, Is.EqualTo(s_zigzag[^1]));
            // Tangent to the first and last edge of the control polygon.
            AssertSameDirection(s_zigzag[1] - s_zigzag[0], cubics[0].P1 - cubics[0].P0);
            AssertSameDirection(s_zigzag[^1] - s_zigzag[^2], cubics[^1].P3 - cubics[^1].P2);
        }

        [Test]
        public void BSpline_FourPointsAreOneBezier()
        {
            var points = s_zigzag.Take(4).ToArray();
            var (_, cubics, _) = Read(SplineGeometry.BSpline(QtPath.Empty, points, false));
            Assert.That(cubics, Has.Count.EqualTo(1));
            AssertNear(points[1], cubics[0].P1);
            AssertNear(points[2], cubics[0].P2);
            Assert.That(cubics[0].P3, Is.EqualTo(points[3]));
        }

        [Test]
        public void BSpline_ThreePointsAreAQuadratic()
        {
            var points = s_zigzag.Take(3).ToArray();
            var (_, cubics, _) = Read(SplineGeometry.BSpline(QtPath.Empty, points, false));
            Assert.That(cubics, Has.Count.EqualTo(1));
            AssertNear(points[0] + (points[1] - points[0]) * 2 / 3, cubics[0].P1);
            AssertNear(points[2] + (points[1] - points[2]) * 2 / 3, cubics[0].P2);
        }

        [Test]
        public void BSpline_ClampedMatchesKnownSegments()
        {
            // Knots 0,0,0,0,1,2,2,2,2: segment 0 has the Bézier points P0, P1, (P1 + P2)/2, (P1 + 2·P2 + P3)/4 … checked
            // through C1 continuity at the join and the exact first control points.
            var p = s_zigzag;
            var (_, cubics, _) = Read(SplineGeometry.BSpline(QtPath.Empty, p, false));
            AssertNear(p[1], cubics[0].P1);
            AssertNear((p[1] + p[2]) / 2, cubics[0].P2);
            AssertNear((p[1] + 2 * p[2] + p[3]) / 4, cubics[0].P3);
            AssertNear((p[2] + p[3]) / 2, cubics[1].P1);
            AssertNear(p[3], cubics[1].P2);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BSpline_IsC1AtTheJoins(bool closed)
        {
            var points = s_zigzag.Concat(new[] { new Vector2(200, 200), new Vector2(80, 180) }).ToArray();
            var (_, cubics, _) = Read(SplineGeometry.BSpline(QtPath.Empty, points, closed));
            var count = closed ? cubics.Count : cubics.Count - 1;
            // Equal knot intervals inside, so the derivative is equal on both sides of a join (also at the seam).
            for (var i = 0; i < count; i++)
            {
                var a = cubics[i];
                var b = cubics[(i + 1) % cubics.Count];
                AssertNear(a.P3, b.P0, 1e-3f);
                AssertNear(a.P3 - a.P2, b.P1 - b.P0, 1e-3f);
            }
        }

        [Test]
        public void BSpline_ClosedVerbs()
        {
            var (verbs, cubics, move) = Read(SplineGeometry.BSpline(QtPath.Empty, s_zigzag, true));
            Assert.That(verbs.Length, Is.EqualTo(1 + s_zigzag.Length + 1));
            Assert.That(verbs[0], Is.EqualTo(Protocol.Verb.Move));
            Assert.That(verbs[^1], Is.EqualTo(Protocol.Verb.Close));
            Assert.That(cubics[^1].P3, Is.EqualTo(move));
            AssertNear((s_zigzag[^1] + 4 * s_zigzag[0] + s_zigzag[1]) / 6, move);
        }
    }
}
