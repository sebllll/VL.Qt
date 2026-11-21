using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Images;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Uniform values for the ShaderEffect: one node per value type, collect them into a spread for its
    /// Uniforms pin. The name must match the uniform block member or sampler2D in the shader (case sensitive).
    /// Advanced, like ShaderEffect: shader files in a qtshaders/ folder become nodes with one pin per uniform. The Sampler node stays visible, shader nodes and effects use it too.
    /// </summary>
    [SkipCategory]
    [Category(Categories.Elements + ".Uniform")]
    public static class UniformNodes
    {
        /// <summary>float uniform.</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Value.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Float(string name, float value) => Uniform.Float(name, value);

        /// <summary>int uniform.</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Value.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Int(string name, int value) => Uniform.Int(name, value);

        /// <summary>bool uniform.</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Value.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Bool(string name, bool value) => Uniform.Bool(name, value);

        /// <summary>vec2 uniform.</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Value.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Vector2(string name, Vector2 value) => Uniform.Vector2(name, value);

        /// <summary>vec3 uniform (std140 padding is handled by QtNative).</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Value.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Vector3(string name, Vector3 value) => Uniform.Vector3(name, value);

        /// <summary>vec4 uniform, passed unchanged (use Color for colors).</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Value.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Vector4(string name, Vector4 value) => Uniform.Vector4(name, value);

        /// <summary>vec4 uniform from a color, premultiplied by alpha like in Qt's ShaderEffect.</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Color with straight alpha.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Color(string name, Color4 value) => Uniform.Color(name, value);

        /// <summary>mat4 uniform. The Stride matrix is transposed for GLSL, so it multiplies like in vvvv.</summary>
        /// <param name="name">Name of the member in the shader's uniform block.</param>
        /// <param name="value">Matrix.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Matrix(string name, Matrix value) => Uniform.Matrix(name, value);

        /// <summary>sampler2D from an element: the element is rendered offscreen (not drawn itself) and sampled, premultiplied.</summary>
        /// <param name="name">Name of the sampler2D in the shader ("source" is reserved for the Input of the ShaderEffect).</param>
        /// <param name="element">The element sampled as texture. Nothing = transparent.</param>
        /// <param name="sampler">How the shader reads the texture (filter, wrap, mipmaps), from the Sampler node. Nothing = as the element's layer is (Nearest, Clamp).</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Texture(string name, Element? element, [Pin(Visibility = PinVisibility.Optional)] Sampler? sampler = null) => Uniform.FromTexture(name, element, sampler);

        /// <summary>sampler2D from a QtImage (LoadImage, ToQtImage, …).</summary>
        /// <param name="name">Name of the sampler2D in the shader.</param>
        /// <param name="image">The image. Nothing = transparent.</param>
        /// <param name="sampler">How the shader reads the image (filter, wrap, mip filter), from the Sampler node. Nothing = as the image is.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Image(string name, QtImage? image, [Pin(Visibility = PinVisibility.Optional)] Sampler? sampler = null) => Uniform.FromImage(name, image, sampler);

        /// <summary>
        /// How a shader reads a texture: filter, wrap (address), mipmaps. Connect the output to
        /// the Sampler pin of a texture input (Input Sampler of ShaderEffect, Texture, Image, Layer of Blend, Map of Displace,
        /// image pins of shader nodes). The defaults are Qt's: Linear, Clamp, no mipmaps. Without a Sampler an element's layer is read Nearest and Clamp.
        /// </summary>
        /// <param name="filter">Minification and magnification: Linear (smooth) or Point (nearest texel, hard pixels).</param>
        /// <param name="mipFilter">Mipmap level selection. Linear or Point also renders the sampled element's layer with mipmaps (an image needs mipmaps from LoadImage). Avoids aliasing when minified and lets a shader sample a blurred version with a texture bias.</param>
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
