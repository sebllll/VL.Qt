using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>Sets the font for the given states. Unconnected pins stay unset (inherited). Color: SetColors or SetColor.</summary>
    [ProcessNode(Name = "SetText", Category = "Qt.Style.Text")]
    public sealed class SetTextNode
    {
        private readonly record struct Key(Optional<string> fontFamily, Optional<float> fontSize, Optional<int> fontWeight, StyleStates states);

        private StyleCache<Key> _cache;

        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="fontFamily">Font family, empty = the default font.</param>
        /// <param name="fontSize">Font size in DIP.</param>
        /// <param name="fontWeight">Font weight, 1..1000 (variable fonts take every value, other fonts the nearest face). Named steps: 100 Thin, 200 ExtraLight, 300 Light, 400 Normal, 500 Medium, 600 DemiBold, 700 Bold, 800 ExtraBold, 900 Black.</param>
        /// <param name="states">States the values apply to, combinable with |.</param>
        /// <returns>The style with the values set. The same style as in the last frame while no input changed.</returns>
        [return: Pin(Name = "Output")]
        public Style Update(
            Style? input,
            Optional<string> fontFamily,
            Optional<float> fontSize,
            Optional<int> fontWeight,
            [DefaultValue(typeof(StyleStates), nameof(StyleStates.All))] StyleStates states)
        {
            var key = new Key(fontFamily, fontSize, fontWeight, states);
            return _cache.Get(input, key) ?? _cache.Store(input, key, TextNodes.Apply(input, states, fontFamily: fontFamily, fontSize: fontSize, fontWeight: fontWeight));
        }
    }
}
