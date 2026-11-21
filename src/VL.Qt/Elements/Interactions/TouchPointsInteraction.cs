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

        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
            var p = previous as TouchPointsInteraction;
            if (Occlusion != (p?.Occlusion ?? true))
                sink.SetBool(id, Protocol.PropertyId.Occlusion, Occlusion);
            PointerProps.Write(sink, id, PointerMasks.DefaultButtons, Devices, PointerMasks.DefaultButtons,
                p?.Devices ?? PointerMasks.DefaultDevices, writeButtons: false);
        }
    }
}
