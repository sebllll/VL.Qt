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
        private QtPath? _input;
        private Spread<Vector2>? _points;
        private bool _close;
        private QtPath? _output;

        /// <summary>Returns the path with a contour that moves to the first point and runs through the others with lines.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="points">The corners of the contour in units. Nothing or an empty spread adds nothing.</param>
        /// <param name="close">If true, the contour ends with a line back to the first point.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Spread<Vector2>? points, bool close = false)
        {
            input ??= QtPath.Empty;
            points ??= Spread<Vector2>.Empty;
            // A new spread with the same content (VL may rebuild it every frame) is not a change.
            if (_output == null || !ReferenceEquals(input, _input) || close != _close || !SameContent(points, _points))
            {
                _input = input;
                _points = points;
                _close = close;
                _output = Build(input, points, close);
            }
            return _output;
        }

        private static bool SameContent(Spread<Vector2> a, Spread<Vector2>? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (b == null || a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static QtPath Build(QtPath path, Spread<Vector2> points, bool close)
        {
            if (points.Count == 0)
                return path;
            path = path.MoveTo(points[0].X, points[0].Y);
            for (var i = 1; i < points.Count; i++)
                path = path.LineTo(points[i].X, points[i].Y);
            return close ? path.Close() : path;
        }
    }
}
