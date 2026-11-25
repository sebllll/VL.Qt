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
    /// Filled rectangle with optional rounded corners and stroke (Qt Rectangle). Connect to Renderer [Qt] or a Group.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Rectangle", Category = Categories.Elements)]
    public sealed class RectangleNode : IDisposable
    {
        internal readonly record struct Props(Color4 Color, float Radius, Color4 StrokeColor, float StrokeWidth);

        private readonly ElementNodeCore<Rectangle, Props> _node;

        public RectangleNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Rectangle, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal RectangleNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<Rectangle, Props>(addWarning, Create);

        internal static readonly Func<CommonInputs, Props, FeedbackSlot, Rectangle> Create = static (c, p, slot) => new Rectangle(c, slot)
            {
                Fill = p.Color,
                Radius = p.Radius,
                StrokeColor = p.StrokeColor,
                StrokeWidth = p.StrokeWidth,
            };

        internal ElementNodeCore<Rectangle, Props> Core => _node;

        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='size']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="color">Fill color.</param>
        /// <param name="radius">Corner radius.</param>
        /// <param name="strokeColor">Border color.</param>
        /// <param name="strokeWidth">Border width, 0 = no border.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='plain']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='plain']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [DefaultValue("1, 1")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue("1, 1, 1, 1")] Color4 color,
            [Pin(Visibility = PinVisibility.Optional)] float radius,
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
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new Props(color, radius, strokeColor, strokeWidth),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
