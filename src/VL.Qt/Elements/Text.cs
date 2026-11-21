using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>Plain text, one style per element (Qt Text). Lengths in units.</summary>
    internal sealed class Text : Element
    {
        /// <summary>12 DIP: Qt's Windows default font (Segoe UI 9 pt at 96 dpi).</summary>
        public const float DefaultFontSize = 0.12f;

        public Text() : base(ElementType.Text) { }

        public string Content { get; init; } = string.Empty;
        /// <summary>Empty = Qt/system default family.</summary>
        public string FontFamily { get; init; } = string.Empty;
        /// <summary>Font pixel size in units.</summary>
        public float FontSize { get; init; } = DefaultFontSize;
        public FontWeight Weight { get; init; } = FontWeight.Normal;
        public bool Italic { get; init; }
        public Color4 Color { get; init; } = Color4.Black;
        public TextHorizontalAlignment HAlign { get; init; } = TextHorizontalAlignment.Left;
        public VerticalAlignment VAlign { get; init; } = VerticalAlignment.Top;
        public TextWrap Wrap { get; init; } = TextWrap.NoWrap;
        public TextElide Elide { get; init; } = TextElide.None;
        /// <summary>Proportional line height (1 = font line height).</summary>
        public float LineHeight { get; init; } = 1;
        /// <summary>Extra space between letters in units.</summary>
        public float LetterSpacing { get; init; }
        public TextAutofit Autofit { get; init; } = TextAutofit.Fixed;
    }
}
