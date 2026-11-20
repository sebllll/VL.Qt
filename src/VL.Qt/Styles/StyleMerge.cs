using Stride.Core.Mathematics;

namespace VL.Qt.Styles
{
    /// <summary>
    /// The one merge rule of the styles (S1): field by field and state by state, a set value of the upper style
    /// wins, otherwise the lower one counts. Used by the reconciler, later by Merge (Style) and IStylable.
    /// </summary>
    internal static class StyleMerge
    {
        /// <summary>
        /// <paramref name="over"/> laid over <paramref name="under"/>. Returns one of the inputs unchanged (no allocation)
        /// when the other one is null, empty or adds nothing; parts are shared where only one side sets them.
        /// </summary>
        public static Style Merge(Style? under, Style? over)
        {
            if (under is null)
                return over ?? Style.Empty;
            if (over is null || ReferenceEquals(under, over))
                return under;

            var frame = Merge(under.Frame, over.Frame);
            var text = Merge(under.Text, over.Text);
            var indicator = Merge(under.Indicator, over.Indicator);
            var track = Merge(under.Track, over.Track);

            if (frame == under.Frame && text == under.Text && indicator == under.Indicator && track == under.Track)
                return under;
            if (frame == over.Frame && text == over.Text && indicator == over.Indicator && track == over.Track)
                return over;
            return new Style { Frame = frame, Text = text, Indicator = indicator, Track = track };
        }


        public static FrameStyle? Merge(FrameStyle? under, FrameStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var color = StateValues<Color4>.Merge(u.Color, o.Color);
            var borderColor = StateValues<Color4>.Merge(u.BorderColor, o.BorderColor);
            var radius = StateValues<float>.Merge(u.Radius, o.Radius);
            var borderWidth = StateValues<float>.Merge(u.BorderWidth, o.BorderWidth);
            var paddingX = StateValues<float>.Merge(u.PaddingX, o.PaddingX);
            var paddingY = StateValues<float>.Merge(u.PaddingY, o.PaddingY);
            var minWidth = StateValues<float>.Merge(u.MinWidth, o.MinWidth);
            var minHeight = StateValues<float>.Merge(u.MinHeight, o.MinHeight);
            if (color == u.Color && borderColor == u.BorderColor && radius == u.Radius && borderWidth == u.BorderWidth && paddingX == u.PaddingX && paddingY == u.PaddingY && minWidth == u.MinWidth && minHeight == u.MinHeight)
                return u;
            if (color == o.Color && borderColor == o.BorderColor && radius == o.Radius && borderWidth == o.BorderWidth && paddingX == o.PaddingX && paddingY == o.PaddingY && minWidth == o.MinWidth && minHeight == o.MinHeight)
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

        public static TextStyle? Merge(TextStyle? under, TextStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var color = StateValues<Color4>.Merge(u.Color, o.Color);
            var fontFamily = StateValues<string>.Merge(u.FontFamily, o.FontFamily);
            var fontSize = StateValues<float>.Merge(u.FontSize, o.FontSize);
            var fontWeight = StateValues<int>.Merge(u.FontWeight, o.FontWeight);
            if (color == u.Color && fontFamily == u.FontFamily && fontSize == u.FontSize && fontWeight == u.FontWeight)
                return u;
            if (color == o.Color && fontFamily == o.FontFamily && fontSize == o.FontSize && fontWeight == o.FontWeight)
                return o;
            return new TextStyle
            {
                Color = color,
                FontFamily = fontFamily,
                FontSize = fontSize,
                FontWeight = fontWeight,
            };
        }

        public static IndicatorStyle? Merge(IndicatorStyle? under, IndicatorStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var color = StateValues<Color4>.Merge(u.Color, o.Color);
            var size = StateValues<float>.Merge(u.Size, o.Size);
            var radius = StateValues<float>.Merge(u.Radius, o.Radius);
            var borderWidth = StateValues<float>.Merge(u.BorderWidth, o.BorderWidth);
            if (color == u.Color && size == u.Size && radius == u.Radius && borderWidth == u.BorderWidth)
                return u;
            if (color == o.Color && size == o.Size && radius == o.Radius && borderWidth == o.BorderWidth)
                return o;
            return new IndicatorStyle
            {
                Color = color,
                Size = size,
                Radius = radius,
                BorderWidth = borderWidth,
            };
        }

        public static TrackStyle? Merge(TrackStyle? under, TrackStyle? over)
        {
            if (under is not { } u)
                return over;
            if (over is not { } o)
                return under;
            var color = StateValues<Color4>.Merge(u.Color, o.Color);
            var fillColor = StateValues<Color4>.Merge(u.FillColor, o.FillColor);
            var thickness = StateValues<float>.Merge(u.Thickness, o.Thickness);
            var radius = StateValues<float>.Merge(u.Radius, o.Radius);
            if (color == u.Color && fillColor == u.FillColor && thickness == u.Thickness && radius == u.Radius)
                return u;
            if (color == o.Color && fillColor == o.FillColor && thickness == o.Thickness && radius == o.Radius)
                return o;
            return new TrackStyle
            {
                Color = color,
                FillColor = fillColor,
                Thickness = thickness,
                Radius = radius,
            };
        }
    }
}
