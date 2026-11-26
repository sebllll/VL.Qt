using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>Tap handler (Qt Quick TapHandler): press, click, tap count, long press.</summary>
    internal sealed class ClickInteraction : Interaction
    {
        public ClickInteraction(Interaction? previous, FeedbackSlot feedback, PointerButtons buttons, PointerDevices devices)
            : base(Protocol.ElementType.TapHandler, previous, feedback)
        {
            Buttons = buttons;
            Devices = devices;
        }

        public PointerButtons Buttons { get; }
        public PointerDevices Devices { get; }
    }
}
