using System;
using System.Collections.Immutable;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Blends a layer (an element) over the element with a blend mode like Multiply, Screen, Overlay, Add, Difference or Soft
    /// Light. The element, with the effect nodes before this one, is the base. The layer is stretched over the element's own
    /// size, also behind a Blur or other effects that make the picture bigger (outside the element the layer is transparent),
    /// and is not drawn itself and does not count for layout. Where the layer is transparent the base stays unchanged.
    /// Without a Layer the chain passes on unchanged.
    /// Connect Output to the Effect pin of an element, or chain it with other effect nodes (data flow order: the node at the
    /// Effect pin acts last). A shader effect ends a run of Blur/DropShadow/... nodes and costs a layer of its own.
    /// </summary>
    [ProcessNode(Name = "Blend", Category = Categories.Effects)]
    public sealed class BlendNode : IDisposable
    {
        internal readonly record struct Props(Element? Layer, Sampler? LayerSampler, BlendMode Mode, float Opacity);

        private static readonly ShaderEffectSource[] s_shaders = { ShaderEffectSource.Builtin("blend") };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, ShaderEffect> s_create = static (previous, ids, p) =>
            new ShaderEffect(previous, Protocol.EffectTemplate.Single, ids,
                new[]
                {
                    Uniform.Int("mode", (int)p.Mode),
                    Uniform.Float("layerOpacity", p.Opacity),
                    Uniform.FromTexture("layer", p.Layer, p.LayerSampler),
                }.ToSpread(),
                default);

        private readonly ShaderEffectBuilder<Props> _link;
        private readonly ElementInput _layer;

        public BlendNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal BlendNode(ResourceClient? client, NodeErrors? errors = null)
        {
            _link = new ShaderEffectBuilder<Props>(client, s_shaders, s_create, errors);
            _layer = new ElementInput(errors, "Layer");
        }

        internal ShaderEffectBuilder<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="layer">Element blended over the base (e.g. a gradient, an image, a ShaderEffect). Unconnected: no effect.</param>
        /// <param name="layerSampler">How the layer is read (filter, mipmaps), from the Sampler node. The address has no effect here: outside the element the layer is transparent. Unconnected: as the layer is.</param>
        /// <param name="mode">How the layer and the base are combined.</param>
        /// <param name="opacity">Strength of the layer, 0 = base only, 1 = full blend.</param>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            IElement? layer,
            [Pin(Visibility = PinVisibility.Optional)] Sampler? layerSampler,
            BlendMode mode,
            [DefaultValue(1f)] float opacity)
        {
            var element = _layer.Update(layer);
            return element is null ? input : _link.Describe(input, new Props(element, layerSampler, mode, opacity));
        }

        public void Dispose()
        {
            _layer.Dispose();
            _link.Dispose();
        }
    }
}
