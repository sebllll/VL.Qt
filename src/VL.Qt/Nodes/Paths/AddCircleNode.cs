using System;
using System.ComponentModel;
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
        internal readonly record struct Props(Vector2 Center, PathDirection Direction, float Radius);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.AddCircle(input ?? QtPath.Empty, p.Center.X, p.Center.Y, p.Radius, p.Direction == PathDirection.Clockwise);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a circle that starts at the right of its center.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="center">The center of the circle in units.</param>
        /// <param name="direction">The direction in which the contour runs.</param>
        /// <param name="radius">The radius of the circle in units.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 center, PathDirection direction, [DefaultValue(0.5f)] float radius)
            => _memo.Get(input, new Props(center, direction, radius), s_create);
    }
}
