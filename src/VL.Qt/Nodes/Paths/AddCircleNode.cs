using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a closed circle as a new contour (Skia AddCircle).</summary>
    [ProcessNode(Name = "AddCircle", Category = Categories.Paths)]
    public sealed class AddCircleNode
    {
        private QtPath? _input;
        private Vector2 _center;
        private PathDirection _direction;
        private float _radius;
        private QtPath? _output;

        /// <summary>Returns the path with a circle that starts at the right of its center.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="center">The center of the circle in units.</param>
        /// <param name="dir">The direction in which the contour runs.</param>
        /// <param name="radius">The radius of the circle in units.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 center = default, [Pin(Name = "Dir")] PathDirection dir = PathDirection.Clockwise, float radius = 0.5f)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || center != _center || dir != _direction || radius != _radius)
            {
                _input = input;
                _center = center;
                _direction = dir;
                _radius = radius;
                _output = ArcGeometry.AddCircle(input, center.X, center.Y, radius, dir == PathDirection.Clockwise);
            }
            return _output;
        }
    }
}
