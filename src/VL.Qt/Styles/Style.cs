using System;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;

namespace VL.Qt.Styles
{
    /// <summary>One style state: used by getters and by <see cref="StateValues{T}.Get"/>.</summary>
    [Category("Qt.Style")]
    public enum StyleState : byte
    {
        Normal = Protocol.StyleState.Normal,
        Hovered = Protocol.StyleState.Hovered,
        Pressed = Protocol.StyleState.Pressed,
        Disabled = Protocol.StyleState.Disabled,
        Checked = Protocol.StyleState.Checked,
    }

    /// <summary>Set of style states for setters, combinable with |. Default of the setters is <see cref="All"/>.</summary>
    [Flags]
    [Category("Qt.Style")]
    public enum StyleStates
    {
        None = 0,
        Normal = 1 << Protocol.StyleState.Normal,
        Hovered = 1 << Protocol.StyleState.Hovered,
        Pressed = 1 << Protocol.StyleState.Pressed,
        Disabled = 1 << Protocol.StyleState.Disabled,
        Checked = 1 << Protocol.StyleState.Checked,
        All = Normal | Hovered | Pressed | Disabled | Checked,
    }

    /// <summary>
    /// One style field in all five states. An unset state inherits from the parent style (merge); when the
    /// resolved style is read, an unset state falls back to Normal (like QtNative).
    /// </summary>
    internal readonly record struct StateValues<T>(
        Optional<T> Normal,
        Optional<T> Hovered,
        Optional<T> Pressed,
        Optional<T> Disabled,
        Optional<T> Checked)
    {
        /// <summary>The value of <paramref name="state"/> as stored (no fallback).</summary>
        public Optional<T> this[StyleState state] => state switch
        {
            StyleState.Normal => Normal,
            StyleState.Hovered => Hovered,
            StyleState.Pressed => Pressed,
            StyleState.Disabled => Disabled,
            StyleState.Checked => Checked,
            _ => default,
        };

        /// <summary>True if no state is set.</summary>
        public bool IsEmpty => !Normal.HasValue && !Hovered.HasValue && !Pressed.HasValue && !Disabled.HasValue && !Checked.HasValue;

        /// <summary>Copy with <paramref name="value"/> set for every state in <paramref name="states"/>.</summary>
        public StateValues<T> With(StyleStates states, T value)
        {
            var v = new Optional<T>(value);
            return new StateValues<T>(
                (states & StyleStates.Normal) != 0 ? v : Normal,
                (states & StyleStates.Hovered) != 0 ? v : Hovered,
                (states & StyleStates.Pressed) != 0 ? v : Pressed,
                (states & StyleStates.Disabled) != 0 ? v : Disabled,
                (states & StyleStates.Checked) != 0 ? v : Checked);
        }

        /// <summary>Value of <paramref name="state"/>, else Normal, else <paramref name="fallback"/> (QtNative's rule).</summary>
        public T Get(StyleState state, T fallback)
        {
            var v = this[state];
            if (v.HasValue)
                return v.Value;
            return Normal.HasValue ? Normal.Value : fallback;
        }

        /// <summary>Merge rule (S1): per state, a set value of <paramref name="over"/> wins, else <paramref name="under"/>.</summary>
        public static StateValues<T> Merge(in StateValues<T> under, in StateValues<T> over) => new(
            over.Normal.HasValue ? over.Normal : under.Normal,
            over.Hovered.HasValue ? over.Hovered : under.Hovered,
            over.Pressed.HasValue ? over.Pressed : under.Pressed,
            over.Disabled.HasValue ? over.Disabled : under.Disabled,
            over.Checked.HasValue ? over.Checked : under.Checked);
    }

    /// <summary>Frame part of a style (background of the control).</summary>
    internal sealed record FrameStyle
    {
        public StateValues<Color4> Color { get; init; }
        public StateValues<Color4> BorderColor { get; init; }
        public StateValues<float> Radius { get; init; }
        public StateValues<float> BorderWidth { get; init; }
        public StateValues<float> PaddingX { get; init; }
        public StateValues<float> PaddingY { get; init; }
        public StateValues<float> MinWidth { get; init; }
        public StateValues<float> MinHeight { get; init; }
    }

    /// <summary>Text part of a style. Font size in px; font family "" = Qt's default font.</summary>
    internal sealed record TextStyle
    {
        public StateValues<Color4> Color { get; init; }
        public StateValues<string> FontFamily { get; init; }
        public StateValues<float> FontSize { get; init; }
        public StateValues<int> FontWeight { get; init; }
    }

    /// <summary>Indicator part: slider handle, check mark, switch knob, SpinBox -/+.</summary>
    internal sealed record IndicatorStyle
    {
        public StateValues<Color4> Color { get; init; }
        public StateValues<float> Size { get; init; }
        public StateValues<float> Radius { get; init; }
        public StateValues<float> BorderWidth { get; init; }
    }

    /// <summary>Track part: slider groove, switch track, check box; FillColor = slider value, switch on, text selection.</summary>
    internal sealed record TrackStyle
    {
        public StateValues<Color4> Color { get; init; }
        public StateValues<Color4> FillColor { get; init; }
        public StateValues<float> Thickness { get; init; }
        public StateValues<float> Radius { get; init; }
    }

    /// <summary>
    /// Look of the widgets: immutable, every field <see cref="Optional{T}"/> per state, unset = inherit from the
    /// parent (Group). Resolved top-down: base style → Group → … → widget (<see cref="StyleMerge"/>). A null part
    /// means "nothing set" in that part. Deep value equality. Built and read with the Qt.Style nodes;
    /// the parts are internal so that only the nodeset shows in the node browser.
    /// </summary>
    [Category("Qt.Style")]
    public sealed record Style
    {
        /// <summary>The style with nothing set.</summary>
        internal static readonly Style Empty = new();

        // Built only through the Qt.Style nodes, so no constructor node.
        internal Style()
        {
        }

        internal FrameStyle? Frame { get; init; }
        internal TextStyle? Text { get; init; }
        internal IndicatorStyle? Indicator { get; init; }
        internal TrackStyle? Track { get; init; }
    }
}
