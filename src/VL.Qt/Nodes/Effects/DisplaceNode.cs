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
    /// Shifts the pixels of the element by a displacement map (an element), like Qt5Compat Displace: map red moves in x, green
    /// in y, 0.5 = no shift, the alpha of the map scales the shift. The map is stretched over the element's own size, also
    /// behind a Blur or other effects that make the picture bigger (outside the element the edge of the map continues); it is not
    /// drawn itself and does not count for layout. Without a Map the chain passes on unchanged.
    /// Connect Output to the Effect pin of an element, or chain it with other effect nodes (data flow order: the node at the
    /// Effect pin acts last). A shader effect ends a run of Blur/DropShadow/... nodes and costs a layer of its own.
    /// </summary>
    [ProcessNode(Name = "Displace", Category = Categories.Effects)]
    public sealed class DisplaceNode : IDisposable
    {
        internal readonly record struct Props(Element? Map, Sampler? MapSampler, float Displacement);

        private static readonly string[] s_shaders = { "displace" };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, Effect> s_create = static (previous, ids, p) =>
        {
            var shift = p.Displacement;
            var pad = MathF.Abs(shift);
            return new BuiltinShaderEffect(previous, Protocol.EffectTemplate.Single, ids,
                new[]
                {
                    Uniform.Length("displacement", shift),
                    Uniform.FromTexture("map", p.Map, p.MapSampler),
                }.ToSpread(),
                new RectangleF(pad, pad, pad, pad));
        };

        private readonly ShaderEffectBuilder<Props> _link;
        private readonly ElementInput _map;

        public DisplaceNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext), ElementInput.WarningsOf(nodeContext))
        {
        }

        internal DisplaceNode(ResourceClient? client, NodeErrors? errors = null, Func<string, IDisposable>? addWarning = null)
        {
            _link = new ShaderEffectBuilder<Props>(client, s_shaders, s_create, errors);
            _map = new ElementInput(addWarning);
        }

        internal ShaderEffectBuilder<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="map">Element used as displacement map (e.g. a ShaderEffect with noise). Unconnected: no effect.</param>
        /// <param name="mapSampler">How the map is read (filter, wrap, mipmaps), from the Sampler node. Outside the element the map continues as its Address says (Clamp: its edge). Unconnected: as the map is.</param>
        /// <param name="displacement">Largest shift in units of the renderer's space (a map value of 0 or 1 moves the pixels by this).</param>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            IElement? map,
            [Pin(Visibility = PinVisibility.Optional)] Sampler? mapSampler,
            [DefaultValue(0.1f)] float displacement)
        {
            var element = _map.Update(map);
            return element is null ? input : _link.Describe(input, new Props(element, mapSampler, displacement));
        }

        public void Dispose()
        {
            _map.Dispose();
            _link.Dispose();
        }
    }
}
