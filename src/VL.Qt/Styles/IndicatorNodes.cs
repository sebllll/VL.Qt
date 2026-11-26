using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Qt.Styles
{
    /// <summary>Indicator part: slider handle, check mark, switch knob, SpinBox -/+.</summary>
    [SkipCategory]
    [Category("Qt.Style.Indicator")]
    public static class IndicatorNodes
    {
        internal static Style Apply(
            Style? input,
            StyleStates state,
            Optional<Color4> color = default,
            Optional<float> size = default,
            Optional<float> radius = default,
            Optional<float> borderWidth = default)
        {
            if (state == StyleStates.None || !(color.HasValue || size.HasValue || radius.HasValue || borderWidth.HasValue))
                return input ?? Style.Empty;
            var style = input ?? Style.Empty;
            var i = style.Indicator ?? new IndicatorStyle();
            return style with
            {
                Indicator = i with
                {
                    Color = StyleNodes.Set(i.Color, color, state),
                    Size = StyleNodes.Set(i.Size, size, state),
                    Radius = StyleNodes.Set(i.Radius, radius, state),
                    BorderWidth = StyleNodes.Set(i.BorderWidth, borderWidth, state),
                },
            };
        }

        /// <summary>Returns the shape of the indicator in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <param name="size">Size of the handle or knob in DIP.</param>
        /// <param name="radius">Corner radius in DIP.</param>
        /// <param name="borderWidth">Border width in DIP, 0 = no border.</param>
        public static void Indicator(Style? input, StyleState state, out float size, out float radius, out float borderWidth)
        {
            size = Size(input, state);
            radius = Radius(input, state);
            borderWidth = BorderWidth(input, state);
        }

        /// <summary>Sets the color of the indicator (slider handle, check mark, switch knob, SpinBox -/+) for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="color">The color of the indicator (slider handle, check mark, switch knob, SpinBox -/+). Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetColor(Style? input, Optional<Color4> color = default, StyleStates states = StyleStates.All)
            => Apply(input, states, color: color);
        /// <summary>Sets the size of the indicator in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="size">The size of the indicator in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetSize(Style? input, Optional<float> size = default, StyleStates states = StyleStates.All)
            => Apply(input, states, size: size);
        /// <summary>Sets the corner radius of the indicator in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="radius">The corner radius of the indicator in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetRadius(Style? input, Optional<float> radius = default, StyleStates states = StyleStates.All)
            => Apply(input, states, radius: radius);
        /// <summary>Sets the border width of the indicator in DIP, 0 = no border for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="borderWidth">The border width of the indicator in DIP, 0 = no border. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetBorderWidth(Style? input, Optional<float> borderWidth = default, StyleStates states = StyleStates.All)
            => Apply(input, states, borderWidth: borderWidth);
        /// <summary>Returns the color of the indicator (slider handle, check mark, switch knob, SpinBox -/+) in one state; unset fields
        /// come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The color of the indicator (slider handle, check mark, switch knob, SpinBox -/+).</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 Color(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.Color, input?.Indicator?.Color ?? default, state, default);
        /// <summary>Returns the size of the indicator in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The size of the indicator in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float Size(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.Size, input?.Indicator?.Size ?? default, state, 0);
        /// <summary>Returns the corner radius of the indicator in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The corner radius of the indicator in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float Radius(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.Radius, input?.Indicator?.Radius ?? default, state, 0);
        /// <summary>Returns the border width of the indicator in DIP, 0 = no border in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The border width of the indicator in DIP, 0 = no border.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float BorderWidth(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.BorderWidth, input?.Indicator?.BorderWidth ?? default, state, 0);
    }
}
