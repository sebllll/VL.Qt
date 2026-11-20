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
    /// Zoom blur: streaks along rays from Center, growing linearly from nothing at Center to Length at the farthest corner of
    /// the element area. Two passes of the same shader keep long blurs clean.
    /// Connect Output to the Effect pin of an element, or chain it with other effect nodes (data flow order: the node at the
    /// Effect pin acts last). A shader effect ends a run of Blur/DropShadow/... nodes and costs a layer of its own.
    /// </summary>
    [ProcessNode(Name = "ZoomBlur", Category = Categories.Effects)]
    public sealed class ZoomBlurNode : IDisposable
    {
        internal readonly record struct Props(Vector2 Center, float Length);

        private static readonly string[] s_shaders = { "zoom_blur" };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, Effect> s_create = static (previous, ids, p) =>
        {
            var length = Math.Max(p.Length, 0f);
            var pad = 0.6f * length;
            return new BuiltinShaderLinkEffect(previous, Protocol.EffectTemplate.TwoPass, ids,
                new[] { Uniform.Vector2("center", p.Center), Uniform.Length("blurLength", length) }.ToSpread(),
                new RectangleF(pad, pad, pad, pad));
        };

        private readonly BuiltinShaderLink<Props> _link;

        public ZoomBlurNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal ZoomBlurNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new BuiltinShaderLink<Props>(client, s_shaders, s_create, errors);

        internal BuiltinShaderLink<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="center">Center of the zoom, 0..1 of the element area (0.5, 0.5 = middle).</param>
        /// <param name="length">Streak length at the farthest corner, in units of the renderer's space (0 = no blur).</param>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue("0.5, 0.5")] Vector2 center,
            [DefaultValue(0.4f)] float length)
            => _link.Describe(input, new Props(center, length));

        public void Dispose() => _link.Dispose();
    }
}
