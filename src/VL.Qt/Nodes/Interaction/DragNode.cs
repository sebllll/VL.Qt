using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lang;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Drags the element with mouse, touch or pen (Qt DragHandler). With Moves Element on, the element follows the
    /// drag by itself, no patching needed: the drag adds an offset to the position from the Transform pin or the layout,
    /// in the parent's space. The element's Scene Bounds output shows where it actually is. A changed Transform pin (also
    /// an animated one, e.g. by an LFO) puts the element back to the pin's position; after a restart the pin's value
    /// applies again (nothing is written back into the patch). A Group with Auto Size off and size 0 has no area to
    /// grab. Of nested draggable elements the innermost one takes the drag. Connect Output to the Interaction pin of
    /// an element, or chain it with other interaction nodes. Values arrive one frame later.
    /// </summary>
    /// <tags>drag, move, slider, touch, mouse, pen, offset</tags>
    [ProcessNode(Name = "Drag", Category = Categories.Interaction)]
    public sealed class DragNode : IDisposable
    {
        internal readonly record struct Props(bool MovesElement, DragAxis Axis, DragLimits Limits, PointerButtons Buttons, PointerDevices Devices);

        const string limitsError = "Minimum must not be greater than Maximum. The limits are ignored.";

        readonly InteractionNode<Props> _node;
        readonly Func<string, IDisposable> _addError;
        IChannel<bool>? _dragging;
        IChannel<Vector2>? _delta;
        bool _lastDragging;
        int _lastDragUpdates;
        IDisposable? _inputErrorMessage;

        public DragNode(NodeContext nodeContext)
        {
            _node = new InteractionNode<Props>(nodeContext, Create);
            _addError = e => nodeContext.AddPersistentMessage(MessageSeverity.Error, e);
        }

        /// <summary>For tests: warnings and input errors go to <paramref name="addMessage"/> instead of a NodeContext.</summary>
        internal DragNode(Func<string, IDisposable> addMessage)
        {
            _node = new InteractionNode<Props>(addMessage, Create);
            _addError = addMessage;
        }

        static readonly Func<Interaction?, Props, SinkSlot, Interaction> Create
            = static (previous, p, slot) => new DragInteraction(previous, slot, p.MovesElement, p.Axis, p.Limits, p.Buttons, p.Devices);

        internal InteractionNode<Props> Core => _node;

        /// <summary>Error about the inputs currently shown at the node (Minimum greater than Maximum), null if none.</summary>
        internal string? InputError { get; private set; }

        /// <summary>Adds a drag handler to the interaction chain.</summary>
        /// <param name="input">Interaction chain to extend (other interaction nodes); the order does not matter.</param>
        /// <param name="isDragging">Optional channel: true while the element is dragged (written by Qt, author "Qt").</param>
        /// <param name="delta">Optional channel: movement of the running drag since the press in units, in the parent's space, after Axis and the limits (author "Qt"). Written on change and once at the start of each drag; keeps the last drag's value after the release until the next drag.</param>
        /// <param name="movesElement">On: the element follows the drag. Off: the drag is only reported (Is Dragging, Delta), e.g. to move something else.</param>
        /// <param name="axis">Directions the element can be dragged in. With one axis, a drag mostly along the other one is left to the elements below (e.g. a parent with its own Drag).</param>
        /// <param name="minimum">Optional smallest offset of the element in units, relative to its position from the Transform pin or layout (e.g. -1, 0 allows dragging 1 unit to the left only). Not set: no limit.</param>
        /// <param name="maximum">Optional largest offset of the element in units, relative to its position from the Transform pin or layout. Not set: no limit. Must not be smaller than Minimum.</param>
        /// <param name="buttons">Mouse buttons that drag. None reacts to touch and pen only.</param>
        /// <param name="devices">Input devices that drag.</param>
        [return: Pin(Name = "Output")]
        public Interaction Update(
            Interaction? input,
            [DefaultValue(null)] IChannel<bool>? isDragging,
            [DefaultValue(null)] IChannel<Vector2>? delta,
            [DefaultValue(true)] bool movesElement,
            [DefaultValue(typeof(DragAxis), nameof(DragAxis.Both))] DragAxis axis,
            [Pin(Visibility = PinVisibility.Optional)] Optional<Vector2> minimum,
            [Pin(Visibility = PinVisibility.Optional)] Optional<Vector2> maximum,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerButtons), nameof(PointerButtons.Left))] PointerButtons buttons,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(PointerDevices), nameof(PointerDevices.All))] PointerDevices devices)
        {
            var output = _node.Describe(input, new Props(movesElement, axis, Limits(minimum, maximum), buttons, devices));

            // Pull: state of the last pump (1 frame latency).
            var sink = _node.Sink;
            if (isDragging.IsValid())
                InteractionNode<Props>.Write(isDragging, ref _dragging, ref _lastDragging, sink.IsActive());
            else
                _dragging = null;

            int updates = sink.DragUpdates();
            if (!delta.IsValid())
                _delta = null;
            else if (!ReferenceEquals(delta, _delta))
            {
                _delta = delta;
                _lastDragUpdates = updates;
                delta.EnsureValue(sink.DragDelta(), author: InteractionNode<Props>.Author);
            }
            else if (updates != _lastDragUpdates)
            {
                // Every EV_DRAG counts, also one equal to the last value (a new drag with the same movement).
                _lastDragUpdates = updates;
                delta.SetValueAndAuthor(sink.DragDelta(), InteractionNode<Props>.Author);
            }

            return output;
        }

        /// <summary>Limits of the offset in logical pixels; invalid (min &gt; max on an axis) → none, with an error at the node.</summary>
        DragLimits Limits(Optional<Vector2> minimum, Optional<Vector2> maximum)
        {
            var min = minimum.HasValue ? Geometry.ToQt(minimum.Value) : new Vector2(float.NegativeInfinity);
            var max = maximum.HasValue ? Geometry.ToQt(maximum.Value) : new Vector2(float.PositiveInfinity);
            bool valid = min.X <= max.X && min.Y <= max.Y; // NaN: invalid
            SetInputError(valid ? null : limitsError);
            return valid ? new DragLimits(min.X, min.Y, max.X, max.Y) : DragLimits.None;
        }

        void SetInputError(string? error)
        {
            if (ReferenceEquals(error, InputError))
                return;
            _inputErrorMessage?.Dispose();
            _inputErrorMessage = error is null ? null : _addError(error);
            InputError = error;
        }

        public void Dispose()
        {
            _inputErrorMessage?.Dispose();
            _inputErrorMessage = null;
            InputError = null;
            _node.Dispose();
        }
    }
}
