using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Images;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// One node per uniform value type, for the Uniforms pin of the ShaderEffect.
    /// </summary>
    [SkipCategory]
    [Category(Categories.Shaders + ".Uniform")]
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
        /// <param name="name">Name of the sampler2D in the shader ("source" is reserved for the Input of the ShaderEffect, so are qt_Matrix
        /// and qt_Opacity: an error at the node).</param>
        /// <param name="element">The element sampled as texture (a self-made widget is built with the style at the shader). Nothing, or an
        /// IElement that is not of VL.Qt, = transparent.</param>
        /// <param name="sampler">How the shader reads the texture (filter, wrap, mipmaps), from the Sampler node. Nothing = as the
        /// element's layer is (Nearest, Clamp).</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Texture(string name, IElement? element, [Pin(Visibility = PinVisibility.Optional)] Sampler? sampler = null)
            => Uniform.FromTexture(name, ElementConversion.ToElement(element), sampler);

        /// <summary>sampler2D from a QtImage (LoadImage, ToQtImage, …).</summary>
        /// <param name="name">Name of the sampler2D in the shader.</param>
        /// <param name="image">The image. Nothing = transparent.</param>
        /// <param name="sampler">How the shader reads the image (filter, wrap, mip filter), from the Sampler node. Nothing = as the image is.</param>
        [Smell(SymbolSmell.Advanced)]
        public static Uniform Image(string name, QtImage? image, [Pin(Visibility = PinVisibility.Optional)] Sampler? sampler = null) => Uniform.FromImage(name, image, sampler);
    }
}
