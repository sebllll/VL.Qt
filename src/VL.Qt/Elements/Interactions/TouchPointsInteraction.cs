using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>Touch Points handler (own QtNative handler, passive grabs): all points pressed in the item.</summary>
    internal sealed class TouchPointsInteraction : Interaction
    {
        public TouchPointsInteraction(Interaction? previous, FeedbackSlot feedback, bool occlusion, PointerDevices devices)
            : base(Protocol.ElementType.TouchPointsHandler, previous, feedback)
        {
            Occlusion = occlusion;
            Devices = devices;
        }

        public bool Occlusion { get; }
        public PointerDevices Devices { get; }
    }
}
