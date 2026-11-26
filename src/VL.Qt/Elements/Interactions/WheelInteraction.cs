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
    }
}
