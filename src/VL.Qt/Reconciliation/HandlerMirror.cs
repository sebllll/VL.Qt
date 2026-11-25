using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Mirror of one input handler object as the reconciler last sent it: id, type, key (the feedback of the
    /// interaction node), the description it was last updated from and its feedback id. Id 0 marks a placeholder (the
    /// interaction is active at another handler, see <see cref="InteractionRegistry"/>).
    /// </summary>
    internal sealed class HandlerMirror
    {
        public HandlerMirror(uint id, ushort type, FeedbackSlot key)
        {
            Id = id;
            Type = type;
            Key = key;
        }

        /// <summary>Handler id in QtNative (same id space as the items); 0 = placeholder without a QtNative object.</summary>
        public uint Id { get; }

        /// <summary>Placeholder for an interaction that is active at another handler.</summary>
        public bool IsPlaceholder => Id == 0;

        /// <summary>CREATE type (<see cref="Protocol.ElementType"/>, from 32).</summary>
        public ushort Type { get; }

        /// <summary>Feedback of the interaction node; identifies the handler within the element's chain.</summary>
        public FeedbackSlot Key { get; }

        /// <summary>Description this handler was last updated from.</summary>
        public Interaction? Last { get; set; }

        /// <summary>Feedback id sent with BIND (<see cref="FeedbackRouter"/>); 0 = unbound or placeholder.</summary>
        public uint BoundFeedbackId { get; set; }

        /// <summary>Update pass in which the chain last contained this handler (see <see cref="HandlerReconciler"/>).</summary>
        public int SeenPass { get; set; }
    }
}
