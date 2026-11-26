namespace VL.Qt.Elements
{
    /// <summary>
    /// Limits of a drag offset in units (written as the rectf slots of VLQT_PROPERTY_DRAG_LIMITS in logical pixels), ±infinity = none. Exact
    /// float equality (unlike Stride's vectors, whose near-equality fails for infinity).
    /// </summary>
    internal readonly record struct DragLimits(float MinX, float MinY, float MaxX, float MaxY)
    {
        public static readonly DragLimits None = new(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity);
    }
}
