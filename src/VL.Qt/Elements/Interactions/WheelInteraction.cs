using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>Wheel handler (Qt Quick WheelHandler, target null): wheel and trackpad scroll in notches.</summary>
    internal sealed class WheelInteraction : Interaction
    {
        public WheelInteraction(Interaction? previous, FeedbackSlot feedback, PointerDevices devices)
            : base(Protocol.ElementType.WheelHandler, previous, feedback)
            => Devices = devices;

        public PointerDevices Devices { get; }

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
            => PointerProps.Write(sink, id, PointerMasks.DefaultButtons, Devices, PointerMasks.DefaultButtons,
                previous is WheelInteraction p ? p.Devices : PointerMasks.DefaultDevices, writeButtons: false);
    }
}
