using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a cubic Bézier curve from the last point to an end point (Skia CubicTo).</summary>
    [ProcessNode(Name = "CubicTo", Category = Categories.Paths)]
    public sealed class CubicToNode
    {
        private QtPath? _input;
        private Vector2 _controlPoint1;
        private Vector2 _controlPoint2;
        private Vector2 _endPoint;
        private QtPath? _output;

        /// <summary>Returns the path with a cubic curve added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="controlPoint1">First control point of the curve in units.</param>
        /// <param name="controlPoint2">Second control point of the curve in units.</param>
        /// <param name="endPoint">End of the curve in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            [Pin(Name = "Control Point 1")] Vector2 controlPoint1,
            [Pin(Name = "Control Point 2")] Vector2 controlPoint2,
            [Pin(Name = "EndPoint")] Vector2 endPoint)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || controlPoint1 != _controlPoint1 || controlPoint2 != _controlPoint2 || endPoint != _endPoint)
            {
                _input = input;
                _controlPoint1 = controlPoint1;
                _controlPoint2 = controlPoint2;
                _endPoint = endPoint;
                _output = input.CubicTo(controlPoint1.X, controlPoint1.Y, controlPoint2.X, controlPoint2.Y, endPoint.X, endPoint.Y);
            }
            return _output;
        }
    }

    /// <summary>Adds a cubic Bézier curve with control and end points relative to the last point (Skia CubicTo (Relative)).</summary>
    [ProcessNode(Name = "CubicTo (Relative)", Category = Categories.Paths)]
    public sealed class CubicToRelativeNode
    {
        private QtPath? _input;
        private Vector2 _controlPoint1;
        private Vector2 _controlPoint2;
        private Vector2 _endPoint;
        private QtPath? _output;

        /// <summary>Returns the path with a cubic curve added, its points offset by the last point.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="controlPoint1">Offset of the first control point from the last point of the path, in units.</param>
        /// <param name="controlPoint2">Offset of the second control point from the last point of the path, in units.</param>
        /// <param name="endPoint">Offset of the end of the curve from the last point of the path, in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            [Pin(Name = "Control Point 1")] Vector2 controlPoint1,
            [Pin(Name = "Control Point 2")] Vector2 controlPoint2,
            [Pin(Name = "EndPoint")] Vector2 endPoint)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || controlPoint1 != _controlPoint1 || controlPoint2 != _controlPoint2 || endPoint != _endPoint)
            {
                _input = input;
                _controlPoint1 = controlPoint1;
                _controlPoint2 = controlPoint2;
                _endPoint = endPoint;
                var last = input.LastPoint;
                _output = input.CubicTo(
                    last.X + controlPoint1.X, last.Y + controlPoint1.Y,
                    last.X + controlPoint2.X, last.Y + controlPoint2.Y,
                    last.X + endPoint.X, last.Y + endPoint.Y);
            }
            return _output;
        }
    }
}
