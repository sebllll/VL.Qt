using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    // Skia has three ArcTo nodes with the same name (Tangent, Oval, SVG: they differ in their pins) and
    // ArcTo (Relative) for the SVG form. The names are the same here. Angles are in cycles (1 = full turn), as in Skia.

    /// <summary>Adds an arc that touches the lines from the last point to a corner and from the corner to a destination point (Skia ArcTo,
    /// tangent form).</summary>
    [ProcessNode(Name = "ArcTo", Category = Categories.Paths)]
    public sealed class ArcToTangentNode
    {
        internal readonly record struct Props(Vector2 Corner, Vector2 DestinationPoint, float CornerRadius);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.TangentArcTo(input ?? QtPath.Empty, p.Corner, p.DestinationPoint, p.CornerRadius);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a rounded corner added: a line from the last point towards the corner, rounded by a circle that
        /// touches the line to the destination point.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="corner">The corner where the two lines meet, in units.</param>
        /// <param name="destinationPoint">End of the second line, in units. The path ends where the circle touches that line.</param>
        /// <param name="cornerRadius">Radius of the rounding in units. Zero gives a line to the corner.</param>
        /// <returns>The path with the segments added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            Vector2 corner,
            [Pin(Name = "Destination Point")] Vector2 destinationPoint,
            [Pin(Name = "Corner Radius")] float cornerRadius)
            => _memo.Get(input, new Props(corner, destinationPoint, cornerRadius), s_create);
    }
}
