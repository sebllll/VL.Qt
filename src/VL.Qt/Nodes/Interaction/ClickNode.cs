using System;
using System.ComponentModel;
using System.Reactive;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Reports presses and clicks (taps) on the element (Qt TapHandler): works with mouse, touch and pen. A click is a
    /// press and release inside the element; of overlapping elements the topmost one with a Click node takes it.
    /// Connect Output to the Interaction pin of an element, or chain it with other interaction nodes. Values arrive
    /// one frame later.
    /// </summary>
    /// <tags>tap, touch, press, mouse, button, double click</tags>
    [ProcessNode(Name = "Click", Category = Categories.Interaction)]
    public sealed class ClickNode : IDisposable
    {
        internal readonly record struct Props(PointerButtons Buttons, PointerDevices Devices);

        private readonly InteractionNode<Props> _node;
        private IChannel<bool>? _pressed;
        private IChannel<int>? _tapCount;
        private bool _lastPressed;
        private int _lastTapCount;

        public ClickNode(NodeContext nodeContext)
            => _node = new InteractionNode<Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ClickNode(Func<string, IDisposable> addWarning)
            => _node = new InteractionNode<Props>(addWarning, Create);

        private static readonly Func<Interaction?, Props, FeedbackSlot, Interaction> Create
            = static (previous, p, slot) => new ClickInteraction(previous, slot, p.Buttons, p.Devices);

        internal InteractionNode<Props> Core => _node;

        /// <summary>Adds a click (tap) handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="isPressed">Optional channel: true while the element is pressed (written by Qt, author "Qt").</param>
        /// <param name="isClicked">Optional channel: receives one value per click, i.e. a release inside the element (author "Qt").</param>
        /// <param name="tapCount">Optional channel: number of quick successive clicks of the last click (1 = single click, 2 = double click, 3 = triple click, ...). A click after a pause (longer than the double click interval) starts again at 1. Written on change; keeps its value until the next click (never written as 0). For a total number of clicks, count the values of Is Clicked. Only reported while connected.</param>
        /// <param name="longPressed">Optional channel: receives one value when the press is held long enough (Qt's long press time).</param>
        /// <param name="buttons">Mouse buttons that count. None reacts to touch and pen only.</param>
        /// <param name="devices">Input devices that count.</param>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<bool>? isPressed,
            [DefaultValue(null)] IChannel<Unit>? isClicked,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(null)] IChannel<int>? tapCount,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(null)] IChannel<Unit>? longPressed,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerButtons), nameof(PointerButtons.Left))] PointerButtons buttons,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerDevices), nameof(PointerDevices.All))] PointerDevices devices)
        {
            var output = _node.Describe(input, new Props(buttons, devices));

            // Pull: state of the last pump (1 frame latency). Events (clicks, long presses) are taken every frame, also
            // without a channel, so they do not pile up; states are written only on change.
            var feedback = _node.Feedback;
            if (isPressed.IsValid())
                InteractionNode<Props>.Write(isPressed, ref _pressed, ref _lastPressed, feedback.IsPressed());
            else
                _pressed = null;

            InteractionNode<Props>.WriteEvents(isClicked, feedback.TakeClicks());
            InteractionNode<Props>.WriteEvents(longPressed, feedback.TakeLongPresses());

            // 0 = no tap yet for this feedback (new node, new handler, released handle): not written, the channel keeps
            // the count of the last click until the next one.
            int count = feedback.TapCount();
            if (!tapCount.IsValid())
                _tapCount = null;
            else if (count > 0)
                InteractionNode<Props>.Write(tapCount, ref _tapCount, ref _lastTapCount, count);

            return output;
        }

        public void Dispose() => _node.Dispose();
    }
}
