using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Reports the position of a point on the element (Qt PointHandler): mouse button down, finger or pen. With
    /// Buttons None the mouse counts while it is over the element (hover position, no press needed); touch and pen
    /// still count while down. The point is only observed, presses still reach the handlers below (use Click to take
    /// them). Connect Output to the Interaction pin of an element, or chain it with other interaction nodes. Values
    /// arrive one frame later.
    /// </summary>
    /// <tags>pointer, touch, mouse, pen, position</tags>
    [ProcessNode(Name = "Point", Category = Categories.Interaction)]
    public sealed class PointNode : IDisposable
    {
        internal readonly record struct Props(PointerButtons Buttons, PointerDevices Devices);

        readonly InteractionNode<Props> _node;
        IChannel<bool>? _active;
        IChannel<Vector2>? _position;
        bool _lastActive;
        Vector2 _lastPosition;

        public PointNode(NodeContext nodeContext)
            => _node = new InteractionNode<Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal PointNode(Func<string, IDisposable> addWarning)
            => _node = new InteractionNode<Props>(addWarning, Create);

        static readonly Func<Interaction?, Props, SinkSlot, Interaction> Create
            = static (previous, p, slot) => new PointInteraction(previous, slot, p.Buttons, p.Devices);

        internal InteractionNode<Props> Core => _node;

        /// <summary>Adds a point handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="isActive">Optional channel: true while a point (mouse button, finger, pen) is down on the element, with Buttons None also while the mouse is over it, i.e. while Position is valid (written by Qt, author "Qt").</param>
        /// <param name="position">Optional channel: position of the point local to the element, in units, written on change while active (author "Qt"). Keeps the last position after the release or when the mouse leaves.</param>
        /// <param name="buttons">Mouse buttons that count. None: the mouse counts while it hovers the element (no press needed), touch and pen while down.</param>
        /// <param name="devices">Input devices that count.</param>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<bool>? isActive,
            [DefaultValue(null)] IChannel<Vector2>? position,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerButtons), nameof(PointerButtons.Left))] PointerButtons buttons,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerDevices), nameof(PointerDevices.All))] PointerDevices devices)
        {
            var output = _node.Describe(input, new Props(buttons, devices));

            // Pull: state of the last pump (1 frame latency); writes only on change.
            var sink = _node.Sink;
            if (isActive.IsValid())
                InteractionNode<Props>.Write(isActive, ref _active, ref _lastActive, sink.IsActive());
            else
                _active = null;

            if (position.IsValid())
                InteractionNode<Props>.Write(position, ref _position, ref _lastPosition, sink.PointerPosition());
            else
                _position = null;

            return output;
        }

        public void Dispose() => _node.Dispose();
    }
}
