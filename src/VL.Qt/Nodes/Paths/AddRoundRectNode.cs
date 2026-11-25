using System;
using System.ComponentModel;
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
        internal readonly record struct Props(RectangleF Rectangle, Vector2 Radius, PathDirection Direction);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.AddRoundRect(input ?? QtPath.Empty, p.Rectangle, p.Radius.X, p.Radius.Y, p.Direction == PathDirection.Clockwise);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a rectangle whose corners are quarter ellipses.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="rectangle">The rectangle in units.</param>
        /// <param name="radius">The horizontal (X) and vertical (Y) radius of the corners in units, scaled down together if they do not fit (a very large radius gives semicircular ends); zero or less gives a plain rectangle.</param>
        /// <param name="dir">The direction in which the contour runs.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            [DefaultValue("0, 0, 1, 1")] RectangleF rectangle,
            [DefaultValue("0.1, 0.1")] Vector2 radius,
            [Pin(Name = "Dir")] PathDirection dir)
            => _memo.Get(input, new Props(rectangle, radius, dir), s_create);
    }
}
