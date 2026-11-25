using System;
using Stride.Core.Mathematics;

namespace VL.Qt.Paths
{
    /// <summary>
    /// Splines through or along a list of points, converted exactly to cubic Béziers for <see cref="QtPath"/>. Both
    /// methods start a new contour on the given path and return the extended path; fewer than 2 points return the path
    /// unchanged. Closed curves end with a Close verb. Nothing is allocated besides the path segments.
    /// </summary>
    internal static class SplineGeometry
    {
        /// <summary>
        /// Catmull-Rom spline that passes through every point, one cubic per span. <paramref name="alpha"/> sets the knot
        /// spacing (0 uniform, 0.5 centripetal without loops or cusps, 1 chordal; clamped to 0..1),
        /// <paramref name="tension"/> scales the tangents by <c>1 − tension</c> (clamped to 0..1; 1 gives straight lines).
        /// A NaN alpha counts as 0.5 and a NaN tension as 0, the defaults of the node.
        /// Open ends duplicate the end point as phantom point; its knot interval is zero, so an open curve has tangent 0
        /// at its end points for every alpha (the control point next to an end point is that end point). Spans of length
        /// zero are skipped, and the neighbors of a span are the nearest points that differ from its ends, so coincident
        /// points never divide by zero. Points that all coincide return the path unchanged.
        /// </summary>
        internal static QtPath CatmullRom(QtPath path, ReadOnlySpan<Vector2> points, bool closed, float alpha, float tension)
        {
            var n = points.Length;
            if (n < 2 || !HasDistinct(points))
                return path;

            var a = Math.Clamp((double)alpha, 0, 1);
            if (double.IsNaN(a))
                a = 0.5;
            var scale = (1 - (double.IsNaN(tension) ? 0 : Math.Clamp((double)tension, 0, 1))) / 3;
            var spans = closed ? n : n - 1;

            path = path.MoveTo(points[0].X, points[0].Y);
            for (var i = 0; i < spans; i++)
            {
                var ib = i;
                var ic = (i + 1) % n;
                var b = points[ib];
                var c = points[ic];
                if (b == c)
                    continue;

                var tbc = Knot(b, c, a);
                double m1x = 0, m1y = 0, m2x = 0, m2y = 0;

                // Previous neighbor: the nearest point before b that differs from it.
                if (TryNeighbor(points, ib, -1, closed, out var pa))
                {
                    var tab = Knot(pa, b, a);
                    // Barry–Goldman tangent at b, scaled to the knot interval of the span.
                    m1x = c.X - b.X + tbc * ((b.X - pa.X) / tab - (c.X - pa.X) / (tab + tbc));
                    m1y = c.Y - b.Y + tbc * ((b.Y - pa.Y) / tab - (c.Y - pa.Y) / (tab + tbc));
                }

                // Next neighbor: the nearest point after c that differs from it.
                if (TryNeighbor(points, ic, 1, closed, out var pd))
                {
                    var tcd = Knot(c, pd, a);
                    m2x = c.X - b.X + tbc * ((pd.X - c.X) / tcd - (pd.X - b.X) / (tbc + tcd));
                    m2y = c.Y - b.Y + tbc * ((pd.Y - c.Y) / tcd - (pd.Y - b.Y) / (tbc + tcd));
                }

                path = path.CubicTo(
                    (float)(b.X + m1x * scale), (float)(b.Y + m1y * scale),
                    (float)(c.X - m2x * scale), (float)(c.Y - m2y * scale),
                    c.X, c.Y);
            }

            return closed ? path.Close() : path;
        }

        /// <summary>
        /// Uniform cubic B-spline along the control polygon <paramref name="points"/>; the curve does not pass through
        /// the inner points. Open: clamped, so it starts at the first and ends at the last point, tangent to the first
        /// and last edge of the polygon (3 points give a quadratic and 2 points a line, both as one cubic). Closed:
        /// periodic, one cubic per point, starting near the first point.
        /// </summary>
        internal static QtPath BSpline(QtPath path, ReadOnlySpan<Vector2> points, bool closed)
        {
            var n = points.Length;
            if (n < 2)
                return path;
            return closed ? Periodic(path, points) : Clamped(path, points);
        }

        private static QtPath Periodic(QtPath path, ReadOnlySpan<Vector2> points)
        {
            var n = points.Length;
            // Segment k runs between the points k and k + 1, controlled by k − 1 .. k + 2 (Böhm conversion).
            var start = PointAtKnot(points[n - 1], points[0], points[1]);
            path = path.MoveTo(start.X, start.Y);
            for (var k = 0; k < n; k++)
            {
                var p1 = points[k];
                var p2 = points[(k + 1) % n];
                var p3 = points[(k + 2) % n];
                var end = k == n - 1 ? start : PointAtKnot(p1, p2, p3);
                path = path.CubicTo(
                    (2 * p1.X + p2.X) / 3, (2 * p1.Y + p2.Y) / 3,
                    (p1.X + 2 * p2.X) / 3, (p1.Y + 2 * p2.Y) / 3,
                    end.X, end.Y);
            }

            return path.Close();
        }

