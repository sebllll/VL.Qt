using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Blurs the element (Qt MultiEffect blur).
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "Blur", Category = Categories.Effects)]
    public sealed class BlurNode
    {
        internal readonly record struct Props(float Amount, int Max, float Multiplier, bool AutoPadding = true, RectangleF Padding = default);

        private static readonly Func<Effect?, Props, Effect> s_create = static (previous, p) => new BlurEffect(previous, p.Amount, p.Max, p.Multiplier, p.AutoPadding, p.Padding);

        private ChainMemo<Effect, Props> _memo;

        internal ChainMemo<Effect, Props> Core => _memo;

        /// <summary>Adds a blur to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="amount">Strength of the blur, 0..1 (0 = no blur).</param>
        /// <param name="maxRadius">Maximum blur radius in DIP at Amount 1; also limits the shadow blur. Changing it is expensive (switches shaders).</param>
        /// <param name="radiusMultiplier">Values above 0 enlarge the blur beyond Max Radius with lower quality.</param>
        /// <param name="autoPadding">On: the effect area grows by what the blur needs. Off: Padding applies. Qt has one effect area per element: if any Blur or DropShadow in the chain has this off, the area is the side-wise maximum of the Padding of all those links.</param>
        /// <param name="padding">Extra effect area around the element in DIP (x = left, y = top, width = right, height = bottom); only used with Auto Padding off.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue(0.5f)] float amount,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(32)] int maxRadius,
            [Pin(Visibility = PinVisibility.Optional)] float radiusMultiplier,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoPadding,
            [Pin(Visibility = PinVisibility.Optional)] RectangleF padding,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _memo.Get(input, new Props(amount, maxRadius, radiusMultiplier, autoPadding, padding), s_create) : input;
    }
}
