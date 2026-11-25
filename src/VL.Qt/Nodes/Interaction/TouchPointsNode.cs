using System;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Reports all fingers (touch points) that are pressed on the element, with id, position, pressure and size
    /// (own Qt Quick input handler with passive grabs). A point belongs to the element from its press inside it until
    /// its release, also when the finger moves out. It works next to Click, Point, Drag and Pinch at the same element.
    /// By default a finger is not seen when an element on top takes the press (Click or Drag on it, a Button); turn
    /// Occlusion off to see all fingers under it too. Connect Output to the Interaction pin of an element, or chain it
    /// with other interaction nodes. Values arrive one frame later.
    /// </summary>
    /// <tags>touch, multitouch, finger, fingers, points, pen, pressure, id</tags>
    [ProcessNode(Name = "Touch Points", Category = Categories.Interaction)]
    public sealed class TouchPointsNode : IDisposable
    {
        internal readonly record struct Props(bool Occlusion, PointerDevices Devices);

        private readonly InteractionNode<Props> _node;
        private IChannel<Spread<TouchPoint>>? _points;
        private int _lastUpdates;

        public TouchPointsNode(NodeContext nodeContext)
            => _node = new InteractionNode<Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal TouchPointsNode(Func<string, IDisposable> addWarning)
            => _node = new InteractionNode<Props>(addWarning, Create);

        private static readonly Func<Interaction?, Props, FeedbackSlot, Interaction> Create
            = static (previous, p, slot) => new TouchPointsInteraction(previous, slot, p.Occlusion, p.Devices);

        internal InteractionNode<Props> Core => _node;

        /// <summary>Adds a touch points handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="points">Optional channel: the points pressed on the element right now, sorted by id; empty when there are none (written by Qt on change, author "Qt"). Positions are local to the element in units; Pressure and Size depend on the device (many touchscreens report 0.5 and 0 x 0).</param>
        /// <param name="devices">Input devices that count. The default is touch screen and pen; add Mouse to see the mouse as a point too.</param>
        /// <param name="occlusion">On: a finger whose press an element on top takes (Click or Drag on it, a Button) is not reported here. Off: all fingers on the element are reported.</param>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<Spread<TouchPoint>>? points,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerDevices), "TouchScreen, Pen")] PointerDevices devices,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool occlusion)
        {
            var output = _node.Describe(input, new Props(occlusion, devices));

            // Pull: state of the last pump (1 frame latency). A new Spread only when Qt reported a change; at rest
            // nothing is built or written. A channel connected later gets the current points, but only if there was
            // any event yet (an untouched feedback would overwrite the value of the channel for nothing).
            if (!points.IsValid())
            {
                _points = null;
                return output;
            }
            var feedback = _node.Feedback;
            int updates = feedback.TouchUpdates();
            if (!ReferenceEquals(points, _points))
            {
                _points = points;
                _lastUpdates = updates;
                if (updates != 0)
                    points.SetValueAndAuthor(feedback.TouchPoints(), ChannelAuthors.QtChannelAuthor);
            }
            else if (updates != _lastUpdates)
            {
                _lastUpdates = updates;
                points.SetValueAndAuthor(feedback.TouchPoints(), ChannelAuthors.QtChannelAuthor);
            }
            return output;
        }

        public void Dispose() => _node.Dispose();
    }
}
