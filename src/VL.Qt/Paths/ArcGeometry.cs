using System;
using Stride.Core.Mathematics;

namespace VL.Qt.Paths
{
    /// <summary>
    /// Arcs, ovals and rounded rectangles as cubic Béziers for <see cref="QtPath"/> (QtNative knows only Move, Line,
    /// Quad, Cubic and Close). Every arc is split into segments of at most 90 degrees with the control distance
    /// <c>4/3 · tan(θ/4) · r</c>. Angles are in cycles (1 = full turn) like the angle pins of the Skia nodes; 0 points
    /// to the right and positive angles turn clockwise on screen (y down), like Skia.
    /// The methods return the path extended by the arc; the current point of a path is not stored, so the methods that
    /// start at it take it as a parameter.
    /// </summary>
    internal static class ArcGeometry
    {
        private const double quarterTurn = Math.PI / 2;
        private const double cycle = 2 * Math.PI;

        /// <summary>
        /// Skia <c>ArcTo (Oval)</c>: the arc of <paramref name="oval"/> from <paramref name="startAngle"/> for
        /// <paramref name="sweepAngle"/>. Connects to the start of the arc with a line, or starts a new contour if
        /// <paramref name="forceMoveTo"/> is set or the path is empty. A sweep of one cycle or more gives a full turn.
        /// </summary>
        internal static QtPath ArcTo(QtPath path, RectangleF oval, float startAngle, float sweepAngle, bool forceMoveTo)
        {
            var (cx, cy, rx, ry) = Center(oval);
            var t0 = startAngle * cycle;
            var dt = Math.Clamp(sweepAngle * cycle, -cycle, cycle);
            var (sx, sy) = (cx + rx * Math.Cos(t0), cy + ry * Math.Sin(t0));
            path = forceMoveTo || path.IsEmpty ? path.MoveTo((float)sx, (float)sy) : path.LineTo((float)sx, (float)sy);
            return Emit(path, cx, cy, rx, ry, 1, 0, t0, dt, false, 0, 0);
        }

        /// <summary>
        /// Skia <c>AddArc</c>: a new contour with the arc of <paramref name="oval"/>. A sweep of one cycle or more adds
        /// the closed oval, a sweep of zero or an empty oval adds nothing.
        /// </summary>
        internal static QtPath AddArc(QtPath path, RectangleF oval, float startAngle, float sweepAngle)
        {
            if (sweepAngle == 0 || oval.Width == 0 || oval.Height == 0)
                return path;
            if (Math.Abs(sweepAngle) >= 1)
                return AddOval(path, oval, sweepAngle > 0);
            return ArcTo(path, oval, startAngle, sweepAngle, true);
        }

        /// <summary>
        /// Skia <c>AddOval</c>: a closed contour around <paramref name="oval"/>, starting at the right middle and going
        /// clockwise (<paramref name="clockwise"/>) or counterclockwise.
        /// </summary>
        internal static QtPath AddOval(QtPath path, RectangleF oval, bool clockwise)
        {
            var (cx, cy, rx, ry) = Center(oval);
            path = path.MoveTo((float)(cx + rx), (float)cy);
            path = Emit(path, cx, cy, rx, ry, 1, 0, 0, clockwise ? cycle : -cycle, true, cx + rx, cy);
            return path.Close();
        }

        /// <summary>Skia <c>AddCircle</c>: <see cref="AddOval"/> of the square around the center with the radius.</summary>
        internal static QtPath AddCircle(QtPath path, float x, float y, float radius, bool clockwise) =>
            AddOval(path, new RectangleF(x - radius, y - radius, 2 * radius, 2 * radius), clockwise);

