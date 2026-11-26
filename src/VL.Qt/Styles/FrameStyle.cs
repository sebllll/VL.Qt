using System.Text;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
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

        /// <summary>Merge rule (S1) for this part; returns one input unchanged if the other adds nothing.</summary>
        public static FrameStyle? Merge(FrameStyle? under, FrameStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var tally = new MergeTally();
            var color = tally.Pick(u.Color, o.Color);
            var borderColor = tally.Pick(u.BorderColor, o.BorderColor);
            var radius = tally.Pick(u.Radius, o.Radius);
            var borderWidth = tally.Pick(u.BorderWidth, o.BorderWidth);
            var paddingX = tally.Pick(u.PaddingX, o.PaddingX);
            var paddingY = tally.Pick(u.PaddingY, o.PaddingY);
            var minWidth = tally.Pick(u.MinWidth, o.MinWidth);
            var minHeight = tally.Pick(u.MinHeight, o.MinHeight);
            if (tally.AllUnder)
                return u;
            if (tally.AllOver)
                return o;
            return new FrameStyle
            {
                Color = color,
                BorderColor = borderColor,
                Radius = radius,
                BorderWidth = borderWidth,
                PaddingX = paddingX,
                PaddingY = paddingY,
                MinWidth = minWidth,
                MinHeight = minHeight,
            };
        }

        /// <summary>Wire mapping: writes the entries of <paramref name="cur"/> that differ from <paramref name="last"/> (see
        /// <see cref="StyleWriter"/>).</summary>
        public static void Visit(ref StyleWriter.Pass pass, FrameStyle? cur, FrameStyle? last)
        {
            pass.Color(Protocol.StyleField.FrameColor, cur?.Color ?? default, last?.Color ?? default);
            pass.Color(Protocol.StyleField.BorderColor, cur?.BorderColor ?? default, last?.BorderColor ?? default);
            pass.Float(Protocol.StyleField.FrameRadius, cur?.Radius ?? default, last?.Radius ?? default);
            pass.Float(Protocol.StyleField.FrameBorderWidth, cur?.BorderWidth ?? default, last?.BorderWidth ?? default);
            pass.Float(Protocol.StyleField.FramePaddingX, cur?.PaddingX ?? default, last?.PaddingX ?? default);
            pass.Float(Protocol.StyleField.FramePaddingY, cur?.PaddingY ?? default, last?.PaddingY ?? default);
            pass.Float(Protocol.StyleField.FrameMinWidth, cur?.MinWidth ?? default, last?.MinWidth ?? default);
            pass.Float(Protocol.StyleField.FrameMinHeight, cur?.MinHeight ?? default, last?.MinHeight ?? default);
        }

        /// <summary>Appends the set fields for <see cref="Style.ToString"/>.</summary>
        public void Describe(StringBuilder sb)
        {
            Style.Describe(sb, "Frame.Color", Color);
            Style.Describe(sb, "Frame.BorderColor", BorderColor);
            Style.Describe(sb, "Frame.Radius", Radius);
            Style.Describe(sb, "Frame.BorderWidth", BorderWidth);
            Style.Describe(sb, "Frame.PaddingX", PaddingX);
            Style.Describe(sb, "Frame.PaddingY", PaddingY);
            Style.Describe(sb, "Frame.MinWidth", MinWidth);
            Style.Describe(sb, "Frame.MinHeight", MinHeight);
        }
    }
}
