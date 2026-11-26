using System;
using System.ComponentModel;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Adjusts brightness, contrast and saturation of the element (Qt MultiEffect).
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "ColorAdjust", Category = Categories.Effects)]
    public sealed class ColorAdjustNode
    {
        internal readonly record struct Props(float Brightness, float Contrast, float Saturation);

        private static readonly Func<Effect?, Props, Effect> s_create = static (previous, p) => new ColorAdjustEffect(previous, p.Brightness, p.Contrast, p.Saturation - 1f);

        private ChainMemo<Effect, Props> _memo;

        internal ChainMemo<Effect, Props> Core => _memo;

        /// <summary>Adds a color adjustment to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="brightness">-1..1, 0 = unchanged.</param>
        /// <param name="contrast">-1..1, 0 = unchanged.</param>
        /// <param name="saturation">0..2, 1 = unchanged, 0 = grey (default, so the effect is visible), above 1 more colorful; the scale of HSCB.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            float brightness,
            float contrast,
            float saturation,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _memo.Get(input, new Props(brightness, contrast, saturation), s_create) : input;
    }
}
