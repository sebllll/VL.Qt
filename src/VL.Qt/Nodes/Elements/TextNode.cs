using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Plain text with one style (Qt Text). Connect to Renderer [Qt] or a Group.
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP); the font size is rounded to whole DIP.
    /// </summary>
    [ProcessNode(Name = "Text", Category = Categories.Elements)]
    public sealed class TextNode : IDisposable
    {
        internal readonly record struct Props(
            string Content, string FontFamily, float FontSize, FontWeight Weight, bool Italic, Color4 Color,
            TextHorizontalAlignment HAlign, VerticalAlignment VAlign, TextWrap Wrap, TextElide Elide,
            float LineHeight, float LetterSpacing, TextAutofit Autofit);

        private readonly ElementNode<Text, Props> _node;

        public TextNode(NodeContext nodeContext)
            => _node = new ElementNode<Text, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal TextNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNode<Text, Props>(addWarning, Create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Text> Create = static (c, p, slot) => new Text
            {
                Size = c.Size,
                Transform = c.Transform,
                Anchor = c.Anchor,
                Opacity = c.Opacity,
                Clip = c.Clip,
                Debug = c.Debug,
                Interaction = c.Interaction,
                Effect = c.Effect,
                Feedback = slot,
                Content = p.Content,
                FontFamily = p.FontFamily,
                FontSize = p.FontSize,
                Weight = p.Weight,
                Italic = p.Italic,
                Color = p.Color,
                HAlign = p.HAlign,
                VAlign = p.VAlign,
                Wrap = p.Wrap,
                Elide = p.Elide,
                LineHeight = p.LineHeight,
                LetterSpacing = p.LetterSpacing,
                Autofit = p.Autofit,
            };

        internal ElementNode<Text, Props> Core => _node;

        /// <summary>Describes the text. A new description is only created when an input changes.</summary>
        /// <param name="size">Size of the element (width, height). The position comes from Transform and Anchor. The size is the box for alignment, wrapping and eliding.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="text">The text (plain, no markup).</param>
        /// <param name="fontFamily">Font family, empty = system default.</param>
        /// <param name="fontSize">Font size (pixel height) in units, rounded to whole DIP (0.12 = 12 DIP).</param>
        /// <param name="weight">Font weight.</param>
        /// <param name="italic">Italic style.</param>
        /// <param name="color">Text color.</param>
        /// <param name="horizontalAlignment">Horizontal alignment within Size.</param>
        /// <param name="verticalAlignment">Vertical alignment within Size.</param>
        /// <param name="wrap">Line breaking at the width of Size.</param>
        /// <param name="elide">Where to cut text that does not fit.</param>
        /// <param name="lineHeight">Proportional line height (1 = font line height).</param>
        /// <param name="letterSpacing">Extra space between letters in units.</param>
        /// <param name="autofit">Shrink the font size to fit Size (Font Size is the maximum).</param>
        /// <param name="opacity">Opacity of the element and its children. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only (not its children; the Renderer's Debug shows all). Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), blue dotted = children extent, translucent red = children outside the frame (overflow), translucent green = padding and hatched violet = Row/Column spacing (both inside the frame), magenta cross = origin, yellow diamond = anchor. Text: green line = baseline, dashed green = ascent, dashed red = descent.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="measuredSize">Size of the laid-out text reported by Qt, in units (last frame).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        // Pin defaults: see RectangleNode (string-valued [DefaultValue] for Color4/Vector2). Default text color is
        // white (readable on the Renderer's #202020 clear color), Size 100 x 100 DIP.
        [return: Pin(Name = "Output")]
        public IElement Update(
            [DefaultValue("1, 1")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue("Text")] string text,
            string fontFamily,
            [DefaultValue(0.12f)] float fontSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(FontWeight), nameof(FontWeight.Normal))] FontWeight weight,
            [Pin(Visibility = PinVisibility.Optional)] bool italic,
            [DefaultValue("1, 1, 1, 1")] Color4 color,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(TextHorizontalAlignment), nameof(TextHorizontalAlignment.Left))] TextHorizontalAlignment horizontalAlignment,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(VerticalAlignment), nameof(VerticalAlignment.Top))] VerticalAlignment verticalAlignment,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(TextWrap), nameof(TextWrap.NoWrap))] TextWrap wrap,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(TextElide), nameof(TextElide.None))] TextElide elide,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float lineHeight,
            [Pin(Visibility = PinVisibility.Optional)] float letterSpacing,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(TextAutofit), nameof(TextAutofit.Fixed))] TextAutofit autofit,
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
                new Props(text ?? string.Empty, fontFamily ?? string.Empty, fontSize, weight, italic, color,
                    horizontalAlignment, verticalAlignment, wrap, elide, lineHeight, letterSpacing, autofit));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
