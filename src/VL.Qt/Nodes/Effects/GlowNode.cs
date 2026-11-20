using System;
using System.Collections.Immutable;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Soft glow around the element: its alpha, blurred and tinted, behind the element or added on top. Uses a blur pyramid, so
    /// the cost hardly depends on the radius, and the radius can be animated smoothly up to several hundred DIP.
    /// Connect Output to the Effect pin of an element, or chain it with other effect nodes (data flow order: the node at the
    /// Effect pin acts last). A shader effect ends a run of Blur/DropShadow/... nodes and costs a layer of its own.
    /// </summary>
    [ProcessNode(Name = "Glow", Category = Categories.Effects)]
    public sealed class GlowNode : IDisposable
    {
        internal readonly record struct Props(Color4 Color, float Radius, float Spread, float Intensity, bool Additive);

        private static readonly string[] s_shaders = { "glow_prefilter", "glow_composite" };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, Effect> s_create = static (previous, ids, p) =>
        {
            var radius = Math.Max(p.Radius, 0f);
            var pad = 1.5f * radius;
            return new BuiltinShaderLinkEffect(previous, Protocol.EffectTemplate.Pyramid, ids,
                new[]
                {
                    Uniform.Color("color", p.Color),
                    Uniform.Float("spread", p.Spread),
                    Uniform.Float("intensity", p.Intensity),
                    Uniform.Bool("additive", p.Additive),
                }.ToSpread(),
                new RectangleF(pad, pad, pad, pad), radius);
        };

        private readonly BuiltinShaderLink<Props> _link;

        public GlowNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal GlowNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new BuiltinShaderLink<Props>(client, s_shaders, s_create, errors);

        internal BuiltinShaderLink<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="color">Color of the glow (its alpha scales the glow).</param>
        /// <param name="radius">How far the glow reaches, in units of the renderer's space (0 = no glow).</param>
        /// <param name="spread">0..1: denser glow, 0 = soft fall-off, towards 1 a nearly hard edge.</param>
        /// <param name="intensity">Strength of the glow; values above 1 glow brighter.</param>
        /// <param name="additive">Off: the glow lies behind the element. On: it is added on top of the element.</param>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            // Pin defaults: see RectangleNode (string-valued [DefaultValue] for Vector2/Color4).
            [DefaultValue("0.2, 0.7, 1, 1")] Color4 color,
            [DefaultValue(0.2f)] float radius,
            [Pin(Visibility = PinVisibility.Optional)] float spread,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float intensity,
            [Pin(Visibility = PinVisibility.Optional)] bool additive)
            => _link.Describe(input, new Props(color, radius, spread, intensity, additive));

        public void Dispose() => _link.Dispose();
    }
}