        /// <summary>
        /// Skia <c>AddRoundRect</c>: a closed rectangle with elliptical corners of the radii <paramref name="rx"/> and
        /// <paramref name="ry"/> (limited to half the width and half the height), starting after the top left corner and
        /// going clockwise (<paramref name="clockwise"/>) or counterclockwise. Radii of zero give a plain rectangle.
        /// </summary>
        internal static QtPath AddRoundRect(QtPath path, RectangleF rect, float rx, float ry, bool clockwise)
        {
            double l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
            if (l > r)
                (l, r) = (r, l);
            if (t > b)
                (t, b) = (b, t);
            double w = r - l, h = b - t;
            double erx = Math.Min(Math.Abs(rx), w / 2), ery = Math.Min(Math.Abs(ry), h / 2);
            if (erx <= 0 || ery <= 0)
            {
                path = path.MoveTo((float)l, (float)t);
                path = clockwise
                    ? path.LineTo((float)r, (float)t).LineTo((float)r, (float)b).LineTo((float)l, (float)b)
                    : path.LineTo((float)l, (float)b).LineTo((float)r, (float)b).LineTo((float)r, (float)t);
                return path.Close();
            }

            // Corner centers: top left, top right, bottom right, bottom left.
            var c = new (double x, double y)[] { (l + erx, t + ery), (r - erx, t + ery), (r - erx, b - ery), (l + erx, b - ery) };
            // Corner by corner, each a quarter turn. Clockwise from the top edge: top right, bottom right, bottom left,
            // top left; counterclockwise: top left, bottom left, bottom right, top right. Lines along the edges join them.
            var dir = clockwise ? 1 : -1;
            path = path.MoveTo((float)(l + erx), (float)t);
            for (var i = 0; i < 4; i++)
            {
                var k = clockwise ? (i + 1) % 4 : (4 - i) % 4;
                var t0 = -quarterTurn + dir * i * quarterTurn;
                // Counterclockwise the first corner starts at the current point.
                if (clockwise || i > 0)
                    path = path.LineTo((float)(c[k].x + erx * Math.Cos(t0)), (float)(c[k].y + ery * Math.Sin(t0)));
                path = Emit(path, c[k].x, c[k].y, erx, ery, 1, 0, t0, dir * quarterTurn, false, 0, 0);
            }

            return path.Close();
        }

        /// <summary>
        /// Skia <c>ArcTo (Radius)</c> / SVG elliptical arc: from <paramref name="current"/> to <paramref name="end"/> on
        /// an ellipse with the radii <paramref name="radius"/>, turned by <paramref name="xAxisRotate"/> (cycles).
        /// <paramref name="largeArc"/> picks the arc of more than half a turn, <paramref name="sweep"/> the clockwise one.
        /// Radii that are too small are scaled up (SVG rule); a radius of zero gives a line, equal points give nothing.
        /// </summary>
        internal static QtPath SvgArcTo(QtPath path, Vector2 current, Vector2 radius, float xAxisRotate, bool largeArc, bool sweep, Vector2 end)
        {
            double x0 = current.X, y0 = current.Y, x1 = end.X, y1 = end.Y;
            if (x0 == x1 && y0 == y1)
                return path;
            double rx = Math.Abs(radius.X), ry = Math.Abs(radius.Y);
            if (rx == 0 || ry == 0)
                return path.LineTo(end.X, end.Y);

            var phi = xAxisRotate * cycle;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

            // SVG implementation notes F.6.5: endpoint to center parameterization.
            double dx = (x0 - x1) / 2, dy = (y0 - y1) / 2;
            double x1p = cosPhi * dx + sinPhi * dy, y1p = -sinPhi * dx + cosPhi * dy;
            var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lambda > 1)
            {
                var s = Math.Sqrt(lambda);
                rx *= s;
                ry *= s;
            }

            var rx2 = rx * rx;
            var ry2 = ry * ry;
            var num = rx2 * ry2 - rx2 * y1p * y1p - ry2 * x1p * x1p;
            var den = rx2 * y1p * y1p + ry2 * x1p * x1p;
            // Scaling can leave a tiny negative value; the center is then exactly the midpoint.
            var coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (largeArc == sweep)
                coef = -coef;
            double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
            double cx = cosPhi * cxp - sinPhi * cyp + (x0 + x1) / 2;
            double cy = sinPhi * cxp + cosPhi * cyp + (y0 + y1) / 2;

            var t0 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
            var t1 = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
            var dt = t1 - t0;
            if (sweep && dt < 0)
                dt += cycle;
            else if (!sweep && dt > 0)
                dt -= cycle;

            return Emit(path, cx, cy, rx, ry, cosPhi, sinPhi, t0, dt, true, x1, y1);
        }

