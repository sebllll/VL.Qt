using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a quadratic Bézier curve with control and end point relative to the last point (Skia QuadTo (Relative)).</summary>
    [ProcessNode(Name = "QuadTo (Relative)", Category = Categories.Paths)]
    public sealed class QuadToRelativeNode
    {
        internal readonly record struct Props(Vector2 ControlPoint, Vector2 EndPoint);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => {
            var path = input ?? QtPath.Empty;
            var last = path.LastPoint;
            return path.QuadTo(last.X + p.ControlPoint.X, last.Y + p.ControlPoint.Y, last.X + p.EndPoint.X, last.Y + p.EndPoint.Y);
        };

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a quadratic curve added, its points offset by the last point.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="controlPoint">Offset of the control point from the last point of the path, in units.</param>
        /// <param name="endPoint">Offset of the end of the curve from the last point of the path, in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, [Pin(Name = "Control Point")] Vector2 controlPoint, [Pin(Name = "End Point")] Vector2 endPoint)
            => _memo.Get(input, new Props(controlPoint, endPoint), s_create);
    }
}
