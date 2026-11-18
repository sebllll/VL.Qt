namespace VL.Qt.Elements
{
    /// <summary>Which areas of a self-overlapping path are filled. Values are the protocol values of <see cref="Protocol.Prop.ShapeFillRule"/>.</summary>
    public enum ShapeFillRule
    {
        /// <summary>A point is inside if the winding number of the path around it is not zero (Skia Winding).</summary>
        NonZero = 0,
        /// <summary>A point is inside if a ray from it crosses the path an odd number of times.</summary>
        EvenOdd = 1,
    }
}
