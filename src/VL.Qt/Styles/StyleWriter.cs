using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Core;

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
            var f = cur.Frame;
            var lf = last?.Frame;
            Color(sink, Protocol.StyleField.FrameColor, f?.Color ?? default, lf?.Color ?? default, full, ref changed, ref removed);
            Color(sink, Protocol.StyleField.BorderColor, f?.BorderColor ?? default, lf?.BorderColor ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.FrameRadius, f?.Radius ?? default, lf?.Radius ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.FrameBorderWidth, f?.BorderWidth ?? default, lf?.BorderWidth ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.FramePaddingX, f?.PaddingX ?? default, lf?.PaddingX ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.FramePaddingY, f?.PaddingY ?? default, lf?.PaddingY ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.FrameMinWidth, f?.MinWidth ?? default, lf?.MinWidth ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.FrameMinHeight, f?.MinHeight ?? default, lf?.MinHeight ?? default, full, ref changed, ref removed);

            var t = cur.Text;
            var lt = last?.Text;
            Color(sink, Protocol.StyleField.TextColor, t?.Color ?? default, lt?.Color ?? default, full, ref changed, ref removed);
            Text(sink, Protocol.StyleField.TextFontFamily, t?.FontFamily ?? default, lt?.FontFamily ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.TextFontSize, t?.FontSize ?? default, lt?.FontSize ?? default, full, ref changed, ref removed);
            Int(sink, Protocol.StyleField.TextFontWeight, t?.FontWeight ?? default, lt?.FontWeight ?? default, full, ref changed, ref removed);

            var i = cur.Indicator;
            var li = last?.Indicator;
            Color(sink, Protocol.StyleField.IndicatorColor, i?.Color ?? default, li?.Color ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.IndicatorSize, i?.Size ?? default, li?.Size ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.IndicatorRadius, i?.Radius ?? default, li?.Radius ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.IndicatorBorderWidth, i?.BorderWidth ?? default, li?.BorderWidth ?? default, full, ref changed, ref removed);

            var k = cur.Track;
            var lk = last?.Track;
            Color(sink, Protocol.StyleField.TrackColor, k?.Color ?? default, lk?.Color ?? default, full, ref changed, ref removed);
            Color(sink, Protocol.StyleField.TrackFillColor, k?.FillColor ?? default, lk?.FillColor ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.TrackThickness, k?.Thickness ?? default, lk?.Thickness ?? default, full, ref changed, ref removed);
            Float(sink, Protocol.StyleField.TrackRadius, k?.Radius ?? default, lk?.Radius ?? default, full, ref changed, ref removed);
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

        /// <summary>Compares one state of the reduced forms; true if the new entry has to be written (value = the entry).</summary>
        private static bool Entry<T>(in StateValues<T> cur, in StateValues<T> last, StyleState state, bool full, ref bool changed, ref bool removed, out T value)
        {
            var c = Reduced(cur, state);
            var l = Reduced(last, state);
            value = c.HasValue ? c.Value : default!;
            if (l.HasValue && !c.HasValue)
                removed = true;
            if (!c.HasValue)
                return false;
            var differs = !l.HasValue || !EqualityComparer<T>.Default.Equals(l.Value, c.Value);
            if (differs)
                changed = true;
            return full || differs;
        }

        private static void Color(ICommandSink? sink, ushort field, in StateValues<Color4> cur, in StateValues<Color4> last, bool full, ref bool changed, ref bool removed)
        {
            for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                if (Entry(cur, last, (StyleState)s, full, ref changed, ref removed, out var v))
                    sink?.StyleColor(field, s, v.R, v.G, v.B, v.A);
        }

        private static void Float(ICommandSink? sink, ushort field, in StateValues<float> cur, in StateValues<float> last, bool full, ref bool changed, ref bool removed)
        {
            for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                if (Entry(cur, last, (StyleState)s, full, ref changed, ref removed, out var v))
                    sink?.StyleF32(field, s, v);
        }

        private static void Int(ICommandSink? sink, ushort field, in StateValues<int> cur, in StateValues<int> last, bool full, ref bool changed, ref bool removed)
        {
            for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                if (Entry(cur, last, (StyleState)s, full, ref changed, ref removed, out var v))
                    sink?.StyleI32(field, s, v);
        }

        private static void Text(ICommandSink? sink, ushort field, in StateValues<string> cur, in StateValues<string> last, bool full, ref bool changed, ref bool removed)
        {
            for (byte s = 0; s <= Protocol.StyleState.Checked; s++)
                if (Entry(cur, last, (StyleState)s, full, ref changed, ref removed, out var v))
                    sink?.StyleString(field, s, v ?? string.Empty);
        }
    }
}
