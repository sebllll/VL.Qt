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

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
            => PointerProps.Write(sink, id, Buttons, Devices, previous is ClickInteraction p ? p.Buttons : PointerMasks.DefaultButtons,
                previous is ClickInteraction q ? q.Devices : PointerMasks.DefaultDevices, writeButtons: true);
    }
}
