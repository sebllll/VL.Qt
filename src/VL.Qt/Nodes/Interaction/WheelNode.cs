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
    /// Reports mouse wheel and trackpad scrolling over the element (Qt WheelHandler) in notches. Does not scroll or
    /// move anything itself. Connect Output to the Interaction pin of an element, or chain it with other interaction
    /// nodes. Values arrive one frame later.
    /// </summary>
    /// <tags>scroll, mouse wheel, trackpad, zoom</tags>
    [ProcessNode(Name = "Wheel", Category = Categories.Interaction)]
    public sealed class WheelNode : IDisposable
    {
        internal readonly record struct Props(PointerDevices Devices);

        private readonly InteractionNodeCore<Props> _node;
        private IChannel<Vector2>? _delta;
        private Vector2 _lastDelta;

        public WheelNode(NodeContext nodeContext)
            => _node = new InteractionNodeCore<Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal WheelNode(Func<string, IDisposable> addWarning)
            => _node = new InteractionNodeCore<Props>(addWarning, Create);

        private static readonly Func<Interaction?, Props, FeedbackSlot, Interaction> Create
            = static (previous, p, slot) => new WheelInteraction(previous, slot, p.Devices);

        internal InteractionNodeCore<Props> Core => _node;

        /// <summary>Adds a wheel handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="delta">Optional channel: wheel motion since the last frame in notches (one notch = 120 wheel units; trackpads give fractions), x horizontal, y vertical, positive y = away from the user. Written every frame with motion, then once (0, 0) (author "Qt").</param>
        /// <param name="value">Optional channel: accumulated wheel motion in notches (same unit as Delta). Each frame with motion adds the delta to the current value of the channel (author "Qt"), so it can be set or reset from outside. No limits.</param>
        /// <param name="devices">Input devices that count.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='interaction']/returns"/>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<Vector2>? delta,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(null)] IChannel<Vector2>? value,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerDevices), nameof(PointerDevices.All))] PointerDevices devices)
        {
            var output = _node.Describe(input, new Props(devices));

            // Pull: the motion of the last pump, taken every frame (also unconnected, so it does not pile up).
            var wheel = _node.Feedback.TakeWheel();
            if (delta.IsValid())
            {
                if (!ReferenceEquals(delta, _delta))
                {
                    _delta = delta;
                    _lastDelta = default;
                }
                // Motion is written every frame (equal values are events), then once back to zero.
                if (wheel != default || _lastDelta != default)
                {
                    _lastDelta = wheel;
                    delta.SetValueAndAuthor(wheel, ChannelAuthors.QtChannelAuthor);
                }
            }
            else
                _delta = null;

            // Value: adds the motion to whatever the channel holds now (set/reset from outside is kept); nothing without motion.
            if (wheel != default && value.IsValid())
                value.SetValueAndAuthor(value.Value + wheel, ChannelAuthors.QtChannelAuthor);

            return output;
        }

        public void Dispose() => _node.Dispose();
    }
}
