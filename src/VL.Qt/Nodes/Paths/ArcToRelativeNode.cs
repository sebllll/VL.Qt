using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds an elliptical arc to a destination point relative to the last point, defined like the SVG arc command (Skia ArcTo (Relative)).</summary>
    [ProcessNode(Name = "ArcTo (Relative)", Category = Categories.Paths)]
    public sealed class ArcToRelativeNode
    {
        internal readonly record struct Props(Vector2 Radius, float XAxisRotate, bool LargeArc, bool Sweep, Vector2 DestinationPoint);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => {
            var path = input ?? QtPath.Empty;
            return ArcGeometry.SvgArcTo(path, p.Radius, p.XAxisRotate, p.LargeArc, p.Sweep, path.LastPoint + p.DestinationPoint);
        };

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with an elliptical arc to the last point plus the offset added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="radius">Radii of the ellipse in units. Radii that are too small are scaled up so that the arc fits.</param>
        /// <param name="xAxisRotate">Rotation of the ellipse in cycles (1 = full turn).</param>
        /// <param name="largeArc">If true, takes the arc of more than half a turn, otherwise the smaller one.</param>
        /// <param name="sweep">If true, the arc turns clockwise, otherwise counterclockwise.</param>
        /// <param name="destinationPoint">Offset of the end of the arc from the last point of the path, in units.</param>
        /// <returns>The path with the arc added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            Vector2 radius,
            [Pin(Name = "X Axis Rotate")] float xAxisRotate,
            [Pin(Name = "Large Arc")] bool largeArc,
            bool sweep,
            [Pin(Name = "Destination Point")] Vector2 destinationPoint)
            => _memo.Get(input, new Props(radius, xAxisRotate, largeArc, sweep, destinationPoint), s_create);
    }
}
