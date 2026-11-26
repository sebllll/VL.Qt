using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Tints the element with a color (Qt MultiEffect colorization).
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "Colorize", Category = Categories.Effects)]
    public sealed class ColorizeNode
    {
        internal readonly record struct Props(Color4 Color, float Amount);

        private static readonly Func<Effect?, Props, Effect> s_create = static (previous, p) => new ColorizeEffect(previous, p.Color, p.Amount);

        private ChainMemo<Effect, Props> _memo;

        internal ChainMemo<Effect, Props> Core => _memo;

        /// <summary>Adds a colorization to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="color">Tint color.</param>
        /// <param name="amount">Strength of the tint, 0..1 (0 = off).</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue("1, 0, 0, 1")] Color4 color,
            [DefaultValue(1f)] float amount,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _memo.Get(input, new Props(color, amount), s_create) : input;
    }
}
