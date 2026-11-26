using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a straight line from the last point to a point relative to the last point (Skia LineTo (Relative)).</summary>
    [ProcessNode(Name = "LineTo (Relative)", Category = Categories.Paths)]
    public sealed class LineToRelativeNode
    {
        internal readonly record struct Props(Vector2 Point);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => {
            var path = input ?? QtPath.Empty;
            var last = path.LastPoint;
            return path.LineTo(last.X + p.Point.X, last.Y + p.Point.Y);
        };

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a line to the last point plus the offset added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="point">Offset from the last point of the path, in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 point)
            => _memo.Get(input, new Props(point), s_create);
    }
}
