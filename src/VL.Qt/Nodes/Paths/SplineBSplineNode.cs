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
        private QtPath? _input;
        private Spread<Vector2>? _points;
        private bool _closed;
        private QtPath? _output;

        /// <summary>Returns the path with a smooth curve that follows the control polygon and does not pass through its inner points.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="points">The control polygon in units. An open curve starts at the first and ends at the last point. Fewer than two points add nothing.</param>
        /// <param name="closed">If true, the curve is a closed loop around the control polygon.</param>
        /// <returns>The path with the spline added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Spread<Vector2>? points, bool closed = false)
        {
            input ??= QtPath.Empty;
            points ??= Spread<Vector2>.Empty;
            // A new spread with the same content (VL may rebuild it every frame) is not a change.
            if (_output == null || !ReferenceEquals(input, _input) || closed != _closed
                || !ShapeNodeCore.SameContent(points, _points!))
            {
                _input = input;
                _points = points;
                _closed = closed;
                _output = SplineGeometry.BSpline(input, points.AsSpan(), closed);
            }
            return _output;
        }
    }
}
