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
        private QtPath? _input;
        private Vector2 _corner;
        private Vector2 _destinationPoint;
        private float _cornerRadius;
        private QtPath? _output;

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
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || corner != _corner || destinationPoint != _destinationPoint || cornerRadius != _cornerRadius)
            {
                _input = input;
                _corner = corner;
                _destinationPoint = destinationPoint;
                _cornerRadius = cornerRadius;
                _output = ArcGeometry.TangentArcTo(input, input.LastPoint, corner, destinationPoint, cornerRadius);
            }
            return _output;
        }
    }

    /// <summary>Adds the arc of an oval (Skia ArcTo, oval form).</summary>
    [ProcessNode(Name = "ArcTo", Category = Categories.Paths)]
    public sealed class ArcToOvalNode
    {
        private QtPath? _input;
        private RectangleF _bounds;
        private float _startAngle;
        private float _sweepAngle;
        private bool _forceMoveTo;
        private QtPath? _output;

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
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || bounds != _bounds || startAngle != _startAngle || sweepAngle != _sweepAngle || forceMoveTo != _forceMoveTo)
            {
                _input = input;
                _bounds = bounds;
                _startAngle = startAngle;
                _sweepAngle = sweepAngle;
                _forceMoveTo = forceMoveTo;
                _output = ArcGeometry.ArcTo(input, bounds, startAngle, sweepAngle, forceMoveTo);
            }
            return _output;
        }
    }

    /// <summary>Adds an elliptical arc from the last point to a destination point, defined like the SVG arc command (Skia ArcTo, SVG form).</summary>
    [ProcessNode(Name = "ArcTo", Category = Categories.Paths)]
    public sealed class ArcToSvgNode
    {
        private QtPath? _input;
        private Vector2 _radius;
        private float _xAxisRotate;
        private bool _largeArc;
        private bool _sweep;
        private Vector2 _destinationPoint;
        private QtPath? _output;

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
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || radius != _radius || xAxisRotate != _xAxisRotate || largeArc != _largeArc || sweep != _sweep || destinationPoint != _destinationPoint)
            {
                _input = input;
                _radius = radius;
                _xAxisRotate = xAxisRotate;
                _largeArc = largeArc;
                _sweep = sweep;
                _destinationPoint = destinationPoint;
                _output = ArcGeometry.SvgArcTo(input, input.LastPoint, radius, xAxisRotate, largeArc, sweep, destinationPoint);
            }
            return _output;
        }
    }

    /// <summary>Adds an elliptical arc to a destination point relative to the last point, defined like the SVG arc command (Skia ArcTo (Relative)).</summary>
    [ProcessNode(Name = "ArcTo (Relative)", Category = Categories.Paths)]
    public sealed class ArcToRelativeNode
    {
        private QtPath? _input;
        private Vector2 _radius;
        private float _xAxisRotate;
        private bool _largeArc;
        private bool _sweep;
        private Vector2 _destinationPoint;
        private QtPath? _output;

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
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || radius != _radius || xAxisRotate != _xAxisRotate || largeArc != _largeArc || sweep != _sweep || destinationPoint != _destinationPoint)
            {
                _input = input;
                _radius = radius;
                _xAxisRotate = xAxisRotate;
                _largeArc = largeArc;
                _sweep = sweep;
                _destinationPoint = destinationPoint;
                var last = input.LastPoint;
                _output = ArcGeometry.SvgArcTo(input, last, radius, xAxisRotate, largeArc, sweep, last + destinationPoint);
            }
            return _output;
        }
    }
}
