using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>Sets the shape of the indicator (slider handle, check mark, switch knob, SpinBox -/+) for the given states. Unconnected pins stay unset (inherited). Color: SetColors or SetColor.</summary>
    [ProcessNode(Name = "SetIndicator", Category = "Qt.Style.Indicator")]
    public sealed class SetIndicatorNode
    {
        private readonly record struct Key(Optional<float> size, Optional<float> radius, Optional<float> borderWidth, StyleStates states);

        private StyleCache<Key> _cache;

        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="size">Size of the handle or knob in DIP.</param>
        /// <param name="radius">Corner radius in DIP.</param>
        /// <param name="borderWidth">Border width in DIP, 0 = no border.</param>
        /// <param name="states">States the values apply to, combinable with |.</param>
        /// <returns>The style with the values set. The same style as in the last frame while no input changed.</returns>
        [return: Pin(Name = "Output")]
        public Style Update(
            Style? input,
            Optional<float> size,
            Optional<float> radius,
            Optional<float> borderWidth,
            [DefaultValue(typeof(StyleStates), nameof(StyleStates.All))] StyleStates states)
        {
            var key = new Key(size, radius, borderWidth, states);
            return _cache.Get(input, key) ?? _cache.Store(input, key, IndicatorNodes.Apply(input, states, size: size, radius: radius, borderWidth: borderWidth));
        }
    }
}
