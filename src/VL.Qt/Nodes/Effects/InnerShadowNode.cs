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
    /// Shadow inside the element, like CSS <c>inset</c>: the inverted alpha of the element, shifted by Offset and blurred, laid
    /// over the element only where the element has content. A positive Offset lets the shadow fall from the left and top
    /// edge. Uses a blur pyramid, so the cost hardly depends on the radius.
    /// Connect Output to the Effect pin of an element, or chain it with other effect nodes (data flow order: the node at the
    /// Effect pin acts last). A shader effect ends a run of Blur/DropShadow/... nodes and costs a layer of its own.
    /// </summary>
    [ProcessNode(Name = "InnerShadow", Category = Categories.Effects)]
    public sealed class InnerShadowNode : IDisposable
    {
        internal readonly record struct Props(Color4 Color, float Radius, Vector2 Offset, float Spread, float Intensity);

        private static readonly string[] s_shaders = { "innershadow_prefilter", "innershadow_composite" };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, Effect> s_create = static (previous, ids, p) =>
        {
            var radius = Math.Max(p.Radius, 0f);
            var pad = 1.5f * radius;
            return new BuiltinShaderLinkEffect(previous, Protocol.EffectTemplate.Pyramid, ids,
                new[]
                {
                    Uniform.Color("color", p.Color),
                    Uniform.LengthVector2("offsetDip", p.Offset),
                    Uniform.Float("spread", p.Spread),
                    Uniform.Float("intensity", p.Intensity),
                }.ToSpread(),
                new RectangleF(pad, pad, pad, pad), radius);
        };

        private readonly BuiltinShaderLink<Props> _link;

        public InnerShadowNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal InnerShadowNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new BuiltinShaderLink<Props>(client, s_shaders, s_create, errors);

        internal BuiltinShaderLink<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="color">Color of the shadow (its alpha scales the shadow).</param>
        /// <param name="radius">Blur radius of the shadow in units of the renderer's space (0 = hard edge).</param>
        /// <param name="offset">Shift of the shadow in units: a positive (x, y) lets the shadow fall from the left and top edge.</param>
        /// <param name="spread">0..1: denser shadow, 0 = soft fall-off, towards 1 a nearly hard edge.</param>
        /// <param name="intensity">Strength of the shadow; values above 1 make it denser.</param>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            // Pin defaults: see RectangleNode (string-valued [DefaultValue] for Vector2/Color4).
            [DefaultValue("0, 0, 0, 0.6")] Color4 color,
            [DefaultValue(0.1f)] float radius,
            [DefaultValue("0.05, 0.05")] Vector2 offset,
            [Pin(Visibility = PinVisibility.Optional)] float spread,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float intensity)
            => _link.Describe(input, new Props(color, radius, offset, spread, intensity));

        public void Dispose() => _link.Dispose();
    }
}
