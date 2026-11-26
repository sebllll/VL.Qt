using System;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Reports when the pointer (mouse, pen, touch) is over the element (Qt HoverHandler). Connect Output to the
    /// Interaction pin of an element, or chain it with other interaction nodes. Does not take clicks from elements
    /// below; hover goes to all elements under the pointer. Values arrive one frame later. For the pointer position use
    /// a Point node (with Buttons None it follows the hovering mouse). With touch, Is Hovered is true as long as a finger
    /// is down on the element.
    /// </summary>
    [ProcessNode(Name = "Hover", Category = Categories.Interaction)]
    public sealed class HoverNode : IDisposable
    {
        internal readonly record struct Props;

        private readonly InteractionNodeCore<Props> _node;
        private IChannel<bool>? _hovered;
        private bool _lastHovered;

        public HoverNode(NodeContext nodeContext)
            => _node = new InteractionNodeCore<Props>(nodeContext, Create);

        internal HoverNode(NodeErrors messages)
            => _node = new InteractionNodeCore<Props>(messages, Create);

        private static readonly Func<Interaction?, Props, FeedbackSlot, Interaction> Create
            = static (previous, p, slot) => new HoverInteraction(previous, slot);

        internal InteractionNodeCore<Props> Core => _node;

        /// <summary>Adds a hover handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="isHovered">Optional channel: true while the pointer is over the element (written by Qt, author "Qt").</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='interaction']/returns"/>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<bool>? isHovered)
        {
            var output = _node.Describe(input, default(Props));

            // Pull: state of the last pump (1 frame latency); writes only on change.
            var feedback = _node.Feedback;
            FeedbackChannels.WriteState(isHovered, ref _hovered, ref _lastHovered, feedback.IsHovered());

            return output;
        }

        public void Dispose() => _node.Dispose();
    }
}
