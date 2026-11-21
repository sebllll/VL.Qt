using System;
using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Immutable chain of interaction descriptions (Hover, Click, Point, Wheel, …) for the <c>Interaction</c> pin of an element. Each
    /// interaction node adds one link and keeps a reference to the rest of the chain in <see cref="Previous"/>. One link
    /// becomes one Qt Quick input handler at the element's item (a protocol object of its own, see protocol.h).
    /// <para>
    /// Identity is the <see cref="Feedback"/> of the interaction node: the reconciler keys the handlers of an element by it.
    /// Create a new instance only when an input of the node changes; an unchanged chain is the same instance (the
    /// reconciler compares by reference). The order of the chain has no meaning (Qt decides by its grab rules).
    /// </para>
    /// </summary>
    public abstract class Interaction
    {
        private protected Interaction(ushort handlerType, Interaction? previous, FeedbackSlot feedback)
        {
            if (feedback.IsNone)
                throw new ArgumentException("An interaction needs feedback.", nameof(feedback));
            HandlerType = handlerType;
            Previous = previous;
            Feedback = feedback;
        }

        /// <summary>The rest of the chain (the interactions connected before this one), null at the start.</summary>
        public Interaction? Previous { get; }

        /// <summary>Receives the handler's events (one slot of the interaction node's feedback); key of the handler.</summary>
        public FeedbackSlot Feedback { get; }

        /// <summary>CREATE type of the handler (<see cref="Protocol.ElementType"/>, from 32).</summary>
        internal ushort HandlerType { get; }

        /// <summary>
        /// Writes the SETs of the handler properties that differ from <paramref name="previous"/> (same handler type;
        /// null = just created, send only values that differ from QtNative's defaults).
        /// <paramref name="mapping"/> converts lengths (Drag limits) to logical pixels; when its scale changes the reconciler
        /// passes <paramref name="previous"/> = null so every non-default value is sent again.
        /// </summary>
        internal abstract void WriteProperties(ICommandSink sink, uint id, Interaction? previous, in SpaceMapping mapping);
    }
}
