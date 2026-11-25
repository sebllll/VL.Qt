using System.Text;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>Indicator part: slider handle, check mark, switch knob, SpinBox -/+.</summary>
    internal sealed record IndicatorStyle
    {
        public StateValues<Color4> Color { get; init; }
        public StateValues<float> Size { get; init; }
        public StateValues<float> Radius { get; init; }
        public StateValues<float> BorderWidth { get; init; }

        /// <summary>Merge rule (S1) for this part; returns one input unchanged if the other adds nothing.</summary>
        public static IndicatorStyle? Merge(IndicatorStyle? under, IndicatorStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var tally = new MergeTally();
            var color = tally.Pick(u.Color, o.Color);
            var size = tally.Pick(u.Size, o.Size);
            var radius = tally.Pick(u.Radius, o.Radius);
            var borderWidth = tally.Pick(u.BorderWidth, o.BorderWidth);
            if (tally.AllUnder)
                return u;
            if (tally.AllOver)
                return o;
            return new IndicatorStyle
            {
                Color = color,
                Size = size,
                Radius = radius,
                BorderWidth = borderWidth,
            };
        }

        /// <summary>Wire mapping: writes the entries of <paramref name="cur"/> that differ from <paramref name="last"/> (see <see cref="StyleWriter"/>).</summary>
        public static void Visit(ref StyleWriter.Pass pass, IndicatorStyle? cur, IndicatorStyle? last)
        {
            pass.Color(Protocol.StyleField.IndicatorColor, cur?.Color ?? default, last?.Color ?? default);
            pass.Float(Protocol.StyleField.IndicatorSize, cur?.Size ?? default, last?.Size ?? default);
            pass.Float(Protocol.StyleField.IndicatorRadius, cur?.Radius ?? default, last?.Radius ?? default);
            pass.Float(Protocol.StyleField.IndicatorBorderWidth, cur?.BorderWidth ?? default, last?.BorderWidth ?? default);
        }

        /// <summary>Appends the set fields for <see cref="Style.ToString"/>.</summary>
        public void Describe(StringBuilder sb)
        {
            Style.Describe(sb, "Indicator.Color", Color);
            Style.Describe(sb, "Indicator.Size", Size);
            Style.Describe(sb, "Indicator.Radius", Radius);
            Style.Describe(sb, "Indicator.BorderWidth", BorderWidth);
        }
    }
}
