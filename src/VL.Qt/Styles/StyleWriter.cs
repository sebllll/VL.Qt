using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>
    /// Sends a resolved style as SET_STYLE. Decision: diff against the style last sent to the element; only
    /// changed (field, state) entries go out. A full send with <see cref="Protocol.StyleSet.Clear"/> happens when nothing
    /// was sent yet (create, resync) or when an entry that was set is unset now (the protocol has no "remove entry").
    /// Unset states are not sent (QtNative falls back to Normal). Allocation-free.
    /// <para>
    /// QtNative falls back from (field, state) to (field, Normal), so per field a reduced form is sent: Normal as set;
    /// another state only if it is set and differs from the Normal value. Diff and the unset rule work on this reduced
    /// form (old vs. new): "Hovered differed, now equals Normal or is unset" counts as a removed entry (full send with
    /// Clear). A style with the same value in all states therefore sends one entry per field.
    /// </para>
    /// </summary>
    internal static class StyleWriter
    {
        /// <summary>
        /// Writes <paramref name="resolved"/> for element <paramref name="id"/>; <paramref name="lastSent"/> = what QtNative
        /// holds (null = nothing). Writes nothing when both are equal. Returns true if a command was written.
        /// </summary>
        public static bool Write(ICommandSink sink, uint id, Style resolved, Style? lastSent)
        {
            if (ReferenceEquals(resolved, lastSent))
                return false;

            var changed = false;
            var removed = false;
            if (lastSent is null)
                changed = true;
            else
                Visit(null, resolved, lastSent, false, ref changed, ref removed);

            if (!changed && !removed)
                return false;

            var full = lastSent is null || removed;
            sink.BeginStyle(id, full ? Protocol.StyleSet.Clear : (ushort)0);
            var c = false;
            var r = false;
            Visit(sink, resolved, lastSent, full, ref c, ref r);
            sink.EndStyle();
            return true;
        }

        private static void Visit(ICommandSink? sink, Style cur, Style? last, bool full, ref bool changed, ref bool removed)
        {
            var pass = new Pass(sink, full);
            FrameStyle.Visit(ref pass, cur.Frame, last?.Frame);
            TextStyle.Visit(ref pass, cur.Text, last?.Text);
            IndicatorStyle.Visit(ref pass, cur.Indicator, last?.Indicator);
            TrackStyle.Visit(ref pass, cur.Track, last?.Track);
            changed = pass.Changed;
            removed = pass.Removed;
        }

        /// <summary>State of one diff pass over a style: where to write, whether to write everything, what was found.</summary>
        internal ref struct Pass
        {
            private readonly ICommandSink? _sink;
            private readonly bool _full;

            public Pass(ICommandSink? sink, bool full)
            {
                _sink = sink;
                _full = full;
                Changed = false;
                Removed = false;
            }

            /// <summary>True if an entry differs from the last sent style.</summary>
            public bool Changed { get; private set; }

            /// <summary>True if an entry that was set is unset now.</summary>
            public bool Removed { get; private set; }

            public void Color(ushort field, in StateValues<Color4> cur, in StateValues<Color4> last)
            {
                for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                    if (Entry(cur, last, (StyleState)s, out var v))
                        _sink?.StyleColor(field, s, v.R, v.G, v.B, v.A);
            }

            public void Float(ushort field, in StateValues<float> cur, in StateValues<float> last)
            {
                for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                    if (Entry(cur, last, (StyleState)s, out var v))
                        _sink?.StyleF32(field, s, v);
            }

            public void Int(ushort field, in StateValues<int> cur, in StateValues<int> last)
            {
                for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                    if (Entry(cur, last, (StyleState)s, out var v))
                        _sink?.StyleI32(field, s, v);
            }

            public void Text(ushort field, in StateValues<string> cur, in StateValues<string> last)
            {
                for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                    if (Entry(cur, last, (StyleState)s, out var v))
                        _sink?.StyleString(field, s, v ?? string.Empty);
            }

            /// <summary>Compares one state of the reduced forms; true if the new entry has to be written (value = the entry).</summary>
            private bool Entry<T>(in StateValues<T> cur, in StateValues<T> last, StyleState state, out T value)
            {
                var c = Reduced(cur, state);
                var l = Reduced(last, state);
                value = c.HasValue ? c.Value : default!;
                if (l.HasValue && !c.HasValue)
                    Removed = true;
                if (!c.HasValue)
                    return false;
                var differs = !l.HasValue || !EqualityComparer<T>.Default.Equals(l.Value, c.Value);
                if (differs)
                    Changed = true;
                return _full || differs;
            }
        }

        /// <summary>Reduced form of one state: Normal as set; others only if set and different from Normal.</summary>
        private static Optional<T> Reduced<T>(in StateValues<T> v, StyleState state)
        {
            var s = v[state];
            if (!s.HasValue)
                return default;
            if (state != StyleState.Normal && v.Normal.HasValue && EqualityComparer<T>.Default.Equals(v.Normal.Value, s.Value))
                return default;
            return s;
        }
    }
}
