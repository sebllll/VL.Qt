using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Pinch handler (Qt Quick PinchHandler, target null): scale, rotation and translation of the running gesture.
    /// The node validates the point counts (both at least 2, minimum not above maximum) before.
    /// </summary>
    internal sealed class PinchInteraction : Interaction
    {
        internal const int DefaultPointCount = 2;

        public PinchInteraction(Interaction? previous, FeedbackSlot feedback, int minimumPointCount, int maximumPointCount, PointerDevices devices)
            : base(Protocol.ElementType.PinchHandler, previous, feedback)
        {
            MinimumPointCount = minimumPointCount;
            MaximumPointCount = maximumPointCount;
            Devices = devices;
        }

        public int MinimumPointCount { get; }
        public int MaximumPointCount { get; }
        public PointerDevices Devices { get; }
    }
}
