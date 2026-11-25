using System.Text;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>Text part of a style. Font size in DIP; font family "" = the default font.</summary>
    internal sealed record TextStyle
    {
        public StateValues<Color4> Color { get; init; }
        public StateValues<string> FontFamily { get; init; }
        public StateValues<float> FontSize { get; init; }
        public StateValues<int> FontWeight { get; init; }

        /// <summary>Merge rule (S1) for this part; returns one input unchanged if the other adds nothing.</summary>
        public static TextStyle? Merge(TextStyle? under, TextStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var tally = new MergeTally();
            var color = tally.Pick(u.Color, o.Color);
            var fontFamily = tally.Pick(u.FontFamily, o.FontFamily);
            var fontSize = tally.Pick(u.FontSize, o.FontSize);
            var fontWeight = tally.Pick(u.FontWeight, o.FontWeight);
            if (tally.AllUnder)
                return u;
            if (tally.AllOver)
                return o;
            return new TextStyle
            {
                Color = color,
                FontFamily = fontFamily,
                FontSize = fontSize,
                FontWeight = fontWeight,
            };
        }

        /// <summary>Wire mapping: writes the entries of <paramref name="cur"/> that differ from <paramref name="last"/> (see <see cref="StyleWriter"/>).</summary>
        public static void Visit(ref StyleWriter.Pass pass, TextStyle? cur, TextStyle? last)
        {
            pass.Color(Protocol.StyleField.TextColor, cur?.Color ?? default, last?.Color ?? default);
            pass.Text(Protocol.StyleField.TextFontFamily, cur?.FontFamily ?? default, last?.FontFamily ?? default);
            pass.Float(Protocol.StyleField.TextFontSize, cur?.FontSize ?? default, last?.FontSize ?? default);
            pass.Int(Protocol.StyleField.TextFontWeight, cur?.FontWeight ?? default, last?.FontWeight ?? default);
        }

        /// <summary>Appends the set fields for <see cref="Style.ToString"/>.</summary>
        public void Describe(StringBuilder sb)
        {
            Style.Describe(sb, "Text.Color", Color);
            Style.Describe(sb, "Text.FontFamily", FontFamily);
            Style.Describe(sb, "Text.FontSize", FontSize);
            Style.Describe(sb, "Text.FontWeight", FontWeight);
        }
    }
}
