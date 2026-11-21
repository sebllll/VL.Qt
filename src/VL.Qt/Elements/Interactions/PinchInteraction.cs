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

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
            var p = previous as PinchInteraction;
            if (MinimumPointCount != (p?.MinimumPointCount ?? DefaultPointCount))
                sink.SetI32(id, Protocol.PropertyId.MinimumPointCount, MinimumPointCount);
            if (MaximumPointCount != (p?.MaximumPointCount ?? DefaultPointCount))
                sink.SetI32(id, Protocol.PropertyId.MaximumPointCount, MaximumPointCount);
            PointerProps.Write(sink, id, PointerMasks.DefaultButtons, Devices, PointerMasks.DefaultButtons,
                p?.Devices ?? PointerMasks.DefaultDevices, writeButtons: false);
        }
    }
}
