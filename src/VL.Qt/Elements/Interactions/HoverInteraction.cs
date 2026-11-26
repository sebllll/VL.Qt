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
    }
}
