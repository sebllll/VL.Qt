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
    /// Straight line between two points, drawn with a stroke (a Qt Shape with two points).
    /// Connect to Renderer [Qt] or a Group. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Line", Category = Categories.Elements)]
    public sealed class LineNode : IDisposable
    {
        private readonly ShapeNodeCore _core;

        public LineNode(NodeContext nodeContext) => _core = new ShapeNodeCore(nodeContext);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal LineNode(Func<string, IDisposable> addWarning) => _core = new ShapeNodeCore(addWarning);

        internal ShapeNodeCore Core => _core;

        /// <summary>Describes the line. A new description is only created when an input changes.</summary>
        /// <param name="from">Start of the line in units, in the parent space. Transform acts around the origin of the parent space, not around the line.</param>
        /// <param name="to">End of the line in units, in the parent space.</param>
        /// <param name="transform">Position, rotation and scale; applied around the origin of the parent space (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="strokeColor">Color of the stroke.</param>
        /// <param name="strokeWidth">Width of the stroke in units, 0 = no stroke.</param>
        /// <param name="cap">Shape of the open ends of the stroke: Flat, Square or Round.</param>
        /// <param name="dash">Lengths of dashes and gaps in units, alternating. Empty = solid stroke.</param>
        /// <param name="dashOffset">Start of the dash pattern along the stroke, in units.</param>
        /// <param name="trimStart">Start of the visible part of the stroke, as fraction of the path length (0 to 1).</param>
        /// <param name="trimEnd">End of the visible part of the stroke, as fraction of the path length (0 to 1).</param>
        /// <param name="trimOffset">Shifts the visible part along the path, as fraction of the path length.</param>
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
            [DefaultValue("0.5, 0.5")] Vector2 from,
            [DefaultValue("2.5, 1.5")] Vector2 to,
            Matrix transform,
            [Pin(Name = "Stroke Color")] [DefaultValue("1, 1, 1, 1")] Color4 strokeColor,
            [Pin(Name = "Stroke Width")] [DefaultValue(0.05f)] float strokeWidth,
            [Pin(Visibility = PinVisibility.Optional)] [DefaultValue(typeof(ShapeCap), nameof(ShapeCap.Flat))] ShapeCap cap,
            [Pin(Visibility = PinVisibility.Optional)] Spread<float> dash,
            [Pin(Name = "Dash Offset", Visibility = PinVisibility.Optional)] [DefaultValue(0f)] float dashOffset,
            [Pin(Name = "Trim Start", Visibility = PinVisibility.Optional)] [DefaultValue(0f)] float trimStart,
            [Pin(Name = "Trim End", Visibility = PinVisibility.Optional)] [DefaultValue(1f)] float trimEnd,
            [Pin(Name = "Trim Offset", Visibility = PinVisibility.Optional)] [DefaultValue(0f)] float trimOffset,
            [Pin(Visibility = PinVisibility.Optional)] [DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var props = new ShapeNodeCore.Props(
                _core.LinePoints(from, to), false, new Color4(0, 0, 0, 0), strokeColor, strokeWidth,
                cap, ShapeJoin.Miter, 4f, dash ?? Spread<float>.Empty, dashOffset,
                trimStart, trimEnd, trimOffset, ShapeFillRule.NonZero);
            return _core.Describe(new CommonInputs(Vector2.Zero, transform, RectangleAnchor.TopLeft, opacity, clip, debug, interaction, effect), props, out measuredSize, out sceneBounds);
        }

        public void Dispose() => _core.Dispose();
    }
}
