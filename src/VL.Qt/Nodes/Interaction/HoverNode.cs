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

        readonly InteractionNode<Props> _node;
        IChannel<bool>? _hovered;
        bool _lastHovered;

        public HoverNode(NodeContext nodeContext)
            => _node = new InteractionNode<Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal HoverNode(Func<string, IDisposable> addWarning)
            => _node = new InteractionNode<Props>(addWarning, Create);

        static readonly Func<Interaction?, Props, SinkSlot, Interaction> Create
            = static (previous, p, slot) => new HoverInteraction(previous, slot);

        internal InteractionNode<Props> Core => _node;

        /// <summary>Adds a hover handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="isHovered">Optional channel: true while the pointer is over the element (written by Qt, author "Qt").</param>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<bool>? isHovered)
        {
            var output = _node.Describe(input, default(Props));

            // Pull: state of the last pump (1 frame latency); writes only on change.
            var sink = _node.Sink;
            if (isHovered.IsValid())
                InteractionNode<Props>.Write(isHovered, ref _hovered, ref _lastHovered, sink.IsHovered());
            else
                _hovered = null;

            return output;
        }

        public void Dispose() => _node.Dispose();
    }
}
