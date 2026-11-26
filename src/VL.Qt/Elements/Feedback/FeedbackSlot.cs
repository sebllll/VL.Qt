using System;
using System.Diagnostics.CodeAnalysis;

namespace VL.Qt.Elements
{
    /// <summary>Reference from an element description to one slot of a feedback. Default = no feedback.</summary>
    internal readonly record struct FeedbackSlot(ElementFeedback? Feedback, int Index)
    {
        public bool IsNone => Feedback is null;

        /// <summary>Returns the state of the slot if it exists (false for <c>default</c>), without allocating it.</summary>
        public bool TryGetState([NotNullWhen(true)] out FeedbackState? state)
        {
            if (Feedback is null)
            {
                state = null;
                return false;
            }
            return Feedback.TryGet(Index, out state);
        }

        /// <summary>
        /// Returns the state of the slot and allocates it (with the slots before it) if it does not exist yet. For a writer
        /// that has something to show; throws for <c>default</c>.
        /// </summary>
        public FeedbackState GetOrAddState()
            => (Feedback ?? throw new InvalidOperationException("The slot has no feedback."))[Index];

        /// <summary>
        /// Sets or (null) removes the message of <paramref name="source"/> in this slot (<see cref="FeedbackState.SetMessage"/>);
        /// nothing for <c>default</c>. Removing does not create a slot.
        /// </summary>
        public void SetMessage(object source, MessageKind kind, string? text)
        {
            if (IsNone)
                return;
            if (text is not null)
                GetOrAddState().SetMessage(source, kind, text);
            else if (TryGetState(out var state))
                state.SetMessage(source, kind, null);
        }
    }
}
