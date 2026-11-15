namespace VL.Qt
{
    /// <summary>
    /// Number of samples per pixel for multisample anti-aliasing (MSAA) of the edges of shapes, text outlines and images.
    /// Names and order as <c>Stride.Graphics.MultisampleCount</c>, the value is the sample count; declared here to avoid a
    /// dependency on Stride. A device that does not support a count gets the next lower one it supports.
    /// </summary>
    public enum MultisampleCount
    {
        /// <summary>No anti-aliasing of the edges, the cheapest.</summary>
        None = 1,

        /// <summary>2 samples per pixel.</summary>
        X2 = 2,

        /// <summary>4 samples per pixel.</summary>
        X4 = 4,

        /// <summary>8 samples per pixel, the most expensive.</summary>
        X8 = 8,
    }
}
