using System;
using System.Collections.Generic;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Paths;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Paths
{
    public class ArcGeometryTests
    {
        private const double maxRadialError = 0.0003; // 0.03 % of the radius
        private const int samplesPerCubic = 64;

        // Ellipse in its own frame: center, radii, rotation. Radial error is measured in the unit circle of that frame,
        // so it is relative to the radius for circles and ellipses alike.
        private readonly record struct Ell(double Cx, double Cy, double Rx, double Ry, double Rot = 0);

        private static double Radial(Ell e, double x, double y)
        {
            double dx = x - e.Cx, dy = y - e.Cy;
            var c = Math.Cos(e.Rot);
            var s = Math.Sin(e.Rot);
            var u = (c * dx + s * dy) / e.Rx;
            var v = (-s * dx + c * dy) / e.Ry;
            return Math.Sqrt(u * u + v * v) - 1;
        }

        // Exact cubic segments of a path: start point and 3 points each.
        private static List<(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)> Cubics(QtPath path)
        {
            var flat = path.Flatten();
            var result = new List<(Vector2, Vector2, Vector2, Vector2)>();
            var pts = flat.Points;
            var pi = 0;
            var cur = default(Vector2);
            foreach (var verb in flat.Verbs)
            {
                switch (verb)
                {
                    case Protocol.Verb.Move:
                    case Protocol.Verb.Line:
                        cur = new Vector2(pts[pi], pts[pi + 1]);
                        pi += 2;
                        break;
                    case Protocol.Verb.Quad:
                        cur = new Vector2(pts[pi + 2], pts[pi + 3]);
                        pi += 4;
                        break;
                    case Protocol.Verb.Cubic:
                        var p1 = new Vector2(pts[pi], pts[pi + 1]);
                        var p2 = new Vector2(pts[pi + 2], pts[pi + 3]);
                        var p3 = new Vector2(pts[pi + 4], pts[pi + 5]);
                        result.Add((cur, p1, p2, p3));
                        cur = p3;
                        pi += 6;
                        break;
                }
            }

            return result;
        }

        private static Vector2 At((Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3) c, double t)
        {
            var u = 1 - t;
            var x = u * u * u * c.p0.X + 3 * u * u * t * c.p1.X + 3 * u * t * t * c.p2.X + t * t * t * c.p3.X;
            var y = u * u * u * c.p0.Y + 3 * u * u * t * c.p1.Y + 3 * u * t * t * c.p2.Y + t * t * t * c.p3.Y;
            return new Vector2((float)x, (float)y);
        }

        private static double MaxError(QtPath path, Ell e)
        {
            var max = 0.0;
            foreach (var c in Cubics(path))
                for (var i = 0; i <= samplesPerCubic; i++)
                {
                    var p = At(c, i / (double)samplesPerCubic);
                    max = Math.Max(max, Math.Abs(Radial(e, p.X, p.Y)));
                }

            return max;
        }

        private static Vector2 LastPoint(QtPath path)
        {
            var flat = path.Flatten();
            var n = flat.Points.Length;
            return new Vector2(flat.Points[n - 2], flat.Points[n - 1]);
        }

        private static void AssertVerbs(byte[] expected, System.Collections.Immutable.ImmutableArray<byte> actual) =>
            Assert.That(System.Linq.Enumerable.ToArray(actual), Is.EqualTo(expected));

        private static int CubicCount(QtPath path)
        {
            var n = 0;
            foreach (var v in path.Flatten().Verbs)
                if (v == Protocol.Verb.Cubic)
                    n++;
            return n;
        }

        private static void AssertFinite(QtPath path)
        {
            foreach (var f in path.Flatten().Points)
                Assert.That(float.IsFinite(f), Is.True);
        }

        private static readonly RectangleF Circle10 = new RectangleF(-10, -10, 20, 20);

        [TestCase(0f, 0.25f, 1)]
        [TestCase(0.1f, 0.25f, 1)]
        [TestCase(0f, 0.5f, 2)]
        [TestCase(0.3f, 0.5f, 2)]
        [TestCase(0.05f, 0.9f, 4)]
        [TestCase(0f, 1f, 4)]
        [TestCase(0.2f, -0.25f, 1)]
        [TestCase(0f, -0.5f, 2)]
        [TestCase(0.7f, -1f, 4)]
        [TestCase(0f, 0.01f, 1)]
        public void ArcOfCircle_IsAccurateAndSplitInQuarterTurns(float start, float sweep, int segments)
        {
            var path = ArcGeometry.AddArc(QtPath.Empty, Circle10, start, sweep);
            if (Math.Abs(sweep) >= 1)
                segments = 4;

            Assert.That(CubicCount(path), Is.EqualTo(segments));
            Assert.That(MaxError(path, new Ell(0, 0, 10, 10)), Is.LessThan(maxRadialError));

            // Start and end lie where the angles say (angle 0 = right, positive = clockwise on screen). A full turn
            // is added as an oval, which always starts at the right.
            if (Math.Abs(sweep) >= 1)
                return;
            var flat = path.Flatten();
            var a0 = start * 2 * Math.PI;
            Assert.That(flat.Points[0], Is.EqualTo((float)(10 * Math.Cos(a0))).Within(1e-4));
            Assert.That(flat.Points[1], Is.EqualTo((float)(10 * Math.Sin(a0))).Within(1e-4));
            var a1 = a0 + sweep * 2 * Math.PI;
            var end = LastPoint(path);
            Assert.That(end.X, Is.EqualTo((float)(10 * Math.Cos(a1))).Within(1e-3));
            Assert.That(end.Y, Is.EqualTo((float)(10 * Math.Sin(a1))).Within(1e-3));
        }

        [Test]
        public void ArcWithSweepOverOneCycle_IsClampedToFullTurn()
        {
            var path = ArcGeometry.ArcTo(QtPath.Empty, Circle10, 0, 2.5f, true);
            Assert.That(CubicCount(path), Is.EqualTo(4));
            Assert.That(MaxError(path, new Ell(0, 0, 10, 10)), Is.LessThan(maxRadialError));
        }

        [Test]
        public void ZeroSweep_AddsNothingOrOnlyTheStartPoint()
        {
            Assert.That(ArcGeometry.AddArc(QtPath.Empty, Circle10, 0.1f, 0), Is.SameAs(QtPath.Empty));

            var path = ArcGeometry.ArcTo(QtPath.Empty, Circle10, 0, 0, true);
            Assert.That(CubicCount(path), Is.EqualTo(0));
            AssertFinite(path);
        }

        [Test]
        public void ArcTo_ConnectsWithLineUnlessForceMoveTo()
        {
            var start = QtPath.Empty.MoveTo(-30, 0);

            var line = ArcGeometry.ArcTo(start, Circle10, 0, 0.25f, false);
            AssertVerbs(
                new[] { Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Cubic }, line.Flatten().Verbs);

            var move = ArcGeometry.ArcTo(start, Circle10, 0, 0.25f, true);
            AssertVerbs(
                new[] { Protocol.Verb.Move, Protocol.Verb.Move, Protocol.Verb.Cubic }, move.Flatten().Verbs);

            // An empty path has no current point: the arc starts a contour.
            var first = ArcGeometry.ArcTo(QtPath.Empty, Circle10, 0, 0.25f, false);
            Assert.That(first.Flatten().Verbs[0], Is.EqualTo(Protocol.Verb.Move));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AddCircle_IsClosedAndAccurate(bool clockwise)
        {
            var path = ArcGeometry.AddCircle(QtPath.Empty, 3, 4, 5, clockwise);
            var verbs = path.Flatten().Verbs;

            Assert.That(verbs.Length, Is.EqualTo(6));
            Assert.That(verbs[0], Is.EqualTo(Protocol.Verb.Move));
            Assert.That(verbs[5], Is.EqualTo(Protocol.Verb.Close));
            Assert.That(MaxError(path, new Ell(3, 4, 5, 5)), Is.LessThan(maxRadialError));

            // Starts at the right middle; the first quarter ends below (clockwise, y down) or above it.
            var pts = path.Flatten().Points;
            Assert.That(pts[0], Is.EqualTo(8f).Within(1e-5));
            Assert.That(pts[1], Is.EqualTo(4f).Within(1e-5));
            Assert.That(pts[7], Is.EqualTo(clockwise ? 9f : -1f).Within(1e-4));
            // The full turn ends exactly at its start.
            Assert.That(pts[^2], Is.EqualTo(8f));
            Assert.That(pts[^1], Is.EqualTo(4f));
        }

        [Test]
        public void AddOval_Ellipse_IsAccurateAndFillsItsBounds()
        {
            var path = ArcGeometry.AddOval(QtPath.Empty, new RectangleF(10, 20, 8, 4), true);

            Assert.That(MaxError(path, new Ell(14, 22, 4, 2)), Is.LessThan(maxRadialError));
            var b = path.Bounds;
            Assert.That(b.Left, Is.EqualTo(10f).Within(1e-4));
            Assert.That(b.Right, Is.EqualTo(18f).Within(1e-4));
            Assert.That(b.Top, Is.EqualTo(20f).Within(1e-4));
            Assert.That(b.Bottom, Is.EqualTo(24f).Within(1e-4));
        }

        [Test]
        public void AddArc_OnEllipse_UsesParametricAngles()
        {
            var path = ArcGeometry.AddArc(QtPath.Empty, new RectangleF(0, 0, 8, 4), 0.25f, 0.5f);
            Assert.That(MaxError(path, new Ell(4, 2, 4, 2)), Is.LessThan(maxRadialError));
            var pts = path.Flatten().Points;
            // 0.25 cycles = bottom of the ellipse.
            Assert.That(pts[0], Is.EqualTo(4f).Within(1e-4));
            Assert.That(pts[1], Is.EqualTo(4f).Within(1e-4));
            Assert.That(LastPoint(path).Y, Is.EqualTo(0f).Within(1e-4));
        }

        [Test]
        public void ZeroSizedOvals_GiveNoNaN()
        {
            AssertFinite(ArcGeometry.AddCircle(QtPath.Empty, 1, 2, 0, true));
            AssertFinite(ArcGeometry.AddOval(QtPath.Empty, new RectangleF(1, 2, 0, 5), false));
            AssertFinite(ArcGeometry.AddArc(QtPath.Empty, new RectangleF(1, 2, 0, 0), 0, 0.3f));
            AssertFinite(ArcGeometry.AddRoundRect(QtPath.Empty, new RectangleF(1, 2, 0, 0), 1, 1, true));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AddRoundRect_HasFourCornersAndFillsItsRect(bool clockwise)
        {
            var path = ArcGeometry.AddRoundRect(QtPath.Empty, new RectangleF(10, 20, 40, 20), 5, 3, clockwise);
            var flat = path.Flatten();

            // Move, four times (Line, Cubic), Close; the counterclockwise path skips the first, zero-length line.
            Assert.That(CubicCount(path), Is.EqualTo(4));
            Assert.That(flat.Verbs[0], Is.EqualTo(Protocol.Verb.Move));
            Assert.That(flat.Verbs[^1], Is.EqualTo(Protocol.Verb.Close));
            Assert.That(flat.Verbs.Length, Is.EqualTo(clockwise ? 10 : 9));

            var b = path.Bounds;
            Assert.That(b.Left, Is.EqualTo(10f).Within(1e-4));
            Assert.That(b.Right, Is.EqualTo(50f).Within(1e-4));
            Assert.That(b.Top, Is.EqualTo(20f).Within(1e-4));
            Assert.That(b.Bottom, Is.EqualTo(40f).Within(1e-4));

            // The start is after the top left corner.
            Assert.That(flat.Points[0], Is.EqualTo(15f).Within(1e-5));
            Assert.That(flat.Points[1], Is.EqualTo(20f).Within(1e-5));

            // Every corner arc is a quarter ellipse around its corner center.
            var centers = new[] { (15.0, 23.0), (45.0, 23.0), (45.0, 37.0), (15.0, 37.0) };
            foreach (var c in Cubics(path))
            {
                var mid = At(c, 0.5);
                var ok = false;
                foreach (var (cx, cy) in centers)
                    ok |= Math.Abs(Radial(new Ell(cx, cy, 5, 3), mid.X, mid.Y)) < maxRadialError;
                Assert.That(ok, Is.True, "corner arc off its ellipse");
            }
        }

        [Test]
        public void AddRoundRect_LimitsRadiiAndAllowsZero()
        {
            // Radii larger than half the size give a stadium/ellipse, the bounds stay the rect.
            var big = ArcGeometry.AddRoundRect(QtPath.Empty, new RectangleF(0, 0, 10, 4), 100, 100, true);
            var b = big.Bounds;
            Assert.That(b.Width, Is.EqualTo(10f).Within(1e-4));
            Assert.That(b.Height, Is.EqualTo(4f).Within(1e-4));
            Assert.That(MaxError(big, new Ell(5, 2, 5, 2)), Is.LessThan(maxRadialError));

            var plain = ArcGeometry.AddRoundRect(QtPath.Empty, new RectangleF(0, 0, 10, 4), 0, 3, true);
            AssertVerbs(
                new[] { Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Line, Protocol.Verb.Line, Protocol.Verb.Close },
                plain.Flatten().Verbs);
        }

        // Circle of radius 1 through (0,0) and (1,1): centers (0,1) (positive angle, small) or (1,0).
        [TestCase(false, true, 0.0, 1.0, 1)]
        [TestCase(false, false, 1.0, 0.0, 1)]
        [TestCase(true, true, 1.0, 0.0, 3)]
        [TestCase(true, false, 0.0, 1.0, 3)]
        public void SvgArcTo_PicksTheArcAndHitsTheEndPoint(bool largeArc, bool sweep, double cx, double cy, int segments)
        {
            var start = new Vector2(0, 0);
            var end = new Vector2(1, 1);
            var path = ArcGeometry.SvgArcTo(QtPath.Empty.MoveTo(0, 0), start, new Vector2(1, 1), 0, largeArc, sweep, end);

            Assert.That(CubicCount(path), Is.EqualTo(segments));
            Assert.That(MaxError(path, new Ell(cx, cy, 1, 1)), Is.LessThan(maxRadialError));
            Assert.That(LastPoint(path), Is.EqualTo(end));
        }

        [Test]
        public void SvgArcTo_ScalesUpTooSmallRadii()
        {
            var path = ArcGeometry.SvgArcTo(QtPath.Empty.MoveTo(0, 0), new Vector2(0, 0), new Vector2(0.1f, 0.1f), 0, false, true, new Vector2(2, 0));

            // The only circle that fits is the one with the end points as diameter: a half circle.
            Assert.That(CubicCount(path), Is.EqualTo(2));
            Assert.That(MaxError(path, new Ell(1, 0, 1, 1)), Is.LessThan(maxRadialError));
            Assert.That(LastPoint(path), Is.EqualTo(new Vector2(2, 0)));

            // Elliptic radii are scaled by the same factor, keeping their ratio.
            var ell = ArcGeometry.SvgArcTo(QtPath.Empty.MoveTo(0, 0), new Vector2(0, 0), new Vector2(0.2f, 0.1f), 0, false, true, new Vector2(2, 0));
            Assert.That(MaxError(ell, new Ell(1, 0, 1, 0.5)), Is.LessThan(maxRadialError));
        }

        [Test]
        public void SvgArcTo_RotatedEllipse_ReachesEndAndStaysOnTheEllipse()
        {
            // Ellipse around (5,5), radii 3 and 1.5, turned by 30 degrees; arc from parameter 20 to 140 degrees.
            const double rot = 30 * Math.PI / 180;
            static Vector2 P(double deg)
            {
                var t = deg * Math.PI / 180;
                double x = 3 * Math.Cos(t), y = 1.5 * Math.Sin(t);
                return new Vector2(
                    (float)(5 + Math.Cos(rot) * x - Math.Sin(rot) * y),
                    (float)(5 + Math.Sin(rot) * x + Math.Cos(rot) * y));
            }

            var start = P(20);
            var end = P(140);
            var path = ArcGeometry.SvgArcTo(QtPath.Empty.MoveTo(start.X, start.Y), start, new Vector2(3, 1.5f), 30f / 360f, false, true, end);

            Assert.That(CubicCount(path), Is.EqualTo(2));
            Assert.That(MaxError(path, new Ell(5, 5, 3, 1.5, rot)), Is.LessThan(maxRadialError));
            Assert.That(LastPoint(path), Is.EqualTo(end));
        }

        [Test]
        public void SvgArcTo_Degenerate_NoNaN()
        {
            var p = QtPath.Empty.MoveTo(1, 1);

            // Start = end: omitted.
            Assert.That(ArcGeometry.SvgArcTo(p, new Vector2(1, 1), new Vector2(2, 2), 0, true, true, new Vector2(1, 1)), Is.SameAs(p));

            // Radius zero: a line.
            var line = ArcGeometry.SvgArcTo(p, new Vector2(1, 1), new Vector2(0, 5), 0, false, true, new Vector2(4, 4));
            AssertVerbs(new[] { Protocol.Verb.Move, Protocol.Verb.Line }, line.Flatten().Verbs);

            // Nearly equal points and a huge radius stay finite.
            AssertFinite(ArcGeometry.SvgArcTo(p, new Vector2(1, 1), new Vector2(1e6f, 1e6f), 0.3f, true, false, new Vector2(1.00001f, 1)));
        }

        [Test]
        public void TangentArcTo_RoundsTheCorner()
        {
            // Right angle at (10,0), radius 2: line to (8,0), quarter circle around (8,2) to (10,2).
            var path = ArcGeometry.TangentArcTo(QtPath.Empty.MoveTo(0, 0), new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), 2);

            AssertVerbs(new[] { Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Cubic }, path.Flatten().Verbs);
            var pts = path.Flatten().Points;
            Assert.That(pts[2], Is.EqualTo(8f).Within(1e-4));
            Assert.That(pts[3], Is.EqualTo(0f).Within(1e-4));
            Assert.That(MaxError(path, new Ell(8, 2, 2, 2)), Is.LessThan(maxRadialError));
            Assert.That(LastPoint(path).X, Is.EqualTo(10f).Within(1e-4));
            Assert.That(LastPoint(path).Y, Is.EqualTo(2f).Within(1e-4));
        }

        [Test]
        public void TangentArcTo_ObtuseAndAcuteCorners_StayOnTheCircle()
        {
            // Corner of 60 degrees (acute) and 120 degrees (obtuse).
            foreach (var deg in new[] { 60.0, 120.0, 170.0 })
            {
                var a = deg * Math.PI / 180;
                var corner = new Vector2(10, 0);
                var end = new Vector2((float)(10 + 10 * Math.Cos(Math.PI - a)), (float)(10 * Math.Sin(Math.PI - a)));
                var path = ArcGeometry.TangentArcTo(QtPath.Empty.MoveTo(0, 0), new Vector2(0, 0), corner, end, 2);

                var pts = path.Flatten().Points;
                // Tangent points are the same distance from the corner, d = r / tan(angle / 2).
                var d = 2 / Math.Tan(a / 2);
                Assert.That(10 - pts[2], Is.EqualTo(d).Within(1e-3), deg.ToString());
                var tx = LastPoint(path);
                Assert.That(Math.Sqrt((tx.X - 10) * (tx.X - 10) + tx.Y * tx.Y), Is.EqualTo(d).Within(1e-3), deg.ToString());

                // Circle center is on the bisector, radius away from both lines: the first cubic point is at distance r.
                var cy = 2.0;
                var cx = 10 - d;
                Assert.That(MaxError(path, new Ell(cx, cy, 2, 2)), Is.LessThan(maxRadialError), deg.ToString());
            }
        }

        [Test]
        public void TangentArcTo_Degenerate_GivesLineToCorner()
        {
            var p = QtPath.Empty.MoveTo(0, 0);
            var collinear = ArcGeometry.TangentArcTo(p, new Vector2(0, 0), new Vector2(5, 0), new Vector2(10, 0), 2);
            var zero = ArcGeometry.TangentArcTo(p, new Vector2(0, 0), new Vector2(5, 0), new Vector2(5, 5), 0);
            var same = ArcGeometry.TangentArcTo(p, new Vector2(5, 0), new Vector2(5, 0), new Vector2(5, 5), 2);

            foreach (var path in new[] { collinear, zero, same })
            {
                AssertVerbs(new[] { Protocol.Verb.Move, Protocol.Verb.Line }, path.Flatten().Verbs);
                AssertFinite(path);
            }
        }
    }
}
