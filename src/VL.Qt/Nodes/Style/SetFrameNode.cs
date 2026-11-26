using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>Sets the shape of the frame for the given states. Unconnected pins stay unset (inherited). Colors: SetColors or
    /// SetColor/SetBorderColor.</summary>
    [ProcessNode(Name = "SetFrame", Category = "Qt.Style.Frame")]
    public sealed class SetFrameNode
    {
        private readonly record struct Key(Optional<float> radius, Optional<float> borderWidth, Optional<float> paddingX, Optional<float> paddingY, Optional<float> minWidth, Optional<float> minHeight, StyleStates states);

        private StyleCache<Key> _cache;

        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="radius">Corner radius in DIP.</param>
        /// <param name="borderWidth">Border width in DIP, 0 = no border.</param>
        /// <param name="paddingX">Space between frame and content, left and right, in DIP.</param>
        /// <param name="paddingY">Space between frame and content, top and bottom, in DIP.</param>
        /// <param name="minWidth">Minimum width of the control in DIP.</param>
        /// <param name="minHeight">Minimum height of the control in DIP.</param>
        /// <param name="states">States the values apply to, combinable with |.</param>
        /// <returns>The style with the values set. The same style as in the last frame while no input changed.</returns>
        [return: Pin(Name = "Output")]
        public Style Update(
            Style? input,
            Optional<float> radius,
            Optional<float> borderWidth,
            Optional<float> paddingX,
            Optional<float> paddingY,
            Optional<float> minWidth,
            Optional<float> minHeight,
            [DefaultValue(typeof(StyleStates), nameof(StyleStates.All))] StyleStates states)
        {
            var key = new Key(radius, borderWidth, paddingX, paddingY, minWidth, minHeight, states);
            return _cache.Get(input, key) ?? _cache.Store(input, key, FrameNodes.Apply(input, states, radius: radius, borderWidth: borderWidth, paddingX: paddingX, paddingY: paddingY, minWidth: minWidth, minHeight: minHeight));
        }
    }
}
