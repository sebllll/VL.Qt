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
}
