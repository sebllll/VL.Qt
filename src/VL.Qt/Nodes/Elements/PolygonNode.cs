using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Closed polygon through the given points with a fill and an optional stroke (a Qt Shape).
    /// Connect to Renderer [Qt] or a Group. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Polygon", Category = Categories.Elements)]
    public sealed class PolygonNode : IDisposable
    {
        private readonly ShapeNodeCore _core;

        public PolygonNode(NodeContext nodeContext) => _core = new ShapeNodeCore(nodeContext);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal PolygonNode(Func<string, IDisposable> addWarning) => _core = new ShapeNodeCore(addWarning);

        internal ShapeNodeCore Core => _core;

        /// <param name="points">Vertices in units, in the parent space. Transform acts around the origin of the parent space, not around the shape; the shape reports (max X, max Y) of the points as its size.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='shape']/param[@name='transform']"/>
        /// <param name="fill">Fill color of the enclosed area.</param>
        /// <param name="strokeColor">Color of the stroke.</param>
        /// <param name="strokeWidth">Width of the stroke in units, 0 = no stroke.</param>
        /// <param name="join">Shape of the stroke at corners: Miter, Bevel or Round.</param>
        /// <param name="miterLimit">Limit for miter joins; sharper corners are beveled (rounded to an integer by Qt).</param>
        /// <param name="dash">Lengths of dashes and gaps in units, alternating. Empty = solid stroke.</param>
        /// <param name="dashOffset">Start of the dash pattern along the stroke, in units.</param>
        /// <param name="fillRule">Which areas of a self-intersecting polygon are filled: NonZero or EvenOdd.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='shape']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='shape']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='plain']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            Spread<Vector2> points,
            Matrix transform,
            [DefaultValue("1, 1, 1, 1")] Color4 fill,
            [Pin(Name = "Stroke Color")] [DefaultValue("1, 1, 1, 1")] Color4 strokeColor,
            [Pin(Name = "Stroke Width")] [DefaultValue(0f)] float strokeWidth,
            [Pin(Visibility = PinVisibility.Optional)] [DefaultValue(typeof(ShapeJoin), nameof(ShapeJoin.Miter))] ShapeJoin join,
            [Pin(Name = "Miter Limit", Visibility = PinVisibility.Optional)] [DefaultValue(4f)] float miterLimit,
            [Pin(Visibility = PinVisibility.Optional)] Spread<float> dash,
            [Pin(Name = "Dash Offset", Visibility = PinVisibility.Optional)] [DefaultValue(0f)] float dashOffset,
            [Pin(Name = "Fill Rule", Visibility = PinVisibility.Optional)] [DefaultValue(typeof(ShapeFillRule), nameof(ShapeFillRule.NonZero))] ShapeFillRule fillRule,
            [Pin(Visibility = PinVisibility.Optional)] [DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var props = new ShapeNodeCore.Props(
                points ?? Spread<Vector2>.Empty, true, fill, strokeColor, strokeWidth,
                ShapeCap.Flat, join, miterLimit, dash ?? Spread<float>.Empty, dashOffset,
                0f, 1f, 0f, fillRule);
            return _core.Describe(new CommonInputs(Vector2.Zero, transform, RectangleAnchor.TopLeft, opacity, clip, debug, interaction, effect, enabled), props, out measuredSize, out sceneBounds);
        }

        public void Dispose() => _core.Dispose();
    }
}
