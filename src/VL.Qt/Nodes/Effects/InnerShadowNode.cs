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
    /// Shadow inside the element, like CSS <c>inset</c>: the inverted alpha of the element, shifted by Offset and blurred,
    /// laid over the element only where the element has content. A positive Offset lets the shadow fall from the left and top
    /// edge. Uses a blur pyramid, so the cost hardly depends on the radius.
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "InnerShadow", Category = Categories.Effects)]
    public sealed class InnerShadowNode : IDisposable
    {
        internal readonly record struct Props(Color4 Color, float Radius, Vector2 Offset, float Spread, float Intensity);

        private static readonly ShaderEffectSource[] s_shaders = { ShaderEffectSource.Builtin("innershadow_prefilter"), ShaderEffectSource.Builtin("innershadow_composite") };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, ShaderEffect> s_create = static (previous, ids, p) =>
        {
            var radius = Math.Max(p.Radius, 0f);
            // Room for the blur of the inverted alpha: it reaches about 3 sigma, see PyramidPadding.
            var pad = PyramidPadding.RadiusFactor * radius;
            return new ShaderEffect(previous, Protocol.EffectTemplate.Pyramid, ids,
                new[]
                {
                    Uniform.Color("color", p.Color),
                    Uniform.Vector2("offsetDip", p.Offset),
                    Uniform.Float("spread", p.Spread),
                    Uniform.Float("intensity", p.Intensity),
                }.ToSpread(),
                new RectangleF(pad, pad, pad, pad), radius);
        };

        private readonly ShaderEffectBuilder<Props> _link;

        public InnerShadowNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal InnerShadowNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new ShaderEffectBuilder<Props>(client, s_shaders, s_create, errors);

        internal ShaderEffectBuilder<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="color">Color of the shadow (its alpha scales the shadow).</param>
        /// <param name="radius">Blur radius of the shadow in DIP (0 = hard edge).</param>
        /// <param name="offset">Shift of the shadow in DIP: a positive (x, y) lets the shadow fall from the left and top edge.</param>
        /// <param name="spread">0..1: denser shadow, 0 = soft fall-off, towards 1 a nearly hard edge.</param>
        /// <param name="intensity">Strength of the shadow; values above 1 make it denser.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue("0, 0, 0, 0.6")] Color4 color,
            [DefaultValue(10f)] float radius,
            [DefaultValue("5, 5")] Vector2 offset,
            [Pin(Visibility = PinVisibility.Optional)] float spread,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float intensity,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _link.Describe(input, new Props(color, radius, offset, spread, intensity)) : input;

        public void Dispose() => _link.Dispose();
    }
}
