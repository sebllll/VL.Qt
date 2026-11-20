using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Qt.Styles
{
    // Style nodeset (S5). Setters are a chain: Style in on top, Style out; a null input is an empty style and an
    // unconnected Optional pin means "not set". Getters return resolved values (unset → base style) for one state
    // and never throw on a null input. Colors go through Colors (Qt.Style) or the per-part color setters (Advanced).

    /// <summary>Nodes that combine styles and the state helper of the style nodeset.</summary>
    [SkipCategory]
    [Category("Qt.Style")]
    public static class StyleNodes
    {
        /// <summary>Sets the colors of all parts for the given states. Unconnected pins stay unset (inherited).</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="frame">Background of the control.</param>
        /// <param name="border">Border of the frame.</param>
        /// <param name="text">Text color.</param>
        /// <param name="indicator">Slider handle, check mark, switch knob, SpinBox -/+.</param>
        /// <param name="track">Slider groove, switch track, check box.</param>
        /// <param name="trackFill">Slider value, switch on, text selection.</param>
        /// <param name="state">States the values apply to, combinable with |.</param>
        public static Style Colors(
            Style? input,
            Optional<Color4> frame = default,
            Optional<Color4> border = default,
            Optional<Color4> text = default,
            Optional<Color4> indicator = default,
            Optional<Color4> track = default,
            Optional<Color4> trackFill = default,
            StyleStates state = StyleStates.All)
        {
            var style = FrameNodes.Apply(input, state, color: frame, borderColor: border);
            style = TextNodes.Apply(style, state, color: text);
            style = IndicatorNodes.Apply(style, state, color: indicator);
            return TrackNodes.Apply(style, state, color: track, fillColor: trackFill);
        }

        /// <summary>Resolved colors of all parts in one state (unset → base style).</summary>
        public static void GetColors(
            Style? input,
            StyleState state,
            out Color4 frame,
            out Color4 border,
            out Color4 text,
            out Color4 indicator,
            out Color4 track,
            out Color4 trackFill)
        {
            frame = FrameNodes.GetColor(input, state);
            border = FrameNodes.GetBorderColor(input, state);
            text = TextNodes.GetColor(input, state);
            indicator = IndicatorNodes.GetColor(input, state);
            track = TrackNodes.GetColor(input, state);
            trackFill = TrackNodes.GetFillColor(input, state);
        }

        /// <summary>Lays <paramref name="input2"/> over <paramref name="input"/>: field by field and state by state, a set value of Input 2 wins (the rule of Group and widget).</summary>
        /// <param name="input">Lower style, e.g. a preset.</param>
        /// <param name="input2">Upper style, e.g. overrides from another branch.</param>
        public static Style Merge(Style? input, Style? input2)
            => StyleMerge.Merge(input, input2);

        /// <summary>The one state a widget shows, with the rank Disabled > Pressed > Hovered > Checked > Normal (like the built-in widgets).</summary>
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
        /// <summary>Sets the shape of the frame for the given states. Unconnected pins stay unset (inherited). Colors: Colors or Color/BorderColor.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="radius">Corner radius.</param>
        /// <param name="borderWidth">Border width, 0 = no border.</param>
        /// <param name="paddingX">Space between frame and content, left and right.</param>
        /// <param name="paddingY">Space between frame and content, top and bottom.</param>
        /// <param name="minWidth">Minimum width of the control.</param>
        /// <param name="minHeight">Minimum height of the control.</param>
        /// <param name="state">States the values apply to, combinable with |.</param>
        public static Style Frame(
            Style? input,
            Optional<float> radius = default,
            Optional<float> borderWidth = default,
            Optional<float> paddingX = default,
            Optional<float> paddingY = default,
            Optional<float> minWidth = default,
            Optional<float> minHeight = default,
            StyleStates state = StyleStates.All)
            => Apply(input, state, radius: radius, borderWidth: borderWidth, paddingX: paddingX, paddingY: paddingY, minWidth: minWidth, minHeight: minHeight);

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

        /// <summary>Resolved frame shape in one state (unset → base style).</summary>
        public static void GetFrame(
            Style? input,
            StyleState state,
            out float radius,
            out float borderWidth,
            out float paddingX,
            out float paddingY,
            out float minWidth,
            out float minHeight)
        {
            radius = GetRadius(input, state);
            borderWidth = GetBorderWidth(input, state);
            paddingX = GetPaddingX(input, state);
            paddingY = GetPaddingY(input, state);
            minWidth = GetMinWidth(input, state);
            minHeight = GetMinHeight(input, state);
        }

        /// <summary>Sets color for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Color(Style? input, Optional<Color4> color = default, StyleStates state = StyleStates.All)
            => Apply(input, state, color: color);
        /// <summary>Sets borderColor for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style BorderColor(Style? input, Optional<Color4> borderColor = default, StyleStates state = StyleStates.All)
            => Apply(input, state, borderColor: borderColor);
        /// <summary>Sets radius for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Radius(Style? input, Optional<float> radius = default, StyleStates state = StyleStates.All)
            => Apply(input, state, radius: radius);
        /// <summary>Sets borderWidth for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style BorderWidth(Style? input, Optional<float> borderWidth = default, StyleStates state = StyleStates.All)
            => Apply(input, state, borderWidth: borderWidth);
        /// <summary>Sets paddingX for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style PaddingX(Style? input, Optional<float> paddingX = default, StyleStates state = StyleStates.All)
            => Apply(input, state, paddingX: paddingX);
        /// <summary>Sets paddingY for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style PaddingY(Style? input, Optional<float> paddingY = default, StyleStates state = StyleStates.All)
            => Apply(input, state, paddingY: paddingY);
        /// <summary>Sets minWidth for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style MinWidth(Style? input, Optional<float> minWidth = default, StyleStates state = StyleStates.All)
            => Apply(input, state, minWidth: minWidth);
        /// <summary>Sets minHeight for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style MinHeight(Style? input, Optional<float> minHeight = default, StyleStates state = StyleStates.All)
            => Apply(input, state, minHeight: minHeight);

        /// <summary>Resolved color in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 GetColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.Color, input?.Frame?.Color ?? default, state, default);
        /// <summary>Resolved borderColor in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 GetBorderColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.BorderColor, input?.Frame?.BorderColor ?? default, state, default);
        /// <summary>Resolved radius in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetRadius(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.Radius, input?.Frame?.Radius ?? default, state, 0);
        /// <summary>Resolved borderWidth in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetBorderWidth(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.BorderWidth, input?.Frame?.BorderWidth ?? default, state, 0);
        /// <summary>Resolved paddingX in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetPaddingX(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.PaddingX, input?.Frame?.PaddingX ?? default, state, 0);
        /// <summary>Resolved paddingY in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetPaddingY(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.PaddingY, input?.Frame?.PaddingY ?? default, state, 0);
        /// <summary>Resolved minWidth in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetMinWidth(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.MinWidth, input?.Frame?.MinWidth ?? default, state, 0);
        /// <summary>Resolved minHeight in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetMinHeight(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Frame!.MinHeight, input?.Frame?.MinHeight ?? default, state, 0);
    }

    /// <summary>Text part: font of the control's label or input text.</summary>
    [SkipCategory]
    [Category("Qt.Style.Text")]
    public static class TextNodes
    {
        /// <summary>Sets the font for the given states. Unconnected pins stay unset (inherited). Color: Colors or Color.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="fontFamily">Font family, "" = Qt's default font.</param>
        /// <param name="fontSize">Font size in px.</param>
        /// <param name="fontWeight">Font weight 100–900 (400 normal, 700 bold).</param>
        /// <param name="state">States the values apply to, combinable with |.</param>
        public static Style Text(
            Style? input,
            Optional<string> fontFamily = default,
            Optional<float> fontSize = default,
            Optional<int> fontWeight = default,
            StyleStates state = StyleStates.All)
            => Apply(input, state, fontFamily: fontFamily, fontSize: fontSize, fontWeight: fontWeight);

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

        /// <summary>Resolved font in one state (unset → base style).</summary>
        public static void GetText(Style? input, StyleState state, out string fontFamily, out float fontSize, out int fontWeight)
        {
            fontFamily = GetFontFamily(input, state);
            fontSize = GetFontSize(input, state);
            fontWeight = GetFontWeight(input, state);
        }

        /// <summary>Sets color for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Color(Style? input, Optional<Color4> color = default, StyleStates state = StyleStates.All)
            => Apply(input, state, color: color);
        /// <summary>Sets fontFamily for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style FontFamily(Style? input, Optional<string> fontFamily = default, StyleStates state = StyleStates.All)
            => Apply(input, state, fontFamily: fontFamily);
        /// <summary>Sets fontSize for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style FontSize(Style? input, Optional<float> fontSize = default, StyleStates state = StyleStates.All)
            => Apply(input, state, fontSize: fontSize);
        /// <summary>Sets fontWeight for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style FontWeight(Style? input, Optional<int> fontWeight = default, StyleStates state = StyleStates.All)
            => Apply(input, state, fontWeight: fontWeight);

        /// <summary>Resolved color in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 GetColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.Color, input?.Text?.Color ?? default, state, default);
        /// <summary>Resolved fontFamily in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static string GetFontFamily(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.FontFamily, input?.Text?.FontFamily ?? default, state, "");
        /// <summary>Resolved fontSize in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetFontSize(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.FontSize, input?.Text?.FontSize ?? default, state, 0);
        /// <summary>Resolved fontWeight in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static int GetFontWeight(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Text!.FontWeight, input?.Text?.FontWeight ?? default, state, 0);
    }

    /// <summary>Indicator part: slider handle, check mark, switch knob, SpinBox -/+.</summary>
    [SkipCategory]
    [Category("Qt.Style.Indicator")]
    public static class IndicatorNodes
    {
        /// <summary>Sets the shape of the indicator for the given states. Unconnected pins stay unset (inherited). Color: Colors or Color.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="size">Size of the handle or knob.</param>
        /// <param name="radius">Corner radius.</param>
        /// <param name="borderWidth">Border width, 0 = no border.</param>
        /// <param name="state">States the values apply to, combinable with |.</param>
        public static Style Indicator(
            Style? input,
            Optional<float> size = default,
            Optional<float> radius = default,
            Optional<float> borderWidth = default,
            StyleStates state = StyleStates.All)
            => Apply(input, state, size: size, radius: radius, borderWidth: borderWidth);

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

        /// <summary>Resolved indicator shape in one state (unset → base style).</summary>
        public static void GetIndicator(Style? input, StyleState state, out float size, out float radius, out float borderWidth)
        {
            size = GetSize(input, state);
            radius = GetRadius(input, state);
            borderWidth = GetBorderWidth(input, state);
        }

        /// <summary>Sets color for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Color(Style? input, Optional<Color4> color = default, StyleStates state = StyleStates.All)
            => Apply(input, state, color: color);
        /// <summary>Sets size for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Size(Style? input, Optional<float> size = default, StyleStates state = StyleStates.All)
            => Apply(input, state, size: size);
        /// <summary>Sets radius for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Radius(Style? input, Optional<float> radius = default, StyleStates state = StyleStates.All)
            => Apply(input, state, radius: radius);
        /// <summary>Sets borderWidth for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style BorderWidth(Style? input, Optional<float> borderWidth = default, StyleStates state = StyleStates.All)
            => Apply(input, state, borderWidth: borderWidth);

        /// <summary>Resolved color in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 GetColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.Color, input?.Indicator?.Color ?? default, state, default);
        /// <summary>Resolved size in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetSize(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.Size, input?.Indicator?.Size ?? default, state, 0);
        /// <summary>Resolved radius in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetRadius(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.Radius, input?.Indicator?.Radius ?? default, state, 0);
        /// <summary>Resolved borderWidth in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetBorderWidth(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Indicator!.BorderWidth, input?.Indicator?.BorderWidth ?? default, state, 0);
    }

    /// <summary>Track part: slider groove, switch track, check box; the fill shows the value.</summary>
    [SkipCategory]
    [Category("Qt.Style.Track")]
    public static class TrackNodes
    {
        /// <summary>Sets the shape of the track for the given states. Unconnected pins stay unset (inherited). Colors: Colors or Color/FillColor.</summary>
        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="thickness">Thickness of the groove or track.</param>
        /// <param name="radius">Corner radius.</param>
        /// <param name="state">States the values apply to, combinable with |.</param>
        public static Style Track(
            Style? input,
            Optional<float> thickness = default,
            Optional<float> radius = default,
            StyleStates state = StyleStates.All)
            => Apply(input, state, thickness: thickness, radius: radius);

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

        /// <summary>Resolved track shape in one state (unset → base style).</summary>
        public static void GetTrack(Style? input, StyleState state, out float thickness, out float radius)
        {
            thickness = GetThickness(input, state);
            radius = GetRadius(input, state);
        }

        /// <summary>Sets color for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Color(Style? input, Optional<Color4> color = default, StyleStates state = StyleStates.All)
            => Apply(input, state, color: color);
        /// <summary>Sets fillColor for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style FillColor(Style? input, Optional<Color4> fillColor = default, StyleStates state = StyleStates.All)
            => Apply(input, state, fillColor: fillColor);
        /// <summary>Sets thickness for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Thickness(Style? input, Optional<float> thickness = default, StyleStates state = StyleStates.All)
            => Apply(input, state, thickness: thickness);
        /// <summary>Sets radius for the given states.</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Style Radius(Style? input, Optional<float> radius = default, StyleStates state = StyleStates.All)
            => Apply(input, state, radius: radius);

        /// <summary>Resolved color in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 GetColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.Color, input?.Track?.Color ?? default, state, default);
        /// <summary>Resolved fillColor in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static Color4 GetFillColor(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.FillColor, input?.Track?.FillColor ?? default, state, default);
        /// <summary>Resolved thickness in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetThickness(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.Thickness, input?.Track?.Thickness ?? default, state, 0);
        /// <summary>Resolved radius in one state (unset → base style).</summary>
        [Smell(SymbolSmell.Advanced)]
        public static float GetRadius(Style? input, StyleState state = StyleState.Normal)
            => StyleNodes.Resolve(BaseStyle.Default.Track!.Radius, input?.Track?.Radius ?? default, state, 0);
    }
}
