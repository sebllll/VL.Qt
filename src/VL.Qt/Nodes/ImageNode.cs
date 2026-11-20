using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;
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
        internal readonly record struct Props(QtImage? Image, ImageFillMode FillMode, bool Smooth);

        private readonly ElementNode<Image, Props> _node;

        public ImageNode(NodeContext nodeContext)
            => _node = new ElementNode<Image, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ImageNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNode<Image, Props>(addWarning, Create);

        private static readonly Func<CommonInputs, Props, SinkSlot, Image> Create = static (c, p, slot) => new Image
            {
                Size = c.Size,
                Transform = c.Transform,
                Anchor = c.Anchor,
                Opacity = c.Opacity,
                Clip = c.Clip,
                Debug = c.Debug,
                Interaction = c.Interaction,
                Effect = c.Effect,
                Sink = slot,
                Source = p.Image,
                FillMode = p.FillMode,
                Smooth = p.Smooth,
            };

        internal ElementNode<Image, Props> Core => _node;

        /// <summary>Describes the image. A new description is only created when an input changes.</summary>
        /// <param name="size">Size of the element (width, height). The position comes from Transform and Anchor.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="image">The image to draw (LoadImage, LoadImage (Async), ToQtImage). Nothing = empty element. While a new image loads, the previous one stays visible.</param>
        /// <param name="fillMode">How the image fills Size: Stretch, Fit (keep aspect), Crop (keep aspect, fill), Tile.</param>
        /// <param name="smooth">Smooth (bilinear) scaling.</param>
        /// <param name="opacity">Opacity of the element and its children. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only (not its children; the Renderer's Debug shows all). Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), blue dotted = children extent, translucent red = children outside the frame (overflow), translucent green = padding and hatched violet = Row/Column spacing (both inside the frame), magenta cross = origin, yellow diamond = anchor. Text: green line = baseline, dashed green = ascent, dashed red = descent.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="measuredSize">Natural size of the image reported by Qt, in units (0, 0 until loaded; last frame).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        // Pin defaults: see RectangleNode. Image default = null (no image).
        [return: Pin(Name = "Output")]
        public Element Update(
            [DefaultValue("1, 1")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            QtImage? image,
            [DefaultValue(typeof(ImageFillMode), nameof(ImageFillMode.Stretch))] ImageFillMode fillMode,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool smooth,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(image, fillMode, smooth));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
