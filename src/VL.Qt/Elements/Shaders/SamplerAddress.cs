namespace VL.Qt.Elements
{
    /// <summary>What a <see cref="Sampler"/> returns for texture coordinates outside 0..1 (Stride: TextureAddressMode; Border and MirrorOnce do not exist in Qt).</summary>
    public enum SamplerAddress
    {
        /// <summary>The edge texel is repeated (ClampToEdge; Qt default).</summary>
        Clamp,
        /// <summary>The texture tiles (Repeat).</summary>
        Wrap,
        /// <summary>The texture tiles, every second copy mirrored (MirroredRepeat).</summary>
        Mirror,
    }
}
