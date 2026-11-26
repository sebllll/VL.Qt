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
    /// Shifts the pixels of the element by a displacement map (an element), like Qt5Compat Displace: map red moves in x,
    /// green in y, 0.5 = no shift, the alpha of the map scales the shift. The map is stretched over the element's own size,
    /// also behind a Blur or other effects that make the picture bigger (outside the element the edge of the map continues);
    /// it is not drawn itself and does not count for layout. Without a Map the chain passes on unchanged.
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "Displace", Category = Categories.Effects)]
    public sealed class DisplaceNode : IDisposable
    {
        internal readonly record struct Props(Element? Map, Sampler? MapSampler, float Displacement);

        private static readonly ShaderEffectSource[] s_shaders = { ShaderEffectSource.Builtin("displace") };

        private static readonly Func<Effect?, ImmutableArray<ulong>, Props, ShaderEffect> s_create = static (previous, ids, p) =>
        {
            var shift = p.Displacement;
            // No factor: displace.frag moves a pixel by at most `displacement` (map alpha and channels are at most 1),
            // in any direction, so the content can come from or land that far outside the element on every side.
            var pad = MathF.Abs(shift);
            return new ShaderEffect(previous, Protocol.EffectTemplate.Single, ids,
                new[]
                {
                    Uniform.Float("displacement", shift),
                    Uniform.FromTexture("map", p.Map, p.MapSampler),
                }.ToSpread(),
                new RectangleF(pad, pad, pad, pad));
        };

        private readonly ShaderEffectBuilder<Props> _link;
        private readonly ElementInput _map;

        public DisplaceNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        internal DisplaceNode(ResourceClient? client, NodeErrors? errors = null)
        {
            _link = new ShaderEffectBuilder<Props>(client, s_shaders, s_create, errors);
            _map = new ElementInput(errors, "Map");
        }

        internal ShaderEffectBuilder<Props> Core => _link;

        /// <summary>Adds the effect to the effect chain.</summary>
        /// <param name="input">Effect chain to extend: this effect works on what the effect nodes before it made of the element.</param>
        /// <param name="map">Element used as displacement map (e.g. a ShaderEffect with noise). Unconnected: no effect.</param>
        /// <param name="mapSampler">How the map is read (filter, wrap, mipmaps), from the Sampler node. Outside the element the map
        /// continues as its Address says (Clamp: its edge). Unconnected: as the map is.</param>
        /// <param name="displacement">Largest shift in DIP (a map value of 0 or 1 moves the pixels by this).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            IElement? map,
            [Pin(Visibility = PinVisibility.Optional)] Sampler? mapSampler,
            [DefaultValue(10f)] float displacement,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
        {
            var element = _map.Update(map);
            return !apply || element is null ? input : _link.Describe(input, new Props(element, mapSampler, displacement));
        }

        public void Dispose()
        {
            _map.Dispose();
            _link.Dispose();
        }
    }
}
