using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>Sets the colors of all parts for the given states. Unconnected pins stay unset (inherited).</summary>
    [ProcessNode(Name = "SetColors", Category = "Qt.Style")]
    public sealed class SetColorsNode
    {
        private readonly record struct Key(Optional<Color4> frame, Optional<Color4> border, Optional<Color4> text, Optional<Color4> indicator, Optional<Color4> track, Optional<Color4> trackFill, StyleStates states);

        private StyleCache<Key> _cache;

        /// <param name="input">Style to add to. Empty if not connected.</param>
        /// <param name="frame">Background of the control.</param>
        /// <param name="border">Border of the frame.</param>
        /// <param name="text">Text color.</param>
        /// <param name="indicator">Slider handle, check mark, switch knob, SpinBox -/+.</param>
        /// <param name="track">Slider groove, switch track, check box.</param>
        /// <param name="trackFill">Slider value, switch on, text selection.</param>
        /// <param name="states">States the values apply to, combinable with |.</param>
        /// <returns>The style with the values set. The same style as in the last frame while no input changed.</returns>
        [return: Pin(Name = "Output")]
        public Style Update(
            Style? input,
            Optional<Color4> frame,
            Optional<Color4> border,
            Optional<Color4> text,
            Optional<Color4> indicator,
            Optional<Color4> track,
            Optional<Color4> trackFill,
            [DefaultValue(typeof(StyleStates), nameof(StyleStates.All))] StyleStates states)
        {
            var key = new Key(frame, border, text, indicator, track, trackFill, states);
            return _cache.Get(input, key) ?? _cache.Store(input, key, StyleNodes.ApplyColors(input, states, frame, border, text, indicator, track, trackFill));
        }
    }
}
