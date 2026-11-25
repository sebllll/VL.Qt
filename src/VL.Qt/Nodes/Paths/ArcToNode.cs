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

    /// <summary>Adds an arc that touches the lines from the last point to a corner and from the corner to a destination point (Skia ArcTo, tangent form).</summary>
    [ProcessNode(Name = "ArcTo", Category = Categories.Paths)]
    public sealed class ArcToTangentNode
    {
        internal readonly record struct Props(Vector2 Corner, Vector2 DestinationPoint, float CornerRadius);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.TangentArcTo(input ?? QtPath.Empty, p.Corner, p.DestinationPoint, p.CornerRadius);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a rounded corner added: a line from the last point towards the corner, rounded by a circle that touches the line to the destination point.</summary>
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

    /// <summary>Adds the arc of an oval (Skia ArcTo, oval form).</summary>
    [ProcessNode(Name = "ArcTo", Category = Categories.Paths)]
    public sealed class ArcToOvalNode
    {
        internal readonly record struct Props(RectangleF Bounds, float StartAngle, float SweepAngle, bool ForceMoveTo);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.ArcTo(input ?? QtPath.Empty, p.Bounds, p.StartAngle, p.SweepAngle, p.ForceMoveTo);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with an arc of the oval added. A line connects the last point to the start of the arc, unless a new contour is forced.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="bounds">The rectangle that bounds the oval, in units.</param>
        /// <param name="startAngle">Start of the arc in cycles (1 = full turn); 0 is the right end of the oval, positive angles go clockwise.</param>
        /// <param name="sweepAngle">Length of the arc in cycles; a sweep of one cycle or more gives a full turn.</param>
        /// <param name="forceMoveTo">If true, the arc starts a new contour instead of connecting to the last point.</param>
        /// <returns>The path with the arc added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            RectangleF bounds,
            [Pin(Name = "Start Angle")] float startAngle,
            [Pin(Name = "Sweep Angle")] float sweepAngle,
            [Pin(Name = "Force Move To")] bool forceMoveTo)
            => _memo.Get(input, new Props(bounds, startAngle, sweepAngle, forceMoveTo), s_create);
    }

    /// <summary>Adds an elliptical arc from the last point to a destination point, defined like the SVG arc command (Skia ArcTo, SVG form).</summary>
    [ProcessNode(Name = "ArcTo", Category = Categories.Paths)]
    public sealed class ArcToSvgNode
    {
        internal readonly record struct Props(Vector2 Radius, float XAxisRotate, bool LargeArc, bool Sweep, Vector2 DestinationPoint);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.SvgArcTo(input ?? QtPath.Empty, p.Radius, p.XAxisRotate, p.LargeArc, p.Sweep, p.DestinationPoint);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with an elliptical arc to the destination point added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path, whose last point is the origin.</param>
        /// <param name="radius">Radii of the ellipse in units. Radii that are too small are scaled up so that the arc fits.</param>
        /// <param name="xAxisRotate">Rotation of the ellipse in cycles (1 = full turn).</param>
        /// <param name="largeArc">If true, takes the arc of more than half a turn, otherwise the smaller one.</param>
        /// <param name="sweep">If true, the arc turns clockwise, otherwise counterclockwise.</param>
        /// <param name="destinationPoint">End of the arc in units.</param>
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
