namespace VL.Qt.Elements
{
    /// <summary>Minification and magnification filter of a <see cref="Sampler"/> (Stride: TextureFilter, Point or Linear).</summary>
    public enum SamplerFilter
    {
        /// <summary>Bilinear interpolation between texels (smooth; Qt default).</summary>
        Linear,
        /// <summary>The nearest texel (hard pixel edges when magnified).</summary>
        Point,
    }
}
