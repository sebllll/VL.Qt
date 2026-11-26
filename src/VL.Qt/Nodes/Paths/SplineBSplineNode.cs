using System;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a uniform cubic B-spline that follows a control polygon, as exact cubic Béziers.</summary>
    [ProcessNode(Name = "Spline (BSpline)", Category = Categories.Paths)]
    public sealed class SplineBSplineNode
    {
        internal readonly record struct Props(Spread<Vector2> Points, bool Closed)
        {
            public bool Equals(Props other)
                => Closed == other.Closed && SpreadEquality.SameContent(Points, other.Points);

            public override int GetHashCode() => HashCode.Combine(Points.Count, Closed);
        }

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => SplineGeometry.BSpline(input ?? QtPath.Empty, p.Points.AsSpan(), p.Closed);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a smooth curve that follows the control polygon and does not pass through its inner points.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="points">The control polygon in units. An open curve starts at the first and ends at the last point. Fewer than two
        /// points add nothing.</param>
        /// <param name="closed">If true, the curve is a closed loop around the control polygon.</param>
        /// <returns>The path with the spline added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Spread<Vector2>? points, bool closed)
            => _memo.Get(input, new Props(points ?? Spread<Vector2>.Empty, closed), s_create);
    }
}
