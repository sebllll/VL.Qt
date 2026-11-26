using System;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Uses an element as alpha mask (Qt MultiEffect mask), e.g. a Text as window onto a Video. The mask element is not drawn
    /// itself and does not count for layout, auto size or hit-testing. It lies in the coordinates of the element's parent
    /// like a sibling: with its own Size, Anchor and Transform it masks exactly the pixels it would cover as a sibling in the
    /// same Group, it is not stretched. Moving, rotating or scaling the masked element (also its place in a Row or Column)
    /// leaves the mask window where it is, and so do Blur or DropShadow growing the effect area. Where the mask has no
    /// content the element is invisible (Inverted flips that).
    /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/para"/>
    /// </summary>
    [ProcessNode(Name = "Mask", Category = Categories.Effects)]
    public sealed class MaskNode : IDisposable
    {
        internal readonly record struct Props(Element? Mask, float ThresholdMin, float SpreadAtMin, float ThresholdMax,
            float SpreadAtMax, bool Inverted);

        private static readonly Func<Effect?, Props, Effect> s_create = static (previous, p)
            => new MaskEffect(previous, p.Mask, p.ThresholdMin, p.SpreadAtMin, p.ThresholdMax, p.SpreadAtMax, p.Inverted);

        private ChainMemo<Effect, Props> _memo;

        private readonly NodeErrors? _messages;
        private readonly ElementInput _mask;

        public MaskNode(NodeContext nodeContext)
            : this(new NodeErrors(nodeContext))
        {
        }

        /// <param name="messages">Store of the node; null shows nothing.</param>
        internal MaskNode(NodeErrors? messages = null)
        {
            _messages = messages;
            _mask = new ElementInput(messages, "Mask");
        }

        internal ChainMemo<Effect, Props> Core => _memo;

        /// <summary>Adds a mask to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="mask">Element whose alpha masks the element (e.g. Text). Unconnected: no mask.</param>
        /// <param name="thresholdMin">Mask alpha below this hides the element, 0..1.</param>
        /// <param name="spreadAtMin">Softness of the edge at Threshold Min, 0..1.</param>
        /// <param name="thresholdMax">Mask alpha above this hides the element, 0..1 (1 = off).</param>
        /// <param name="spreadAtMax">Softness of the edge at Threshold Max, 0..1.</param>
        /// <param name="inverted">Shows the element where the mask is transparent.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/param[@name='apply']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='effect']/returns"/>
        [return: Pin(Name = "Output")]
        public Effect? Update(
            Effect? input,
            IElement? mask,
            [Pin(Visibility = PinVisibility.Optional)] float thresholdMin,
            [Pin(Visibility = PinVisibility.Optional)] float spreadAtMin,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float thresholdMax,
            [Pin(Visibility = PinVisibility.Optional)] float spreadAtMax,
            bool inverted,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool apply)
        {
            var element = _mask.Update(mask);
            return apply ? _memo.Get(input, new Props(element, thresholdMin, spreadAtMin, thresholdMax, spreadAtMax, inverted), s_create) : input;
        }

        public void Dispose()
        {
            _mask.Dispose();
            _messages?.Dispose();
        }
    }
}
