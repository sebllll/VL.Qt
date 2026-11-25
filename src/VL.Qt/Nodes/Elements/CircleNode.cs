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
        private readonly ElementNodeCore<Rectangle, RectangleNode.Props> _node;

        public CircleNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Rectangle, RectangleNode.Props>(nodeContext, RectangleNode.Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal CircleNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<Rectangle, RectangleNode.Props>(addWarning, RectangleNode.Create);

        internal ElementNodeCore<Rectangle, RectangleNode.Props> Core => _node;

        /// <param name="radius">Radius; the circle takes 2 × Radius in width and height. 0 = not drawn. The position comes from Transform and Anchor (default TopLeft: the corner of the bounding square; Center: the circle's center at the origin).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="color">Fill color.</param>
        /// <param name="strokeColor">Border color.</param>
        /// <param name="strokeWidth">Border width, 0 = no border.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='plain']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input. Hit-testing uses the square, not the circle.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='plain']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        // Pin defaults: see RectangleNode (string-valued [DefaultValue] for Vector2/Color4).
        [return: Pin(Name = "Output")]
        public IElement Update(
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
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var r = Math.Max(radius, 0f);
            return _node.Describe(
                new CommonInputs(new Vector2(2 * r, 2 * r), transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new RectangleNode.Props(color, r, strokeColor, strokeWidth),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
