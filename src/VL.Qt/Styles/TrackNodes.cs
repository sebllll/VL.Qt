using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Qt.Styles
{
    /// <summary>Track part: slider groove, switch track, check box; the fill shows the value.</summary>
    [SkipCategory]
    [Category("Qt.Style.Track")]
    public static class TrackNodes
    {
        internal static Style Apply(
            Style? input,
            StyleStates state,
            Optional<Color4> color = default,
            Optional<Color4> fillColor = default,
            Optional<float> thickness = default,
            Optional<float> radius = default)
        {
            if (state == StyleStates.None || !(color.HasValue || fillColor.HasValue || thickness.HasValue || radius.HasValue))
                return input ?? Style.Empty;
            var style = input ?? Style.Empty;
            var t = style.Track ?? new TrackStyle();
            return style with
            {
                Track = t with
                {
                    Color = StyleNodes.Set(t.Color, color, state),
                    FillColor = StyleNodes.Set(t.FillColor, fillColor, state),
                    Thickness = StyleNodes.Set(t.Thickness, thickness, state),
                    Radius = StyleNodes.Set(t.Radius, radius, state),
                },
            };
        }

        /// <summary>Returns the shape of the track in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <param name="thickness">Thickness of the groove or track in DIP.</param>
        /// <param name="radius">Corner radius in DIP.</param>
        public static void Track(Style? input, StyleState state, out float thickness, out float radius)
        {
            thickness = Thickness(input, state);
            radius = Radius(input, state);
        }

        /// <summary>Sets the color of the track (slider groove, switch track, check box) for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="color">The color of the track (slider groove, switch track, check box). Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetColor(Style? input, Optional<Color4> color = default, StyleStates states = StyleStates.All)
            => Apply(input, states, color: color);
        /// <summary>Sets the fill color of the track (slider value, switch on, text selection) for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="fillColor">The fill color of the track (slider value, switch on, text selection). Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetFillColor(Style? input, Optional<Color4> fillColor = default, StyleStates states = StyleStates.All)
            => Apply(input, states, fillColor: fillColor);
        /// <summary>Sets the thickness of the track in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="thickness">The thickness of the track in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetThickness(Style? input, Optional<float> thickness = default, StyleStates states = StyleStates.All)
            => Apply(input, states, thickness: thickness);
        /// <summary>Sets the corner radius of the track in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="radius">The corner radius of the track in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetRadius(Style? input, Optional<float> radius = default, StyleStates states = StyleStates.All)
            => Apply(input, states, radius: radius);
        /// <summary>Returns the color of the track (slider groove, switch track, check box) in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The color of the track (slider groove, switch track, check box).</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 Color(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.Color, input?.Track?.Color ?? default, state, default);
        /// <summary>Returns the fill color of the track (slider value, switch on, text selection) in one state; unset fields come from the
        /// base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The fill color of the track (slider value, switch on, text selection).</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 FillColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.FillColor, input?.Track?.FillColor ?? default, state, default);
        /// <summary>Returns the thickness of the track in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The thickness of the track in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float Thickness(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.Thickness, input?.Track?.Thickness ?? default, state, 0);
        /// <summary>Returns the corner radius of the track in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The corner radius of the track in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float Radius(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.Radius, input?.Track?.Radius ?? default, state, 0);
    }
}
