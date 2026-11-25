using System;
using System.ComponentModel;
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
        internal readonly record struct Props(RectangleF Rectangle, PathDirection Direction);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => Build(input ?? QtPath.Empty, p.Rectangle, p.Direction);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a rectangle that starts at the top left corner.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="rectangle">The rectangle in units.</param>
        /// <param name="direction">The direction in which the corners are visited.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, [DefaultValue("0, 0, 1, 1")] RectangleF rectangle, PathDirection direction)
            => _memo.Get(input, new Props(rectangle, direction), s_create);

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
