using System;
using System.Collections.Generic;
using System.Text;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
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
