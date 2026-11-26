using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>Sets the shape of the track (slider groove, switch track, check box) for the given states. Unconnected pins stay unset
    /// (inherited). Colors: SetColors or SetColor/SetFillColor.</summary>
    [ProcessNode(Name = "SetTrack", Category = "Qt.Style.Track")]
    public sealed class SetTrackNode
    {
        private readonly record struct Key(Optional<float> thickness, Optional<float> radius, StyleStates states);

        private StyleCache<Key> _cache;

        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="thickness">Thickness of the groove or track in DIP.</param>
        /// <param name="radius">Corner radius in DIP.</param>
        /// <param name="states">States the values apply to, combinable with |.</param>
        /// <returns>The style with the values set. The same style as in the last frame while no input changed.</returns>
        [return: Pin(Name = "Output")]
        public Style Update(
            Style? input,
            Optional<float> thickness,
            Optional<float> radius,
            [DefaultValue(typeof(StyleStates), nameof(StyleStates.All))] StyleStates states)
        {
            var key = new Key(thickness, radius, states);
            return _cache.Get(input, key) ?? _cache.Store(input, key, TrackNodes.Apply(input, states, thickness: thickness, radius: radius));
        }
    }
}
