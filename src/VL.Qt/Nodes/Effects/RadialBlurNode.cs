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
    /// Radial (rotational) blur: arcs around Center, a box blur over a rotation of Angle cycles (centred, the picture does
    /// not turn). Two passes of the same shader keep large angles clean.
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "RadialBlur", Category = Categories.Effects)]
    public sealed class RadialBlurNode : IDisposable
    {
        internal readonly record struct Props(Vector2 Center, float Angle, float Padding);

        private static readonly ShaderEffectSource[] s_shaders = { ShaderEffectSource.Builtin("radial_blur") };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, ShaderEffect> s_create = static (previous, ids, p) =>
        {
            var pad = Math.Max(p.Padding, 0f);
            return new ShaderEffect(previous, Protocol.EffectTemplate.TwoPass, ids,
                new[] { Uniform.Vector2("center", p.Center), Uniform.Float("angle", Math.Max(p.Angle, 0f)) }.ToSpread(),
                new RectangleF(pad, pad, pad, pad));
        };

        private readonly ShaderEffectBuilder<Props> _link;

        public RadialBlurNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal RadialBlurNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new ShaderEffectBuilder<Props>(client, s_shaders, s_create, errors);

        internal ShaderEffectBuilder<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="center">Center of the rotation, 0..1 of the element area (0.5, 0.5 = middle).</param>
        /// <param name="angle">Total rotation of the blur in cycles (1 = full turn, 0 = no blur).</param>
        /// <param name="padding">Extra area around the element in DIP so the arcs have room: about half the angle in radians (Angle times 2π) times the distance of the farthest point from Center.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue("0.5, 0.5")] Vector2 center,
            [DefaultValue(0.05f)] float angle,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(30f)] float padding,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _link.Describe(input, new Props(center, angle, padding)) : input;

        public void Dispose() => _link.Dispose();
    }
}
