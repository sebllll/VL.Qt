using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VL.Qt.NodeFactories
{
    /// <summary>The pin naming rule of the generated nodes: one rule for shader uniforms and QML declarations.</summary>
    internal static class PinNames
    {
        /// <summary>Returns the pin name of an identifier: "pixelSize" → "Pixel Size", "pixel_size" → "Pixel Size".</summary>
        public static string FromIdentifier(string identifier)
        {
            var spaced = Regex.Replace(identifier.Replace('_', ' '), @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        }
    }
}
