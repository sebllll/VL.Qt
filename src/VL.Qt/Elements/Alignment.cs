namespace VL.Qt.Elements
{
    // Values are Qt::AlignmentFlag, so QtNative can pass them through unchanged.

    /// <summary>Horizontal alignment of children in a <see cref="Column"/>.</summary>
    public enum HorizontalAlignment
    {
        Left = 0x1,
        Right = 0x2,
        Center = 0x4,
    }

    /// <summary>Horizontal text alignment (Qt Text.horizontalAlignment).</summary>
    public enum TextHorizontalAlignment
    {
        Left = 0x1,
        Right = 0x2,
        Center = 0x4,
        Justify = 0x8,
    }

    /// <summary>Vertical alignment of text, and of children in a <see cref="Row"/>.</summary>
    public enum VerticalAlignment
    {
        Top = 0x20,
        Bottom = 0x40,
        Center = 0x80,
    }
}
