using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Qt.Paths;

namespace VL.Qt.Nodes.Paths
{
    /// <summary>Adds a Catmull-Rom spline that passes through every point, as exact cubic Béziers.</summary>
    [ProcessNode(Name = "Spline (CatmullRom)", Category = Categories.Paths)]
    public sealed class SplineCatmullRomNode
    {
        private QtPath? _input;
        private Spread<Vector2>? _points;
        private bool _closed;
        private float _alpha;
        private float _tension;
        private QtPath? _output;

        /// <summary>Returns the path with a smooth curve that runs through all points.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="points">The points the curve passes through, in units. Fewer than two points add nothing.</param>
        /// <param name="closed">If true, the curve returns smoothly from the last point to the first.</param>
        /// <param name="alpha">The parametrization: 0 = uniform, 0.5 = centripetal (no loops or cusps), 1 = chordal.</param>
        /// <param name="tension">How tight the curve is: 0 = smooth, 1 = straight lines between the points.</param>
        /// <returns>The path with the spline added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            Spread<Vector2>? points,
            bool closed = false,
            [DefaultValue(0.5f)] float alpha = 0.5f,
            [DefaultValue(0f)] float tension = 0f)
        {
            input ??= QtPath.Empty;
            points ??= Spread<Vector2>.Empty;
            // A new spread with the same content (VL may rebuild it every frame) is not a change.
            if (_output == null || !ReferenceEquals(input, _input) || closed != _closed || alpha != _alpha
                || tension != _tension || !ShapeNodeCore.SameContent(points, _points!))
            {
                _input = input;
                _points = points;
                _closed = closed;
                _alpha = alpha;
                _tension = tension;
                _output = SplineGeometry.CatmullRom(input, points.AsSpan(), closed, alpha, tension);
            }
            return _output;
        }
    }
}
