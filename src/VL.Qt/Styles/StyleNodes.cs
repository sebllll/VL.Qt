using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Qt.Styles
{
    // Style nodeset (S5). Setters are a chain: Style in on top, Style out; a null input is an empty style and an
    // unconnected Optional pin means "not set". Getters return resolved values (unset → base style) for one state
    // and never throw on a null input. The six bundles (SetColors, SetFrame, SetText, SetIndicator, SetTrack, Merge)
    // are process nodes in Nodes/Style/ that cache their output; the single setters (Set<Field>, Advanced) and all
    // getters stay static nodes here. Colors go through SetColors or the per-part color setters.

    /// <summary>Nodes that read the colors of a style and the state helper of the style nodeset.</summary>
    [SkipCategory]
    [Category("Qt.Style")]
    public static class StyleNodes
    {
        // Shared implementation of SetColors: the six colors of the four parts.
        internal static Style ApplyColors(
            Style? input,
            StyleStates states,
            Optional<Color4> frame,
            Optional<Color4> border,
            Optional<Color4> text,
            Optional<Color4> indicator,
            Optional<Color4> track,
            Optional<Color4> trackFill)
        {
            var style = FrameNodes.Apply(input, states, color: frame, borderColor: border);
            style = TextNodes.Apply(style, states, color: text);
            style = IndicatorNodes.Apply(style, states, color: indicator);
            return TrackNodes.Apply(style, states, color: track, fillColor: trackFill);
        }

        /// <summary>Returns the colors of all parts in one state; unset fields come from the base style.</summary>
        /// <param name="input">Style to read from. Empty or not connected reads the base style.</param>
        /// <param name="state">State to read.</param>
        /// <param name="frame">Background of the control.</param>
        /// <param name="border">Border of the frame.</param>
        /// <param name="text">Text color.</param>
        /// <param name="indicator">Slider handle, check mark, switch knob, SpinBox -/+.</param>
        /// <param name="track">Slider groove, switch track, check box.</param>
        /// <param name="trackFill">Slider value, switch on, text selection.</param>
        public static void Colors(
            Style? input,
            StyleState state,
            out Color4 frame,
            out Color4 border,
            out Color4 text,
            out Color4 indicator,
            out Color4 track,
            out Color4 trackFill)
        {
            frame = FrameNodes.Color(input, state);
            border = FrameNodes.BorderColor(input, state);
            text = TextNodes.Color(input, state);
            indicator = IndicatorNodes.Color(input, state);
            track = TrackNodes.Color(input, state);
            trackFill = TrackNodes.FillColor(input, state);
        }

        /// <summary>Returns the one state a widget shows, with the rank Disabled > Pressed > Hovered > Checked > Normal (like the built-in widgets).</summary>
        /// <param name="isHovered">True while the pointer is over the widget.</param>
        /// <param name="isPressed">True while the widget is pressed.</param>
        /// <param name="isDisabled">True if the widget is disabled.</param>
        /// <param name="isChecked">True if the widget is checked or on.</param>
        /// <returns>The state to read the style in.</returns>
        public static StyleState ResolveState(bool isHovered, bool isPressed, bool isDisabled, bool isChecked)
            => isDisabled ? StyleState.Disabled
                : isPressed ? StyleState.Pressed
                : isHovered ? StyleState.Hovered
                : isChecked ? StyleState.Checked
                : StyleState.Normal;

        // Setter plumbing shared by the parts: sets the value for the states if the pin is connected.
        internal static StateValues<T> Set<T>(StateValues<T> field, Optional<T> value, StyleStates states)
            => value.HasValue ? field.With(states, value.Value) : field;

        // Resolved value: base style under the input, for one state (unset state → Normal → fallback).
        internal static T Resolve<T>(StateValues<T> baseValues, StateValues<T> inputValues, StyleState state, T fallback)
            => StateValues<T>.Merge(baseValues, inputValues).Get(state, fallback);
    }

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