        // (p0 + 4·p1 + p2) / 6: the point of a uniform cubic B-spline at the knot of p1.
        private static Vector2 PointAtKnot(Vector2 p0, Vector2 p1, Vector2 p2) =>
            new Vector2((p0.X + 4 * p1.X + p2.X) / 6, (p0.Y + 4 * p1.Y + p2.Y) / 6);

        private static QtPath Clamped(QtPath path, ReadOnlySpan<Vector2> points)
        {
            var n = points.Length;
            var first = points[0];
            var last = points[n - 1];
            path = path.MoveTo(first.X, first.Y);
            if (n == 2)
                return path.CubicTo(
                    (2 * first.X + last.X) / 3, (2 * first.Y + last.Y) / 3,
                    (first.X + 2 * last.X) / 3, (first.Y + 2 * last.Y) / 3,
                    last.X, last.Y);
            if (n == 3)
            {
                // Quadratic Bézier, raised to a cubic.
                var q = points[1];
                return path.CubicTo(
                    (first.X + 2 * q.X) / 3, (first.Y + 2 * q.Y) / 3,
                    (last.X + 2 * q.X) / 3, (last.Y + 2 * q.Y) / 3,
                    last.X, last.Y);
            }

            // Knots 0,0,0,0,1,2,…,m,m,m,m with m = n − 3 segments; segment s is the knot interval [s, s + 1]. Its Bézier
            // points are the blossoms f(s,s,s), f(s,s,s+1), f(s,s+1,s+1), f(s+1,s+1,s+1).
            var m = n - 3;
            for (var s = 0; s < m; s++)
            {
                var c1 = Blossom(points, s, s, s, s + 1);
                var c2 = Blossom(points, s, s, s + 1, s + 1);
                var end = s == m - 1 ? last : Blossom(points, s, s + 1, s + 1, s + 1);
                path = path.CubicTo(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
            }

            return path;
        }

        // Knot j of the clamped knot vector for n points.
        private static double ClampedKnot(int j, int n) => Math.Clamp(j - 3, 0, n - 3);

        /// <summary>
        /// The blossom (polar form) f(x1, x2, x3) of segment <paramref name="s"/> of the clamped spline: de Boor's
        /// algorithm with a different parameter per level. The segment uses the points s .. s + 3 and the knot interval
        /// [u(s+3), u(s+4)]; all denominators span that interval, so they are at least 1.
        /// </summary>
        private static Vector2 Blossom(ReadOnlySpan<Vector2> points, int s, double x1, double x2, double x3)
        {
            var n = points.Length;
            var i = s + 3;
            // Level 1: three points from four.
            double d0x = points[s].X, d0y = points[s].Y, d1x = points[s + 1].X, d1y = points[s + 1].Y;
            double d2x = points[s + 2].X, d2y = points[s + 2].Y, d3x = points[s + 3].X, d3y = points[s + 3].Y;
            Lerp(ref d0x, ref d0y, d1x, d1y, Weight(i - 2, 3, x1, n));
            Lerp(ref d1x, ref d1y, d2x, d2y, Weight(i - 1, 3, x1, n));
            Lerp(ref d2x, ref d2y, d3x, d3y, Weight(i, 3, x1, n));
            // Level 2: two points from three.
            Lerp(ref d0x, ref d0y, d1x, d1y, Weight(i - 1, 2, x2, n));
            Lerp(ref d1x, ref d1y, d2x, d2y, Weight(i, 2, x2, n));
            // Level 3: the point.
            Lerp(ref d0x, ref d0y, d1x, d1y, Weight(i, 1, x3, n));
            return new Vector2((float)d0x, (float)d0y);
        }

        // de Boor weight for the point j at a level whose knot span is u(j) .. u(j + span).
        private static double Weight(int j, int span, double x, int n)
        {
            var uj = ClampedKnot(j, n);
            return (x - uj) / (ClampedKnot(j + span, n) - uj);
        }

        private static void Lerp(ref double ax, ref double ay, double bx, double by, double t)
        {
            ax += (bx - ax) * t;
            ay += (by - ay) * t;
        }

        private static bool HasDistinct(ReadOnlySpan<Vector2> points)
        {
            for (var i = 1; i < points.Length; i++)
                if (points[i] != points[0])
                    return true;
            return false;
        }

        // Knot interval |q − p|^alpha (1 for alpha 0); p and q differ.
        private static double Knot(Vector2 p, Vector2 q, double alpha)
        {
            double dx = q.X - p.X, dy = q.Y - p.Y;
            return Math.Pow(dx * dx + dy * dy, alpha / 2);
        }

        /// <summary>
        /// The nearest point from <paramref name="index"/> in <paramref name="step"/> direction that differs from the
        /// point at <paramref name="index"/>, wrapping around if <paramref name="closed"/>. False at an open end.
        /// </summary>
        private static bool TryNeighbor(ReadOnlySpan<Vector2> points, int index, int step, bool closed, out Vector2 neighbor)
        {
            var n = points.Length;
            var p = points[index];
            var j = index;
            for (var k = 1; k < n; k++)
            {
                j += step;
                if (j < 0 || j >= n)
                {
                    if (!closed)
                        break;
                    j = (j + n) % n;
                }

                if (points[j] != p)
                {
                    neighbor = points[j];
                    return true;
                }
            }

            neighbor = p;
            return false;
        }
    }
}
