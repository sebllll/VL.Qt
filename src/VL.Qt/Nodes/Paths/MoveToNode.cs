using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    // Builder nodes of Qt.Paths (MoveTo, LineTo, ...): a [ProcessNode] class per node, absolute and relative variant side by
    // side. The relative one is a separate node "<Name> (Relative)" (VL shows the text in parentheses as the version). The
    // ChainMemo of the node keeps the chain reference-stable between frames.

    /// <summary>Starts a new contour at a point (Skia MoveTo).</summary>
    [ProcessNode(Name = "MoveTo", Category = Categories.Paths)]
    public sealed class MoveToNode
    {
        internal readonly record struct Props(Vector2 Point);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => (input ?? QtPath.Empty).MoveTo(p.Point.X, p.Point.Y);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a new contour that starts at the point.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="point">Start of the new contour in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 point)
            => _memo.Get(input, new Props(point), s_create);
    }
}
