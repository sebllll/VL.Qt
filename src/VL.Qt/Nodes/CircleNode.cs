using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Filled circle with optional stroke, given by its radius (a Qt Rectangle with fully rounded corners).
    /// Connect to Renderer [Qt] or a Group. Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Circle", Category = Categories.Elements)]
    public sealed class CircleNode : IDisposable
    {
        private readonly ElementNode<Rectangle, RectangleNode.Props> _node;

        public CircleNode(NodeContext nodeContext)
            => _node = new ElementNode<Rectangle, RectangleNode.Props>(nodeContext, RectangleNode.Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal CircleNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNode<Rectangle, RectangleNode.Props>(addWarning, RectangleNode.Create);

        internal ElementNode<Rectangle, RectangleNode.Props> Core => _node;

        /// <summary>Describes the circle. A new description is only created when an input changes.</summary>
        /// <param name="radius">Radius; the circle takes 2 × Radius in width and height. 0 = not drawn. The position comes from Transform and Anchor (default TopLeft: the corner of the bounding square; Center: the circle's center at the origin).</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="color">Fill color.</param>
        /// <param name="strokeColor">Border color.</param>
        /// <param name="strokeWidth">Border width, 0 = no border.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the circle's square.</param>
        /// <param name="debug">Debug overlay for this element (see Rectangle).</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input. Hit-testing uses the square, not the circle.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="measuredSize">Implicit size reported by Qt, in units (0, 0).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        // Pin defaults: see RectangleNode (string-valued [DefaultValue] for Vector2/Color4).
        [return: Pin(Name = "Output")]
        public Element Update(
            [DefaultValue(0.5f)] float radius,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue("1, 1, 1, 1")] Color4 color,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("0, 0, 0, 1")] Color4 strokeColor,
            [Pin(Visibility = PinVisibility.Optional)] float strokeWidth,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var r = Math.Max(radius, 0f);
            var element = _node.Describe(
                new CommonInputs(new Vector2(2 * r, 2 * r), transform, anchor, opacity, clip, debug, interaction, effect),
                new RectangleNode.Props(color, r, strokeColor, strokeWidth));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
