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

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
            => PointerProps.Write(sink, id, Buttons, Devices, previous is PointInteraction p ? p.Buttons : PointerMasks.DefaultButtons,
                previous is PointInteraction q ? q.Devices : PointerMasks.DefaultDevices, writeButtons: true);
    }
}
