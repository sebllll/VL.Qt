using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a Catmull-Rom spline that passes through every point, as exact cubic Béziers.</summary>
    [ProcessNode(Name = "Spline (CatmullRom)", Category = Categories.Paths)]
    public sealed class SplineCatmullRomNode
    {
        internal readonly record struct Props(Spread<Vector2> Points, bool Closed, float Alpha, float Tension)
        {
            public bool Equals(Props other)
                => Closed == other.Closed && Alpha == other.Alpha && Tension == other.Tension && SpreadEquality.SameContent(Points, other.Points);

            public override int GetHashCode() => HashCode.Combine(Points.Count, Closed, Alpha, Tension);
        }

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => SplineGeometry.CatmullRom(input ?? QtPath.Empty, p.Points.AsSpan(), p.Closed, p.Alpha, p.Tension);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a smooth curve that runs through all points.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="points">The points the curve passes through, in units. Fewer than two points add nothing.</param>
        /// <param name="closed">If true, the curve returns smoothly from the last point to the first.</param>
        /// <param name="alpha">The parametrization: 0 = uniform, 0.5 = centripetal (no loops or cusps), 1 = chordal; clamped to 0..1.</param>
        /// <param name="tension">How tight the curve is: 0 = smooth, 1 = straight lines between the points; clamped to 0..1.</param>
        /// <returns>The path with the spline added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            Spread<Vector2>? points,
            bool closed,
            [DefaultValue(0.5f)] float alpha,
            float tension)
            => _memo.Get(input, new Props(points ?? Spread<Vector2>.Empty, closed, alpha, tension), s_create);
    }
}
