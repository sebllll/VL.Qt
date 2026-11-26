using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// The Sampler node, next to the shader nodes. It stays visible (unlike the uniform nodes): shader nodes and effects use it too.
    /// </summary>
    [SkipCategory]
    [Category(Categories.Shaders)]
    public static class SamplerNodes
    {
        /// <summary>
        /// How a shader reads a texture: filter, wrap (address), mipmaps. Connect the output to
        /// the Sampler pin of a texture input (Input Sampler of ShaderEffect, Texture, Image, Layer of Blend, Map of Displace,
        /// image pins of shader nodes). The defaults are Qt's: Linear, Clamp, no mipmaps. Without a Sampler an element's layer is read Nearest and Clamp.
        /// </summary>
        /// <param name="filter">Minification and magnification: Linear (smooth) or Point (nearest texel, hard pixels).</param>
        /// <param name="mipFilter">Mipmap level selection. Linear or Point also renders the sampled element's layer with mipmaps (an image
        /// needs mipmaps from LoadImage). Avoids aliasing when minified and lets a shader sample a blurred version with a texture
        /// bias.</param>
        /// <param name="addressU">What lies outside 0..1 in x: Clamp (edge texel), Wrap (tiles) or Mirror (tiles, every second mirrored).</param>
        /// <param name="addressV">What lies outside 0..1 in y.</param>
        public static Sampler Sampler(
            SamplerFilter filter = SamplerFilter.Linear,
            [Pin(Visibility = PinVisibility.Optional)] SamplerMipFilter mipFilter = SamplerMipFilter.None,
            SamplerAddress addressU = SamplerAddress.Clamp,
            SamplerAddress addressV = SamplerAddress.Clamp)
            => new()
            {
                Filter = filter,
                MipFilter = mipFilter,
                AddressU = addressU,
                AddressV = addressV,
            };
    }
}
