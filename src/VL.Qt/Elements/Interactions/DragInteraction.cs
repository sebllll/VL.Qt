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

        /// <summary>Limits of the drag offset in units of the renderer's space (converted to logical pixels in
        /// <see cref="Reconciliation.HandlerPropertyDiff"/>).</summary>
        public DragLimits Limits { get; }

        public PointerButtons Buttons { get; }
        public PointerDevices Devices { get; }
    }
}
