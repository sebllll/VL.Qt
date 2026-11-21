using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// A filter shader file as link of the Effect chain: template Single, or TwoPass with @passes 2, one fragment
    /// shader resource owned by the node, the uniform pins as <see cref="ShaderEffect.Uniforms"/>, @padding as padding.
    /// </summary>
    internal sealed class ShaderFileEffect : ShaderEffect
    {
        public ShaderFileEffect(Effect? previous, ushort template, ulong shaderId, Spread<Uniform> uniforms, RectangleF padding)
            : base(previous, template, ImmutableArray.Create(shaderId), uniforms, padding, 0f)
        {
        }
    }
}
