using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a closed rectangle with rounded corners as a new contour (Skia AddRoundRect).</summary>
    [ProcessNode(Name = "AddRoundRect", Category = Categories.Paths)]
    public sealed class AddRoundRectNode
    {
        private QtPath? _input;
        private RectangleF _rectangle;
        private Vector2 _radius;
        private PathDirection _direction;
        private QtPath? _output;

        /// <summary>Returns the path with a rectangle whose corners are quarter ellipses.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="rectangle">The rectangle in units.</param>
        /// <param name="radius">The horizontal (X) and vertical (Y) radius of the corners in units, limited to half the width and half the height.</param>
        /// <param name="dir">The direction in which the contour runs.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, RectangleF rectangle = default, Vector2 radius = default, [Pin(Name = "Dir")] PathDirection dir = PathDirection.Clockwise)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || rectangle != _rectangle || radius != _radius || dir != _direction)
            {
                _input = input;
                _rectangle = rectangle;
                _radius = radius;
                _direction = dir;
                _output = ArcGeometry.AddRoundRect(input, rectangle, radius.X, radius.Y, dir == PathDirection.Clockwise);
            }
            return _output;
        }
    }
}
