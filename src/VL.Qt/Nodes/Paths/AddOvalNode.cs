using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes.Paths
{
    /// <summary>Adds a closed oval as a new contour (Skia AddOval).</summary>
    [ProcessNode(Name = "AddOval", Category = Categories.Paths)]
    public sealed class AddOvalNode
    {
        private QtPath? _input;
        private RectangleF _bounds;
        private PathDirection _direction;
        private QtPath? _output;

        /// <summary>Returns the path with an oval that starts at the right middle of its bounds.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="bounds">The rectangle that encloses the oval in units.</param>
        /// <param name="direction">The direction in which the contour runs.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, RectangleF bounds = default, PathDirection direction = PathDirection.Clockwise)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || bounds != _bounds || direction != _direction)
            {
                _input = input;
                _bounds = bounds;
                _direction = direction;
                _output = ArcGeometry.AddOval(input, bounds, direction == PathDirection.Clockwise);
            }
            return _output;
        }
    }
}