        /// <summary>
        /// Skia <c>ArcTo (Tangent)</c>: a line from <paramref name="current"/> towards <paramref name="corner"/>, rounded
        /// with a circle of <paramref name="radius"/> that touches the line to <paramref name="end"/>. The path ends where
        /// the circle touches that second line, not at <paramref name="end"/>. A radius of zero, equal points or points on
        /// one line give a line to <paramref name="corner"/>.
        /// </summary>
        internal static QtPath TangentArcTo(QtPath path, Vector2 current, Vector2 corner, Vector2 end, float radius)
        {
            double px = corner.X, py = corner.Y;
            double ax = current.X - px, ay = current.Y - py;
            double bx = end.X - px, by = end.Y - py;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (radius <= 0 || la == 0 || lb == 0)
                return path.LineTo(corner.X, corner.Y);
            ax /= la;
            ay /= la;
            bx /= lb;
            by /= lb;

            var cross = ax * by - ay * bx;
            var dot = ax * bx + ay * by;
            if (Math.Abs(cross) < 1e-9)
                return path.LineTo(corner.X, corner.Y);

            // Half of the angle at the corner; the tangent points lie d away from it on both lines.
            var half = Math.Atan2(Math.Abs(cross), dot) / 2;
            var d = radius / Math.Tan(half);
            double t0x = px + ax * d, t0y = py + ay * d;
            double t1x = px + bx * d, t1y = py + by * d;

            var mx = ax + bx;
            var my = ay + by;
            var lm = Math.Sqrt(mx * mx + my * my);
            var dc = radius / Math.Sin(half);
            double cx = px + mx / lm * dc, cy = py + my / lm * dc;

            var a0 = Math.Atan2(t0y - cy, t0x - cx);
            var a1 = Math.Atan2(t1y - cy, t1x - cx);
            var dt = a1 - a0;
            if (dt > Math.PI)
                dt -= cycle;
            else if (dt < -Math.PI)
                dt += cycle;

            path = path.LineTo((float)t0x, (float)t0y);
            return Emit(path, cx, cy, radius, radius, 1, 0, a0, dt, true, t1x, t1y);
        }

        private static (double cx, double cy, double rx, double ry) Center(RectangleF oval) =>
            (oval.X + oval.Width / 2.0, oval.Y + oval.Height / 2.0, Math.Abs(oval.Width) / 2.0, Math.Abs(oval.Height) / 2.0);

        /// <summary>
        /// Appends cubics for the ellipse arc around (cx, cy) with the radii rx, ry, rotated by the given cosine/sine,
        /// from the parameter angle t0 (radians) for dt. The current point must be the start of the arc. With
        /// <paramref name="snapEnd"/> the last point is exactly (ex, ey), so a chain does not drift.
        /// </summary>
        private static QtPath Emit(QtPath path, double cx, double cy, double rx, double ry, double cosPhi, double sinPhi,
            double t0, double dt, bool snapEnd, double ex, double ey)
        {
            if (dt == 0)
                return path;

            var n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / quarterTurn - 1e-9));
            var step = dt / n;
            var k = 4.0 / 3.0 * Math.Tan(step / 4);

            for (var i = 0; i < n; i++)
            {
                var a = t0 + i * step;
                var b = a + step;
                double cosA = Math.Cos(a), sinA = Math.Sin(a), cosB = Math.Cos(b), sinB = Math.Sin(b);

                // Points and tangents on the unscaled ellipse, then rotated and moved.
                double p1x = rx * (cosA - k * sinA), p1y = ry * (sinA + k * cosA);
                double p2x = rx * (cosB + k * sinB), p2y = ry * (sinB - k * cosB);
                double p3x = rx * cosB, p3y = ry * sinB;

                float c1x = (float)(cx + cosPhi * p1x - sinPhi * p1y);
                float c1y = (float)(cy + sinPhi * p1x + cosPhi * p1y);
                float c2x = (float)(cx + cosPhi * p2x - sinPhi * p2y);
                float c2y = (float)(cy + sinPhi * p2x + cosPhi * p2y);
                float x = (float)(cx + cosPhi * p3x - sinPhi * p3y);
                float y = (float)(cy + sinPhi * p3x + cosPhi * p3y);
                if (snapEnd && i == n - 1)
                {
                    x = (float)ex;
                    y = (float)ey;
                }

                path = path.CubicTo(c1x, c1y, c2x, c2y, x, y);
            }

            return path;
        }
    }
}
