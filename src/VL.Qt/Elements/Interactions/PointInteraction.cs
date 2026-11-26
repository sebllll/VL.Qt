using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>Point handler (Qt Quick PointHandler): position of a pressed point; a passive grab, presses pass through.</summary>
    internal sealed class PointInteraction : Interaction
    {
        public PointInteraction(Interaction? previous, FeedbackSlot feedback, PointerButtons buttons, PointerDevices devices)
            : base(Protocol.ElementType.PointHandler, previous, feedback)
        {
            Buttons = buttons;
            Devices = devices;
        }

        public PointerButtons Buttons { get; }
        public PointerDevices Devices { get; }
    }
}
