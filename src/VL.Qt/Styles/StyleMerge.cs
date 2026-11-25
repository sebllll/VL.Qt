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


        public static FrameStyle? Merge(FrameStyle? under, FrameStyle? over) => FrameStyle.Merge(under, over);

        public static TextStyle? Merge(TextStyle? under, TextStyle? over) => TextStyle.Merge(under, over);

        public static IndicatorStyle? Merge(IndicatorStyle? under, IndicatorStyle? over) => IndicatorStyle.Merge(under, over);

        public static TrackStyle? Merge(TrackStyle? under, TrackStyle? over) => TrackStyle.Merge(under, over);
    }
}
