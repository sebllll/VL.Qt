using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Images;
using Image = VL.Qt.Elements.Image;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Draws a QtImage (from LoadImage, LoadImage (Async) or ToQtImage). Connect to Renderer [Qt] or a Group.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// </summary>
    [ProcessNode(Name = "Image", Category = Categories.Elements)]
    public sealed class ImageNode : IDisposable
    {
        // The image is compared by reference: a new QtImage is a new image.
        internal readonly record struct Props(QtImage? Image, FillMode FillMode, bool Smooth);

        private readonly ElementNodeCore<Image, Props> _node;

        public ImageNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Image, Props>(nodeContext, s_create);

        internal ImageNode(NodeErrors messages)
            => _node = new ElementNodeCore<Image, Props>(messages, s_create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Image> s_create = static (c, p, slot) => new Image(c, slot)
            {
                Source = p.Image,
                FillMode = p.FillMode,
                Smooth = p.Smooth,
            };

        internal ElementNodeCore<Image, Props> Core => _node;

        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='size']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="image">The image to draw (LoadImage, LoadImage (Async), ToQtImage). Nothing = empty element. While a new image
        /// loads, the previous one stays visible.</param>
        /// <param name="fillMode">How the image fills Size: Stretch, Fit (keep aspect), Crop (keep aspect, fill), Tile.</param>
        /// <param name="smooth">Smooth (bilinear) scaling.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="measuredSize">Natural size of the image, in units (0, 0 until loaded; last frame).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [DefaultValue("1, 1")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            QtImage? image,
            [DefaultValue(typeof(FillMode), nameof(FillMode.Stretch))] FillMode fillMode,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool smooth,
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
                new Props(image, fillMode, smooth),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
