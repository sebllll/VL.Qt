namespace VL.Qt.Elements
{
    /// <summary>How a <see cref="Sampler"/> picks between mipmap levels.</summary>
    public enum SamplerMipFilter
    {
        /// <summary>No mipmaps, always the full resolution (Qt default).</summary>
        None,
        /// <summary>The nearest mipmap level.</summary>
        Point,
        /// <summary>Interpolation between the two nearest mipmap levels (trilinear together with Filter Linear).</summary>
        Linear,
    }
}
