using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Qt.Styles
{
    /// <summary>Text part: font of the control's label or input text.</summary>
    [SkipCategory]
    [Category("Qt.Style.Text")]
    public static class TextNodes
    {
        internal static Style Apply(
            Style? input,
            StyleStates state,
            Optional<Color4> color = default,
            Optional<string> fontFamily = default,
            Optional<float> fontSize = default,
            Optional<int> fontWeight = default)
        {
            if (state == StyleStates.None || !(color.HasValue || fontFamily.HasValue || fontSize.HasValue || fontWeight.HasValue))
                return input ?? Style.Empty;
            var style = input ?? Style.Empty;
            var t = style.Text ?? new TextStyle();
            return style with
            {
                Text = t with
                {
                    Color = StyleNodes.Set(t.Color, color, state),
                    FontFamily = StyleNodes.Set(t.FontFamily, fontFamily, state),
                    FontSize = StyleNodes.Set(t.FontSize, fontSize, state),
                    FontWeight = StyleNodes.Set(t.FontWeight, fontWeight, state),
                },
            };
        }

        /// <summary>Returns the font of the text in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <param name="fontFamily">Font family, empty = the default font.</param>
        /// <param name="fontSize">Font size in DIP.</param>
        /// <param name="fontWeight">Font weight, 1..1000 (400 Normal, 700 Bold).</param>
        public static void Text(Style? input, StyleState state, out string fontFamily, out float fontSize, out int fontWeight)
        {
            fontFamily = FontFamily(input, state);
            fontSize = FontSize(input, state);
            fontWeight = FontWeight(input, state);
        }

        /// <summary>Sets the text color for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="color">The text color. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetColor(Style? input, Optional<Color4> color = default, StyleStates states = StyleStates.All)
            => Apply(input, states, color: color);
        /// <summary>Sets the font family of the text, empty = the default font for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="fontFamily">The font family of the text, empty = the default font. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetFontFamily(Style? input, Optional<string> fontFamily = default, StyleStates states = StyleStates.All)
            => Apply(input, states, fontFamily: fontFamily);
        /// <summary>Sets the font size of the text in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="fontSize">The font size of the text in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetFontSize(Style? input, Optional<float> fontSize = default, StyleStates states = StyleStates.All)
            => Apply(input, states, fontSize: fontSize);
        /// <summary>Sets the font weight of the text, 1..1000 (400 Normal, 700 Bold) for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="fontWeight">The font weight of the text, 1..1000 (400 Normal, 700 Bold). Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetFontWeight(Style? input, Optional<int> fontWeight = default, StyleStates states = StyleStates.All)
            => Apply(input, states, fontWeight: fontWeight);
        /// <summary>Returns the text color in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The text color.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 Color(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.Color, input?.Text?.Color ?? default, state, default);
        /// <summary>Returns the font family of the text, empty = the default font in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The font family of the text, empty = the default font.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static string FontFamily(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.FontFamily, input?.Text?.FontFamily ?? default, state, "");
        /// <summary>Returns the font size of the text in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The font size of the text in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float FontSize(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.FontSize, input?.Text?.FontSize ?? default, state, 0);
        /// <summary>Returns the font weight of the text, 1..1000 (400 Normal, 700 Bold) in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The font weight of the text, 1..1000 (400 Normal, 700 Bold).</returns>
        [Smell(SymbolSmell.Advanced)]
        public static int FontWeight(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.FontWeight, input?.Text?.FontWeight ?? default, state, 0);
    }
}
