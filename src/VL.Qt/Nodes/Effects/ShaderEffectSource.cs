using System;
using System.Collections.Immutable;
using VL.Qt.Images;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// One shader of a <see cref="ShaderEffect"/>: either a path (<c>builtin:&lt;name&gt;</c>, shipped with VL.Qt) or GLSL
    /// source (a shader file), with the label that starts its error text.
    /// </summary>
    internal readonly record struct ShaderEffectSource(string Label, string? Path, string? Code)
    {
        /// <summary>A shader shipped with VL.Qt.</summary>
        public static ShaderEffectSource Builtin(string name) => new(name, ShaderSlot.builtinShaderPrefix + name, null);

        /// <summary>A fragment shader given as source text.</summary>
        public static ShaderEffectSource Fragment(string code) => new("Fragment shader", null, code);
    }
}
