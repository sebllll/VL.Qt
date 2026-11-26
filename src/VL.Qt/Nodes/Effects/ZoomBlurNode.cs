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
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "ZoomBlur", Category = Categories.Effects)]
    public sealed class ZoomBlurNode : IDisposable
    {
        internal readonly record struct Props(Vector2 Center, float Length);

        private static readonly ShaderEffectSource[] s_shaders = { ShaderEffectSource.Builtin("zoom_blur") };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, ShaderEffect> s_create = static (previous, ids, p) =>
        {
            var length = Math.Max(p.Length, 0f);
            // The streak is `length` long at the farthest corner and centred on the pixel, so it reaches length / 2 to
            // each side there (less elsewhere); pass 2 adds length / 128 (see zoom_blur.frag). 0.6 covers the 0.508 of
            // that with a margin; the shader file template of effects-and-shaders.md uses 0.52.
            var pad = 0.6f * length;
            return new ShaderEffect(previous, Protocol.EffectTemplate.TwoPass, ids,
                new[] { Uniform.Vector2("center", p.Center), Uniform.Float("blurLength", length) }.ToSpread(),
                new RectangleF(pad, pad, pad, pad));
        };

        private readonly ShaderEffectBuilder<Props> _link;

        public ZoomBlurNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal ZoomBlurNode(ResourceClient? client, NodeErrors? errors = null)
            => _link = new ShaderEffectBuilder<Props>(client, s_shaders, s_create, errors);

        internal ShaderEffectBuilder<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="center">Center of the zoom, 0..1 of the element area (0.5, 0.5 = middle).</param>
        /// <param name="length">Streak length at the farthest corner, in DIP (0 = no blur).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue("0.5, 0.5")] Vector2 center,
            [DefaultValue(40f)] float length,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _link.Describe(input, new Props(center, length)) : input;

        public void Dispose() => _link.Dispose();
    }
}
