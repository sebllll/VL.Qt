namespace VL.Qt.Elements
{
    /// <summary>
    /// How a shader reads a texture (sampler2D): filter, mipmaps and behaviour outside 0..1. Immutable; create
    /// it with the Sampler node and connect it to the Sampler pin of a texture input (Input Sampler of ShaderEffect,
    /// Texture and Image uniform nodes, image pins of shader nodes, Layer of Blend, Map of Displace). The defaults of the
    /// record are the sampler defaults of Qt. Without a Sampler an element is read as its layer is: Nearest, Clamp, no
    /// mipmaps (an image: as its image resource is).
    /// </summary>
    public sealed record Sampler
    {
        /// <summary>Minification and magnification filter.</summary>
        public SamplerFilter Filter { get; init; } = SamplerFilter.Linear;

        /// <summary>
        /// Mipmap selection. Anything but None also renders the sampled element's layer with mipmaps (33 % more memory, a mip pass
        /// per frame). An image needs mipmaps from its load (LoadImage, Mipmaps).
        /// </summary>
        public SamplerMipFilter MipFilter { get; init; } = SamplerMipFilter.None;

        /// <summary>Behaviour outside 0..1 in x (u).</summary>
        public SamplerAddress AddressU { get; init; } = SamplerAddress.Clamp;

        /// <summary>Behaviour outside 0..1 in y (v).</summary>
        public SamplerAddress AddressV { get; init; } = SamplerAddress.Clamp;

        /// <summary>The state as the wire carries it (protocol.h "Sampler"): bytes filter, mip filter, address U, address V.</summary>
        internal ulong Pack()
        {
            return (ulong)Filter
                | (ulong)MipFilter << 8
                | (ulong)AddressU << 16
                | (ulong)AddressV << 24;
        }
    }
}
