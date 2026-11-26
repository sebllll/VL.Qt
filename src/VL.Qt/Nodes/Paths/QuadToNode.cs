using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a quadratic Bézier curve from the last point to an end point (Skia QuadTo).</summary>
    [ProcessNode(Name = "QuadTo", Category = Categories.Paths)]
    public sealed class QuadToNode
    {
        internal readonly record struct Props(Vector2 ControlPoint, Vector2 EndPoint);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => (input ?? QtPath.Empty).QuadTo(p.ControlPoint.X, p.ControlPoint.Y, p.EndPoint.X, p.EndPoint.Y);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a quadratic curve added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="controlPoint">Control point of the curve in units.</param>
        /// <param name="endPoint">End of the curve in units.</param>
        /// <returns>The path with the segment added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, [Pin(Name = "Control Point")] Vector2 controlPoint, [Pin(Name = "End Point")] Vector2 endPoint)
            => _memo.Get(input, new Props(controlPoint, endPoint), s_create);
    }
}
