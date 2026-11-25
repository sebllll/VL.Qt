using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Draws a soft shadow behind the element (Qt MultiEffect shadow).
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// The shadow does not change the element's size or layout.
    /// </summary>
    [ProcessNode(Name = "DropShadow", Category = Categories.Effects)]
    public sealed class DropShadowNode
    {
        internal readonly record struct Props(Color4 Color, Vector2 Offset, float Blur, float Scale, bool AutoPadding = true, RectangleF Padding = default);

        private static readonly Func<Effect?, Props, Effect> s_create = static (previous, p) => new DropShadowEffect(previous, p.Color, p.Offset, p.Blur, p.Scale, p.AutoPadding, p.Padding);

        private ChainMemo<Effect, Props> _memo;

        internal ChainMemo<Effect, Props> Core => _memo;

        /// <summary>Adds a drop shadow to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="color">Shadow color; its alpha is the shadow's opacity.</param>
        /// <param name="offset">Shadow offset (x, y) in DIP.</param>
        /// <param name="blurAmount">Softness of the shadow, 0..1 (limited by Max Radius of a Blur in the chain, default 32 DIP).</param>
        /// <param name="scale">Size of the shadow relative to the element, around its center.</param>
        /// <param name="autoPadding">On: the effect area grows by what the shadow needs. Off: Padding applies. Qt has one effect area per element: if any Blur or DropShadow in the chain has this off, the area is the side-wise maximum of the Padding of all those links.</param>
        /// <param name="padding">Extra effect area around the element in DIP (x = left, y = top, width = right, height = bottom); only used with Auto Padding off.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            [DefaultValue("0, 0, 0, 1")] Color4 color,
            [DefaultValue("5, 5")] Vector2 offset,
            [DefaultValue(1f)] float blurAmount,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float scale,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoPadding,
            [Pin(Visibility = PinVisibility.Optional)] RectangleF padding,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
            => apply ? _memo.Get(input, new Props(color, offset, blurAmount, scale, autoPadding, padding), s_create) : input;
    }
}
