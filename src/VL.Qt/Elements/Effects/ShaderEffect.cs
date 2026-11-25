using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Collections;

namespace VL.Qt.Elements
{
    /// <summary>
    /// A filter shader as link of the Effect chain: driven by QtNative's pass driver with a template (protocol.h
    /// "Effect chain", passes: docs/effects-and-shaders.md). Ends a MultiEffect run in <see cref="Effect.Segment"/>.
    /// QtNative sets texelSize, dpr, weight, elementRect, source and the template's inputs itself; <see cref="Uniforms"/> must not use them.
    /// </summary>
    internal abstract class ShaderEffect : Effect
    {
        protected ShaderEffect(Effect? previous, ushort template, ImmutableArray<ulong> shaderIds, Spread<Uniform>? uniforms,
            RectangleF padding, float strength)
            : base(previous)
        {
            Template = template;
            ShaderIds = shaderIds.IsDefault ? ImmutableArray<ulong>.Empty : shaderIds;
            Uniforms = uniforms ?? Spread<Uniform>.Empty;
            Padding = padding;
            Strength = strength;
        }

        /// <summary><see cref="Protocol.EffectTemplate"/>: Single, TwoPass or Pyramid.</summary>
        public ushort Template { get; }

        /// <summary>
        /// Shader resource ids (stage fragment) owned by the node, by role: Single/TwoPass [fragment], Pyramid [prefilter,
        /// composite]; 0 = not loaded.
        /// </summary>
        public ImmutableArray<ulong> ShaderIds { get; }

        /// <summary>Uniform values by name, like <see cref="Shader.Uniforms"/>.</summary>
        public Spread<Uniform> Uniforms { get; }

        /// <summary>Extra effect area in DIP: x = left, y = top, width = right, height = bottom.</summary>
        public RectangleF Padding { get; }

        /// <summary>Pyramid: radius in DIP; Single and TwoPass: unused.</summary>
        public float Strength { get; }

        internal sealed override EffectKind Kind => EffectKind.None;

        internal sealed override EffectDesc Apply(in EffectDesc desc) => desc;
    }
}
