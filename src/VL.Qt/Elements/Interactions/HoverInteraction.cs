using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>Hover handler (Qt Quick HoverHandler): reports hover only; positions come from the Point handler.</summary>
    internal sealed class HoverInteraction : Interaction
    {
        public HoverInteraction(Interaction? previous, FeedbackSlot feedback)
            : base(Protocol.ElementType.HoverHandler, previous, feedback)
        {
        }

        /// <summary>No properties: QtNative defaults are all a Hover handler has.</summary>
        internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping)
        {
        }
    }
}
