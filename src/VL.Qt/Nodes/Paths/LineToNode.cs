using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes.Paths
{
    // Builder nodes of Qt.Paths, see MoveToNode for the pattern.

    /// <summary>Adds a straight line from the last point to a point (Skia LineTo).</summary>
    [ProcessNode(Name = "LineTo", Category = Categories.Paths)]
    public sealed class LineToNode
    {
        private QtPath? _input;
        private Vector2 _point;
        private QtPath? _output;

        /// <summary>Returns the path with a line to the point added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="point">End of the line in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, Vector2 point)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || point != _point)
            {
                _input = input;
                _point = point;
                _output = input.LineTo(point.X, point.Y);
            }
            return _output;
        }
    }

    /// <summary>Adds a straight line from the last point to a point relative to the last point (Skia LineTo (Relative)).</summary>
    [ProcessNode(Name = "LineTo (Relative)", Category = Categories.Paths)]
    public sealed class LineToRelativeNode
    {
        private QtPath? _input;
        private Vector2 _point;
        private QtPath? _output;

        /// <summary>Returns the path with a line to the last point plus the offset added.</summary>
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
                _output = input.LineTo(last.X + point.X, last.Y + point.Y);
            }
            return _output;
        }
    }
}
