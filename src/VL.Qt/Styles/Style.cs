using System;
using System.Collections.Generic;
using System.Text;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Wire;

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

        /// <summary>The states that are set as text for <see cref="Style.ToString"/>: "All" or a list like "Normal,Hovered".</summary>
        public string StatesText()
        {
            if (Normal.HasValue && Hovered.HasValue && Pressed.HasValue && Disabled.HasValue && Checked.HasValue)
                return "All";
            var names = new List<string>(5);
            for (var s = StyleState.Normal; s <= StyleState.Checked; s++)
                if (this[s].HasValue)
                    names.Add(s.ToString());
            return string.Join(",", names);
        }
    }

    /// <summary>
    /// Counts per field of a part merge whether the result equals the lower or the upper part, so that the merge can
    /// return one of them unchanged. A field that does not go through <see cref="Pick"/> is missing in the result
    /// and is noticed by the per-field merge test.
    /// </summary>
    internal struct MergeTally
    {
        private int _fields;
        private int _equalUnder;
        private int _equalOver;

        /// <summary><see cref="StateValues{T}.Merge"/> of one field, counted.</summary>
        public StateValues<T> Pick<T>(in StateValues<T> under, in StateValues<T> over)
        {
            var m = StateValues<T>.Merge(under, over);
            _fields++;
            if (m == under)
                _equalUnder++;
            if (m == over)
                _equalOver++;
            return m;
        }

        /// <summary>True if every field of the result equals the lower part.</summary>
        public bool AllUnder => _equalUnder == _fields;

        /// <summary>True if every field of the result equals the upper part.</summary>
        public bool AllOver => _equalOver == _fields;
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

        /// <summary>Lists the set entries compactly, e.g. <c>Frame.Color[Normal,Hovered], Text.FontSize[All]</c>.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            Frame?.Describe(sb);
            Text?.Describe(sb);
            Indicator?.Describe(sb);
            Track?.Describe(sb);
            return sb.Length == 0 ? "Style (empty)" : sb.ToString();
        }

        /// <summary>Appends <paramref name="name"/> and its set states to <paramref name="sb"/>; nothing if no state is set.</summary>
        internal static void Describe<T>(StringBuilder sb, string name, in StateValues<T> values)
        {
            if (values.IsEmpty)
                return;
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append(name).Append('[').Append(values.StatesText()).Append(']');
        }
    }
}
