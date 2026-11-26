using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Qt.Styles
{
    /// <summary>Frame part: background, border, padding and minimum size of the control.</summary>
    [SkipCategory]
    [Category("Qt.Style.Frame")]
    public static class FrameNodes
    {
        internal static Style Apply(
            Style? input,
            StyleStates state,
            Optional<Color4> color = default,
            Optional<Color4> borderColor = default,
            Optional<float> radius = default,
            Optional<float> borderWidth = default,
            Optional<float> paddingX = default,
            Optional<float> paddingY = default,
            Optional<float> minWidth = default,
            Optional<float> minHeight = default)
        {
            if (state == StyleStates.None || !(color.HasValue || borderColor.HasValue || radius.HasValue || borderWidth.HasValue
                || paddingX.HasValue || paddingY.HasValue || minWidth.HasValue || minHeight.HasValue))
                return input ?? Style.Empty;
            var style = input ?? Style.Empty;
            var f = style.Frame ?? new FrameStyle();
            return style with
            {
                Frame = f with
                {
                    Color = StyleNodes.Set(f.Color, color, state),
                    BorderColor = StyleNodes.Set(f.BorderColor, borderColor, state),
                    Radius = StyleNodes.Set(f.Radius, radius, state),
                    BorderWidth = StyleNodes.Set(f.BorderWidth, borderWidth, state),
                    PaddingX = StyleNodes.Set(f.PaddingX, paddingX, state),
                    PaddingY = StyleNodes.Set(f.PaddingY, paddingY, state),
                    MinWidth = StyleNodes.Set(f.MinWidth, minWidth, state),
                    MinHeight = StyleNodes.Set(f.MinHeight, minHeight, state),
                },
            };
        }

        /// <summary>Returns the shape of the frame in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <param name="radius">Corner radius in DIP.</param>
        /// <param name="borderWidth">Border width in DIP, 0 = no border.</param>
        /// <param name="paddingX">Space between frame and content, left and right, in DIP.</param>
        /// <param name="paddingY">Space between frame and content, top and bottom, in DIP.</param>
        /// <param name="minWidth">Minimum width of the control in DIP.</param>
        /// <param name="minHeight">Minimum height of the control in DIP.</param>
        public static void Frame(
            Style? input,
            StyleState state,
            out float radius,
            out float borderWidth,
            out float paddingX,
            out float paddingY,
            out float minWidth,
            out float minHeight)
        {
            radius = Radius(input, state);
            borderWidth = BorderWidth(input, state);
            paddingX = PaddingX(input, state);
            paddingY = PaddingY(input, state);
            minWidth = MinWidth(input, state);
            minHeight = MinHeight(input, state);
        }

        /// <summary>Sets the background color of the frame for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="color">The background color of the frame. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetColor(Style? input, Optional<Color4> color = default, StyleStates states = StyleStates.All)
            => Apply(input, states, color: color);
        /// <summary>Sets the border color of the frame for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="borderColor">The border color of the frame. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetBorderColor(Style? input, Optional<Color4> borderColor = default, StyleStates states = StyleStates.All)
            => Apply(input, states, borderColor: borderColor);
        /// <summary>Sets the corner radius of the frame in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="radius">The corner radius of the frame in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetRadius(Style? input, Optional<float> radius = default, StyleStates states = StyleStates.All)
            => Apply(input, states, radius: radius);
        /// <summary>Sets the border width of the frame in DIP, 0 = no border for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="borderWidth">The border width of the frame in DIP, 0 = no border. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetBorderWidth(Style? input, Optional<float> borderWidth = default, StyleStates states = StyleStates.All)
            => Apply(input, states, borderWidth: borderWidth);
        /// <summary>Sets the horizontal padding between the frame and its content in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="paddingX">The horizontal padding between the frame and its content in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetPaddingX(Style? input, Optional<float> paddingX = default, StyleStates states = StyleStates.All)
            => Apply(input, states, paddingX: paddingX);
        /// <summary>Sets the vertical padding between the frame and its content in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="paddingY">The vertical padding between the frame and its content in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetPaddingY(Style? input, Optional<float> paddingY = default, StyleStates states = StyleStates.All)
            => Apply(input, states, paddingY: paddingY);
        /// <summary>Sets the minimum width of the control in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="minWidth">The minimum width of the control in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetMinWidth(Style? input, Optional<float> minWidth = default, StyleStates states = StyleStates.All)
            => Apply(input, states, minWidth: minWidth);
        /// <summary>Sets the minimum height of the control in DIP for the given states.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="minHeight">The minimum height of the control in DIP. Unconnected stays unset (inherited).</param>
        /// <param name="states">States the value applies to, combinable with |.</param>
        /// <returns>The style with the value set.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Style SetMinHeight(Style? input, Optional<float> minHeight = default, StyleStates states = StyleStates.All)
            => Apply(input, states, minHeight: minHeight);
        /// <summary>Returns the background color of the frame in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The background color of the frame.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 Color(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.Color, input?.Frame?.Color ?? default, state, default);
        /// <summary>Returns the border color of the frame in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The border color of the frame.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 BorderColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.BorderColor, input?.Frame?.BorderColor ?? default, state, default);
        /// <summary>Returns the corner radius of the frame in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The corner radius of the frame in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float Radius(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.Radius, input?.Frame?.Radius ?? default, state, 0);
        /// <summary>Returns the border width of the frame in DIP, 0 = no border in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The border width of the frame in DIP, 0 = no border.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float BorderWidth(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.BorderWidth, input?.Frame?.BorderWidth ?? default, state, 0);
        /// <summary>Returns the horizontal padding between the frame and its content in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The horizontal padding between the frame and its content in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float PaddingX(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.PaddingX, input?.Frame?.PaddingX ?? default, state, 0);
        /// <summary>Returns the vertical padding between the frame and its content in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The vertical padding between the frame and its content in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float PaddingY(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.PaddingY, input?.Frame?.PaddingY ?? default, state, 0);
        /// <summary>Returns the minimum width of the control in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The minimum width of the control in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float MinWidth(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.MinWidth, input?.Frame?.MinWidth ?? default, state, 0);
        /// <summary>Returns the minimum height of the control in DIP in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <returns>The minimum height of the control in DIP.</returns>
        [Smell(SymbolSmell.Advanced)]
        public static float MinHeight(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.MinHeight, input?.Frame?.MinHeight ?? default, state, 0);
    }
}
