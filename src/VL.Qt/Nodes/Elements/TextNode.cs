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
            string Content, string FontFamily, float FontSize, int Weight, bool Italic, Color4 Color,
            TextHorizontalAlignment HAlign, VerticalAlignment VAlign, TextWrap Wrap, TextElide Elide,
            float LineHeight, float LetterSpacing, TextAutofit Autofit);

        private readonly ElementNodeCore<Text, Props> _node;

        public TextNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Text, Props>(nodeContext, s_create);

        internal TextNode(NodeErrors messages)
            => _node = new ElementNodeCore<Text, Props>(messages, s_create);

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Text> s_create = static (c, p, slot) => new Text(c, slot)
            {
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

        internal ElementNodeCore<Text, Props> Core => _node;

        /// <param name="size">Size of the element (width, height). The position comes from Transform and Anchor. The size is the box for
        /// alignment, wrapping and eliding.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="text">The text (plain, no markup).</param>
        /// <param name="fontFamily">Font family, empty = system default.</param>
        /// <param name="fontSize">Font size (pixel height) in units, rounded to whole DIP (0.12 = 12 DIP).</param>
        /// <param name="weight">Font weight, 1..1000 (variable fonts take every value, other fonts the nearest face). Named steps: 100
        /// Thin, 200 ExtraLight, 300 Light, 400 Normal, 500 Medium, 600 DemiBold, 700 Bold, 800 ExtraBold, 900 Black.</param>
        /// <param name="italic">Italic style.</param>
        /// <param name="color">Text color.</param>
        /// <param name="horizontalAlignment">Horizontal alignment within Size.</param>
        /// <param name="verticalAlignment">Vertical alignment within Size.</param>
        /// <param name="wrap">Line breaking at the width of Size.</param>
        /// <param name="elide">Where to cut text that does not fit.</param>
        /// <param name="lineHeight">Proportional line height (1 = font line height).</param>
        /// <param name="letterSpacing">Extra space between letters in units.</param>
        /// <param name="autofit">Shrink the font size to fit Size (Font Size is the maximum).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="measuredSize">Size of the laid-out text, in units (last frame).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        // Default text color is white (readable on the Renderer's #202020 clear color), Size 100 x 100 DIP.
        [return: Pin(Name = "Output")]
        public IElement Update(
            [DefaultValue("1, 1")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [DefaultValue("Text")] string text,
            string fontFamily,
            [DefaultValue(0.12f)] float fontSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(400)] int weight,
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
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            return _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new Props(text ?? string.Empty, fontFamily ?? string.Empty, fontSize, weight, italic, color,
                    horizontalAlignment, verticalAlignment, wrap, elide, lineHeight, letterSpacing, autofit),
                out measuredSize, out sceneBounds);
        }

        public void Dispose() => _node.Dispose();
    }
}
