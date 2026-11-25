using System;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a closed oval as a new contour (Skia AddOval).</summary>
    [ProcessNode(Name = "AddOval", Category = Categories.Paths)]
    public sealed class AddOvalNode
    {
        internal readonly record struct Props(RectangleF Bounds, PathDirection Direction);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.AddOval(input ?? QtPath.Empty, p.Bounds, p.Direction == PathDirection.Clockwise);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with an oval that starts at the right middle of its bounds.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="bounds">The rectangle that encloses the oval in units.</param>
        /// <param name="direction">The direction in which the contour runs.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, RectangleF bounds = default, PathDirection direction = PathDirection.Clockwise)
            => _memo.Get(input, new Props(bounds, direction), s_create);
    }
}
