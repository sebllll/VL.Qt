using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes.Paths
{
    /// <summary>Adds a quadratic Bézier curve from the last point to an end point (Skia QuadTo).</summary>
    [ProcessNode(Name = "QuadTo", Category = Categories.Paths)]
    public sealed class QuadToNode
    {
        private QtPath? _input;
        private Vector2 _controlPoint;
        private Vector2 _endPoint;
        private QtPath? _output;

        /// <summary>Returns the path with a quadratic curve added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="controlPoint">Control point of the curve in units.</param>
        /// <param name="endPoint">End of the curve in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, [Pin(Name = "Control Point")] Vector2 controlPoint, [Pin(Name = "End Point")] Vector2 endPoint)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || controlPoint != _controlPoint || endPoint != _endPoint)
            {
                _input = input;
                _controlPoint = controlPoint;
                _endPoint = endPoint;
                _output = input.QuadTo(controlPoint.X, controlPoint.Y, endPoint.X, endPoint.Y);
            }
            return _output;
        }
    }

    /// <summary>Adds a quadratic Bézier curve with control and end point relative to the last point (Skia QuadTo (Relative)).</summary>
    [ProcessNode(Name = "QuadTo (Relative)", Category = Categories.Paths)]
    public sealed class QuadToRelativeNode
    {
        private QtPath? _input;
        private Vector2 _controlPoint;
        private Vector2 _endPoint;
        private QtPath? _output;

        /// <summary>Returns the path with a quadratic curve added, its points offset by the last point.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="controlPoint">Offset of the control point from the last point of the path, in units.</param>
        /// <param name="endPoint">Offset of the end of the curve from the last point of the path, in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, [Pin(Name = "Control Point")] Vector2 controlPoint, [Pin(Name = "End Point")] Vector2 endPoint)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || controlPoint != _controlPoint || endPoint != _endPoint)
            {
                _input = input;
                _controlPoint = controlPoint;
                _endPoint = endPoint;
                var last = input.LastPoint;
                _output = input.QuadTo(last.X + controlPoint.X, last.Y + controlPoint.Y, last.X + endPoint.X, last.Y + endPoint.Y);
            }
            return _output;
        }
    }
}
