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
        readonly ShapeNodeCore _core;

        public PolygonNode(NodeContext nodeContext) => _core = new ShapeNodeCore(nodeContext);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal PolygonNode(Func<string, IDisposable> addWarning) => _core = new ShapeNodeCore(addWarning);

        internal ShapeNodeCore Core => _core;

        /// <summary>Describes the polygon. A new description is only created when an input changes.</summary>
        /// <param name="points">Vertices in units, in the parent space (at most 8190 are drawn, the node warns about more). Transform acts around the origin of the parent space, not around the shape; the shape reports (max X, max Y) of the points as its size.</param>
        /// <param name="transform">Position, rotation and scale; applied around the origin of the parent space (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="fill">Fill color of the enclosed area.</param>
        /// <param name="strokeColor">Color of the stroke.</param>
        /// <param name="strokeWidth">Width of the stroke in units, 0 = no stroke.</param>
        /// <param name="join">Shape of the stroke at corners: Miter, Bevel or Round.</param>
        /// <param name="miterLimit">Limit for miter joins; sharper corners are beveled (rounded to an integer by Qt).</param>
        /// <param name="dash">Lengths of dashes and gaps in units, alternating. Empty = solid stroke.</param>
        /// <param name="dashOffset">Start of the dash pattern along the stroke, in units.</param>
        /// <param name="fillRule">Which areas of a self-intersecting polygon are filled: NonZero or EvenOdd.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the extent of the shape (see Rectangle).</param>
        /// <param name="debug">Debug overlay for this element (see Rectangle).</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input. Hit-testing uses the extent (0, 0) to (max X, max Y) of the points, not the stroke.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="measuredSize">Implicit size reported by Qt, in units (0, 0).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        /// <returns>The element for Renderer [Qt] or a Group.</returns>
        [return: Pin(Name = "Output")]
        public Element Update(
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
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var props = new ShapeNodeCore.Props(
                points ?? Spread<Vector2>.Empty, true, fill, strokeColor, strokeWidth,
                ShapeCap.Flat, join, miterLimit, dash ?? Spread<float>.Empty, dashOffset,
                0f, 1f, 0f, fillRule);
            return _core.Describe(new CommonInputs(Vector2.Zero, transform, RectangleAnchor.TopLeft, opacity, clip, debug, interaction, effect), props, out measuredSize, out sceneBounds);
        }

        public void Dispose() => _core.Dispose();
    }
}
