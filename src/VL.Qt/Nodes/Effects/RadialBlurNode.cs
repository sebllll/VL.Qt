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
    /// Radial (rotational) blur: arcs around Center, a box blur over a rotation of Angle degrees (centred, the picture does not
    /// turn). Two passes of the same shader keep large angles clean.
    /// Connect Output to the Effect pin of an element, or chain it with other effect nodes (data flow order: the node at the
    /// Effect pin acts last). A shader effect ends a run of Blur/DropShadow/... nodes and costs a layer of its own.
    /// </summary>
    [ProcessNode(Name = "RadialBlur", Category = Categories.Effects)]
    public sealed class RadialBlurNode : IDisposable
    {
        internal readonly record struct Props(Vector2 Center, float Angle, float Padding);

        static readonly string[] s_shaders = { "radial_blur" };

        static readonly Func<Effect?, ImmutableArray<ulong>, Props, Effect> s_create = static (previous, ids, p) =>
        {
            var pad = EffectPadding.Ceil(Math.Max(Geometry.ToQt(p.Padding), 0f));
            return new BuiltinShaderLinkEffect(previous, Protocol.EffectTemplate.TwoPass, ids,
                new[] { Uniform.Vector2("center", p.Center), Uniform.Float("angle", Math.Max(p.Angle, 0f)) }.ToSpread(),
                new RectangleF(pad, pad, pad, pad));
        };

        readonly BuiltinShaderLink<Props> _link;

        public RadialBlurNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal RadialBlurNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new BuiltinShaderLink<Props>(client, s_shaders, s_create, errors);

        internal BuiltinShaderLink<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="center">Center of the rotation, 0..1 of the element area (0.5, 0.5 = middle).</param>
        /// <param name="angle">Total rotation of the blur in degrees (0 = no blur).</param>
        /// <param name="padding">Extra area around the element in units so the arcs have room: about half the angle in radians times the distance of the farthest point from Center.</param>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue("0.5, 0.5")] Vector2 center,
            [DefaultValue(8f)] float angle,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(0.3f)] float padding)
            => _link.Describe(input, new Props(center, angle, padding));

        public void Dispose() => _link.Dispose();
    }
}
