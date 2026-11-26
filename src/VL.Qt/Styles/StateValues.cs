using System;
using System.Collections.Generic;
using System.Text;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
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
}
