using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a straight line from the last point to a point (Skia LineTo).</summary>
    [ProcessNode(Name = "LineTo", Category = Categories.Paths)]
    public sealed class LineToNode
    {
        internal readonly record struct Props(Vector2 Point);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => (input ?? QtPath.Empty).LineTo(p.Point.X, p.Point.Y);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a line to the point added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="point">End of the line in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 point)
            => _memo.Get(input, new Props(point), s_create);
    }
}
