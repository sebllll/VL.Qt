namespace VL.Qt.Paths
{
    /// <summary>How a path is added to another path (Skia SKPathAddMode).</summary>
    public enum PathAddMode
    {
        /// <summary>Adds the contours of the other path as new contours.</summary>
        Append,

        /// <summary>Like Append, but the first contour of the other path continues the last contour with a line to its start.</summary>
        Extend,
    }
}
