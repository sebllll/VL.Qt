using System.ComponentModel;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>The Font node, next to LoadFont.</summary>
    [SkipCategory]
    [VL.Core.Import.Category(Categories.Qt)]
    public static class FontNodes
    {
        /// <summary>
        /// A font for a font property of a QML file: a pin of a node made from a QML file, or a value of the Properties input of
        /// QmlElement. The pins are those of the font of Text.
        /// </summary>
        /// <param name="family">Font family, empty = the default family. For a font file, connect the Family output of LoadFont.</param>
        /// <param name="size">Font size (pixel height) in units, rounded to whole DIP (0.12 = 12 DIP).</param>
        /// <param name="weight">Font weight, 1..1000 (variable fonts take every value, other fonts the nearest face). Named steps: 100
        /// Thin, 200 ExtraLight, 300 Light, 400 Normal, 500 Medium, 600 DemiBold, 700 Bold, 800 ExtraBold, 900 Black.</param>
        /// <param name="italic">Italic style.</param>
        /// <returns>The font.</returns>
        [return: Pin(Name = "Output")]
        public static Font Font(
            [DefaultValue("")] string family,
            [DefaultValue(0.12f)] float size,
            [DefaultValue(400)] int weight,
            bool italic)
            => new() { Family = family ?? string.Empty, Size = size, Weight = weight, Italic = italic };
    }
}
