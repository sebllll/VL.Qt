using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Drag handler (Qt Quick DragHandler, target null): reports the running drag and, with
    /// <see cref="MovesElement"/>, moves the element by its drag offset in QtNative. Limits are in units of the renderer's
    /// space, ±infinity = none; the node validates min ≤ max before.
    /// </summary>
    internal sealed class DragInteraction : Interaction
    {
        public DragInteraction(Interaction? previous, FeedbackSlot feedback, bool movesElement, DragAxis axis, DragLimits limits,
            PointerButtons buttons, PointerDevices devices)
            : base(Protocol.ElementType.DragHandler, previous, feedback)
        {
            MovesElement = movesElement;
            Axis = axis;
            Limits = limits;
            Buttons = buttons;
            Devices = devices;
        }

        public bool MovesElement { get; }
        public DragAxis Axis { get; }

        /// <summary>Limits of the drag offset in units of the renderer's space (converted in <see cref="WriteProperties"/>).</summary>
        public DragLimits Limits { get; }

        public PointerButtons Buttons { get; }
        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
            var p = previous as DragInteraction;
            if (MovesElement != (p?.MovesElement ?? true))
                sink.SetBool(id, Protocol.PropertyId.MovesElement, MovesElement);
            if (Axis != (p?.Axis ?? DragAxis.Both))
                sink.SetI32(id, Protocol.PropertyId.DragAxis, (int)Axis);
            if (Limits != (p?.Limits ?? DragLimits.None))
                sink.SetRect(id, Protocol.PropertyId.DragLimits, mapping.ToQt(Limits.MinX), mapping.ToQt(Limits.MinY), mapping.ToQt(Limits.MaxX), mapping.ToQt(Limits.MaxY));
            PointerProps.Write(sink, id, Buttons, Devices, p?.Buttons ?? PointerMasks.DefaultButtons,
                p?.Devices ?? PointerMasks.DefaultDevices, writeButtons: true);
        }
    }
}
