using System;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a contour through a list of points, optionally closed (Skia AddPoly).</summary>
    [ProcessNode(Name = "AddPoly", Category = Categories.Paths)]
    public sealed class AddPolyNode
    {
        internal readonly record struct Props(Spread<Vector2> Points, bool Closed)
        {
            public bool Equals(Props other)
                => Closed == other.Closed && SpreadEquality.SameContent(Points, other.Points);

            public override int GetHashCode() => HashCode.Combine(Points.Count, Closed);
        }

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => Build(input ?? QtPath.Empty, p.Points, p.Closed);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a contour that moves to the first point and runs through the others with lines.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="points">The corners of the contour in units. Nothing or an empty spread adds nothing.</param>
        /// <param name="closed">If true, the contour ends with a line back to the first point.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Spread<Vector2>? points, bool closed)
            => _memo.Get(input, new Props(points ?? Spread<Vector2>.Empty, closed), s_create);

        private static QtPath Build(QtPath path, Spread<Vector2> points, bool closed)
        {
            if (points.Count == 0)
                return path;
            path = path.MoveTo(points[0].X, points[0].Y);
            for (var i = 1; i < points.Count; i++)
                path = path.LineTo(points[i].X, points[i].Y);
            return closed ? path.Close() : path;
        }
    }
}
