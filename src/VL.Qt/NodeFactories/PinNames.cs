using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VL.Qt.NodeFactories
{
    /// <summary>The pin naming rule of the generated nodes: one rule for shader uniforms and QML declarations, except that shader files also split before a digit.</summary>
    internal static class PinNames
    {
        private const string split = @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])";

        private const string splitWithDigits = split + @"|(?<=[A-Za-z])(?=[0-9])";

        /// <summary>
        /// Returns the pin name of an identifier: "pixelSize" → "Pixel Size", "pixel_size" → "Pixel Size". With <paramref name="splitDigits"/>
        /// a digit after a letter starts a word too ("pass1" → "Pass 1", as VL.Stride names the pins of a TextureFX); the shader files do.
        /// </summary>
        public static string FromIdentifier(string identifier, bool splitDigits = false)
        {
            var spaced = Regex.Replace(identifier.Replace('_', ' '), splitDigits ? splitWithDigits : split, " ");
            var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        }
    }
}
