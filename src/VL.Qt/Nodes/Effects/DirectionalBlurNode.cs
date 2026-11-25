using System;
using System.Collections.Immutable;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Linear motion blur: smears the element along a line, centred on each pixel (the picture does not shift). Two passes of
    /// the same shader keep long blurs clean.
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "DirectionalBlur", Category = Categories.Effects)]
    public sealed class DirectionalBlurNode : IDisposable
    {
        internal readonly record struct Props(float Angle, float Length);

        private static readonly ShaderEffectSource[] s_shaders = { ShaderEffectSource.Builtin("directional_blur") };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, ShaderEffect> s_create = static (previous, ids, p) =>
        {
            var length = Math.Max(p.Length, 0f);
            // Reach of the blur on each side of a pixel along the line: half of the blur line of pass 1, plus the
            // half-length of pass 2, which blurs by length / 64 (the sample spacing of pass 1, see directional_blur.frag).
            // length / 64 is a full spacing, so the sum is slightly more than the reach of length / 2 + length / 128: a margin.
            var half = length / 2 + length / 64;
            var a = 2 * MathF.PI * p.Angle;
            var padX = half * MathF.Abs(MathF.Cos(a));
            var padY = half * MathF.Abs(MathF.Sin(a));
            return new ShaderEffect(previous, Protocol.EffectTemplate.TwoPass, ids,
                new[] { Uniform.Float("angle", p.Angle), Uniform.Float("blurLength", length) }.ToSpread(),
                new RectangleF(padX, padY, padX, padY));
        };

        private readonly ShaderEffectBuilder<Props> _link;

        public DirectionalBlurNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal DirectionalBlurNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new ShaderEffectBuilder<Props>(client, s_shaders, s_create, errors);

        internal ShaderEffectBuilder<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="angle">Direction of the blur in cycles (1 = full turn): 0 = horizontal, 0.25 = vertical, increases clockwise on screen.</param>
        /// <param name="length">Length of the blur line in DIP (total, 0 = no blur).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            float angle,
            [DefaultValue(10f)] float length,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _link.Describe(input, new Props(angle, length)) : input;

        public void Dispose() => _link.Dispose();
    }
}
