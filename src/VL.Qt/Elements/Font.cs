namespace VL.Qt.Elements
{
    /// <summary>
    /// A font for a <c>font</c> property of a QML file: a pin of a node made from a QML file, or a value of the Properties input of
    /// QmlElement. Immutable; create it with the Font node. The fields and units are those of the font pins of Text: the family
    /// (an installed font, or the Family of LoadFont), the size in units, the weight and italic.
    /// </summary>
    public sealed record Font
    {
        /// <summary>Font family; empty = the default family of the app.</summary>
        public string Family { get; init; } = string.Empty;

        /// <summary>Font size (pixel height) in units, rounded to whole DIP as at Text (0.12 = 12 DIP).</summary>
        public float Size { get; init; } = Text.DefaultFontSize;

        /// <summary>Font weight, 1..1000 (400 Normal, 700 Bold).</summary>
        public int Weight { get; init; } = 400;

        /// <summary>Italic style.</summary>
        public bool Italic { get; init; }
    }
}
