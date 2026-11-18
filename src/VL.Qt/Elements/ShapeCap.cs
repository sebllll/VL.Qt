namespace VL.Qt.Elements
{
    /// <summary>How the open ends of a stroke are drawn. Values are the protocol values of <see cref="Protocol.Prop.ShapeCap"/>.</summary>
    public enum ShapeCap
    {
        /// <summary>The stroke ends exactly at the end point (Skia Butt).</summary>
        Flat = 0,
        /// <summary>The stroke extends by half the stroke width beyond the end point.</summary>
        Square = 1,
        /// <summary>The stroke ends in a half circle around the end point.</summary>
        Round = 2,
    }
}
