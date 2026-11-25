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
        internal readonly record struct Props(Vector2 ControlPoint1, Vector2 ControlPoint2, Vector2 EndPoint);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => (input ?? QtPath.Empty).CubicTo(p.ControlPoint1.X, p.ControlPoint1.Y, p.ControlPoint2.X, p.ControlPoint2.Y, p.EndPoint.X, p.EndPoint.Y);

        private ChainMemo<QtPath, Props> _memo;

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
            Vector2 endPoint)
            => _memo.Get(input, new Props(controlPoint1, controlPoint2, endPoint), s_create);
    }

    /// <summary>Adds a cubic Bézier curve with control and end points relative to the last point (Skia CubicTo (Relative)).</summary>
    [ProcessNode(Name = "CubicTo (Relative)", Category = Categories.Paths)]
    public sealed class CubicToRelativeNode
    {
        internal readonly record struct Props(Vector2 ControlPoint1, Vector2 ControlPoint2, Vector2 EndPoint);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => {
            var path = input ?? QtPath.Empty;
            var last = path.LastPoint;
            return path.CubicTo(
                last.X + p.ControlPoint1.X, last.Y + p.ControlPoint1.Y,
                last.X + p.ControlPoint2.X, last.Y + p.ControlPoint2.Y,
                last.X + p.EndPoint.X, last.Y + p.EndPoint.Y);
        };

        private ChainMemo<QtPath, Props> _memo;

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
            Vector2 endPoint)
            => _memo.Get(input, new Props(controlPoint1, controlPoint2, endPoint), s_create);
    }
}
