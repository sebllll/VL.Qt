using System;
using System.Collections.Immutable;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// The uniform names VL.Qt sets itself. C# mirror of the one native list in <c>effects/shader_item.h</c>
    /// (<c>kItemReservedUniforms</c>, <c>kItemAutomaticUniforms</c>, <c>kChainAutomaticUniforms</c>, <c>kChainReservedUniforms</c>);
    /// <c>ReservedUniformsDriftTests</c> compares the two. A uniform of such a name gets no pin; an entry of that name
    /// set from C# is an error at the node.
    /// </summary>
    internal static class ReservedUniforms
    {
        /// <summary>Name of the sampler2D that becomes the Input pin (and the Input of an effect chain link).</summary>
        public const string InputSampler = "source";

        /// <summary>Set for every ShaderEffect: qt_Matrix and qt_Opacity from the render state, source is the Input.</summary>
        public static readonly ImmutableHashSet<string> Item = ImmutableHashSet.Create(StringComparer.Ordinal, "qt_Matrix", "qt_Opacity", InputSampler);

        /// <summary>
        /// Written by QtNative into every ShaderEffect whose shader declares them: texelSize (1 / pixel size of the Input), dpr and
        /// viewSize (size in DIP). C# sends no entry of such a name.
        /// </summary>
        public static readonly ImmutableHashSet<string> ItemAutomatic = ImmutableHashSet.Create(StringComparer.Ordinal, "texelSize", "dpr", "viewSize");

        /// <summary>Written by the effect chain into the passes of a shader link if the shader declares them.</summary>
        public static readonly ImmutableHashSet<string> ChainAutomatic = ImmutableHashSet.Create(StringComparer.Ordinal, "weight", "elementRect", "passIndex");

        /// <summary>Names of the textures of the Blur-Pyramide: no uniform of a shader link may have them.</summary>
        public static readonly ImmutableHashSet<string> ChainReserved = ImmutableHashSet.Create(StringComparer.Ordinal, "base", "glow", "shadow");

        /// <summary>The name is one of <see cref="Item"/> or <see cref="ItemAutomatic"/>: no pin, no entry from C#.</summary>
        public static bool IsItemReserved(string name) => Item.Contains(name) || ItemAutomatic.Contains(name);

        /// <summary>The name is one of <see cref="Item"/>, <see cref="ItemAutomatic"/>, <see cref="ChainAutomatic"/> or <see cref="ChainReserved"/>.</summary>
        public static bool IsChainReserved(string name) => IsItemReserved(name) || ChainAutomatic.Contains(name) || ChainReserved.Contains(name);

        /// <summary>Text of the error for the reserved <paramref name="name"/> in a table of uniform values.</summary>
        public static string Describe(string name) =>
            $"Uniform '{name}' is reserved ({(name == InputSampler ? "use the Input pin" : "set by VL.Qt")}), the entry is ignored";
    }
}
