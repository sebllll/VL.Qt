using System.Text;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>Track part: slider groove, switch track, check box; FillColor = slider value, switch on, text selection.</summary>
    internal sealed record TrackStyle
    {
        public StateValues<Color4> Color { get; init; }
        public StateValues<Color4> FillColor { get; init; }
        public StateValues<float> Thickness { get; init; }
        public StateValues<float> Radius { get; init; }

        /// <summary>Merge rule (S1) for this part; returns one input unchanged if the other adds nothing.</summary>
        public static TrackStyle? Merge(TrackStyle? under, TrackStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var tally = new MergeTally();
            var color = tally.Pick(u.Color, o.Color);
            var fillColor = tally.Pick(u.FillColor, o.FillColor);
            var thickness = tally.Pick(u.Thickness, o.Thickness);
            var radius = tally.Pick(u.Radius, o.Radius);
            if (tally.AllUnder)
                return u;
            if (tally.AllOver)
                return o;
            return new TrackStyle
            {
                Color = color,
                FillColor = fillColor,
                Thickness = thickness,
                Radius = radius,
            };
        }

        /// <summary>Wire mapping: writes the entries of <paramref name="cur"/> that differ from <paramref name="last"/> (see <see cref="StyleWriter"/>).</summary>
        public static void Visit(ref StyleWriter.Pass pass, TrackStyle? cur, TrackStyle? last)
        {
            pass.Color(Protocol.StyleField.TrackColor, cur?.Color ?? default, last?.Color ?? default);
            pass.Color(Protocol.StyleField.TrackFillColor, cur?.FillColor ?? default, last?.FillColor ?? default);
            pass.Float(Protocol.StyleField.TrackThickness, cur?.Thickness ?? default, last?.Thickness ?? default);
            pass.Float(Protocol.StyleField.TrackRadius, cur?.Radius ?? default, last?.Radius ?? default);
        }

        /// <summary>Appends the set fields for <see cref="Style.ToString"/>.</summary>
        public void Describe(StringBuilder sb)
        {
            Style.Describe(sb, "Track.Color", Color);
            Style.Describe(sb, "Track.FillColor", FillColor);
            Style.Describe(sb, "Track.Thickness", Thickness);
            Style.Describe(sb, "Track.Radius", Radius);
        }
    }
}
