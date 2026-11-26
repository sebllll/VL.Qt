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
    /// Connected line segments through the given points, drawn with a stroke; optionally closed (a Qt Shape).
    /// Connect to Renderer [Qt] or a Group. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Polyline", Category = Categories.Elements)]
    public sealed class PolylineNode : IDisposable
    {
        private readonly ShapeNodeCore _core;

        public PolylineNode(NodeContext nodeContext) => _core = new ShapeNodeCore(nodeContext);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal PolylineNode(Func<string, IDisposable> addWarning) => _core = new ShapeNodeCore(addWarning);

        internal ShapeNodeCore Core => _core;

        /// <param name="points">Vertices in units, in the parent space. Transform acts around the origin of the parent space, not around
        /// the shape; the shape reports (max X, max Y) of the points as its size.</param>
        /// <param name="closed">Connects the last point to the first.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='shape']/param[@name='transform']"/>
        /// <param name="strokeColor">Color of the stroke.</param>
        /// <param name="strokeWidth">Width of the stroke in units, 0 = no stroke.</param>
        /// <param name="cap">Shape of the open ends of the stroke: Flat, Square or Round.</param>
        /// <param name="join">Shape of the stroke at corners: Miter, Bevel or Round.</param>
        /// <param name="miterLimit">Limit for miter joins; sharper corners are beveled (rounded to an integer by Qt).</param>
        /// <param name="dash">Lengths of dashes and gaps in units, alternating. Empty = solid stroke.</param>
        /// <param name="dashOffset">Start of the dash pattern along the stroke, in units.</param>
        /// <param name="trimStart">Start of the visible part of the stroke, as fraction of the path length (0 to 1).</param>
        /// <param name="trimEnd">End of the visible part of the stroke, as fraction of the path length (0 to 1).</param>
        /// <param name="trimOffset">Shifts the visible part along the path, as fraction of the path length.</param>
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
            bool closed,
            Matrix transform,
            [DefaultValue("1, 1, 1, 1")] Color4 strokeColor,
            [DefaultValue(0.05f)] float strokeWidth,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(ShapeCap), nameof(ShapeCap.Flat))] ShapeCap cap,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(ShapeJoin), nameof(ShapeJoin.Miter))] ShapeJoin join,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(4f)] float miterLimit,
            [Pin(Visibility = PinVisibility.Optional)] Spread<float> dash,
            [Pin(Visibility = PinVisibility.Optional)] float dashOffset,
            [Pin(Visibility = PinVisibility.Optional)] float trimStart,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float trimEnd,
            [Pin(Visibility = PinVisibility.Optional)] float trimOffset,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var props = new ShapeNodeCore.Props(
                points ?? Spread<Vector2>.Empty, closed, new Color4(0, 0, 0, 0), strokeColor, strokeWidth,
                cap, join, miterLimit, dash ?? Spread<float>.Empty, dashOffset,
                trimStart, trimEnd, trimOffset, ShapeFillRule.NonZero);
            return _core.Describe(new CommonInputs(Vector2.Zero, transform, RectangleAnchor.TopLeft, opacity, clip, debug, interaction, effect, enabled), props, out measuredSize, out sceneBounds);
        }

        public void Dispose() => _core.Dispose();
    }
}
