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
    /// Linear motion blur: smears the element along a line, centred on each pixel (the picture does not shift). Two passes of
    /// the same shader keep long blurs clean.
    /// Connect Output to the Effect pin of an element, or chain it with other effect nodes (data flow order: the node at the
    /// Effect pin acts last). A shader effect ends a run of Blur/DropShadow/... nodes and costs a layer of its own.
    /// </summary>
    [ProcessNode(Name = "DirectionalBlur", Category = Categories.Effects)]
    public sealed class DirectionalBlurNode : IDisposable
    {
        internal readonly record struct Props(float Angle, float Length);

        static readonly string[] s_shaders = { "directional_blur" };

        static readonly Func<Effect?, ImmutableArray<ulong>, Props, Effect> s_create = static (previous, ids, p) =>
        {
            var length = Math.Max(Geometry.ToQt(p.Length), 0f);
            var half = length / 2 + length / 64;
            var a = MathF.PI / 180f * p.Angle;
            var padX = EffectPadding.Ceil(half * MathF.Abs(MathF.Cos(a)));
            var padY = EffectPadding.Ceil(half * MathF.Abs(MathF.Sin(a)));
            return new BuiltinShaderLinkEffect(previous, Protocol.EffectTemplate.TwoPass, ids,
                new[] { Uniform.Float("angle", p.Angle) }.ToSpread(),
                new RectangleF(padX, padY, padX, padY), length, "blurLength");
        };

        readonly BuiltinShaderLink<Props> _link;

        public DirectionalBlurNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal DirectionalBlurNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new BuiltinShaderLink<Props>(client, s_shaders, s_create, errors);

        internal BuiltinShaderLink<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="angle">Direction of the blur in degrees: 0 = horizontal, increases clockwise on screen.</param>
        /// <param name="length">Length of the blur line in units of the renderer's space (total, 0 = no blur).</param>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            float angle,
            [DefaultValue(0.1f)] float length)
            => _link.Describe(input, new Props(angle, length));

        public void Dispose() => _link.Dispose();
    }
}
