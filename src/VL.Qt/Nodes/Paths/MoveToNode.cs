using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes.Paths
{
    // Template for the builder nodes of Qt.Paths (MoveTo, LineTo, ...): a [ProcessNode] class per node, absolute and
    // relative variant side by side. The relative one is a separate node "<Name> (Relative)" (VL shows the text in
    // parentheses as the version). Inputs are remembered and the previous output is returned while the input path
    // (by reference) and all parameters are unchanged, so a chain of nodes stays reference-stable between frames.

    /// <summary>Starts a new contour at a point (Skia MoveTo).</summary>
    [ProcessNode(Name = "MoveTo", Category = Categories.Paths)]
    public sealed class MoveToNode
    {
        private QtPath? _input;
        private Vector2 _point;
        private QtPath? _output;

        /// <summary>Returns the path with a new contour that starts at the point.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="point">Start of the new contour in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 point)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || point != _point)
            {
                _input = input;
                _point = point;
                _output = input.MoveTo(point.X, point.Y);
            }
            return _output;
        }
    }

    /// <summary>Starts a new contour at a point relative to the last point of the path (Skia MoveTo (Relative)).</summary>
    [ProcessNode(Name = "MoveTo (Relative)", Category = Categories.Paths)]
    public sealed class MoveToRelativeNode
    {
        private QtPath? _input;
        private Vector2 _point;
        private QtPath? _output;

        /// <summary>Returns the path with a new contour that starts at the last point plus the offset.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="point">Offset from the last point of the path, in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 point)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || point != _point)
            {
                _input = input;
                _point = point;
                var last = input.LastPoint;
                _output = input.MoveTo(last.X + point.X, last.Y + point.Y);
            }
            return _output;
        }
    }
}
