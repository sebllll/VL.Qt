using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a closed rectangle as a new contour (Skia AddRect).</summary>
    [ProcessNode(Name = "AddRect", Category = Categories.Paths)]
    public sealed class AddRectNode
    {
        private QtPath? _input;
        private RectangleF _rectangle;
        private PathDirection _direction;
        private QtPath? _output;

        /// <summary>Returns the path with a rectangle that starts at the top left corner.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="rectangle">The rectangle in units.</param>
        /// <param name="direction">The direction in which the corners are visited.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, RectangleF rectangle = default, PathDirection direction = PathDirection.Clockwise)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || rectangle != _rectangle || direction != _direction)
            {
                _input = input;
                _rectangle = rectangle;
                _direction = direction;
                _output = Build(input, rectangle, direction);
            }
            return _output;
        }

        private static QtPath Build(QtPath path, RectangleF rect, PathDirection direction)
        {
            float l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
            path = path.MoveTo(l, t);
            path = direction == PathDirection.Clockwise
                ? path.LineTo(r, t).LineTo(r, b).LineTo(l, b)
                : path.LineTo(l, b).LineTo(r, b).LineTo(r, t);
            return path.Close();
        }
    }
}
