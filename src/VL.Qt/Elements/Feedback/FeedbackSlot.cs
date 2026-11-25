namespace VL.Qt.Elements
{
    /// <summary>Reference from an element description to one slot of a feedback. Default = no feedback.</summary>
    internal readonly record struct FeedbackSlot(ElementFeedback? Feedback, int Index)
    {
        public bool IsNone => Feedback is null;

        /// <summary>State of the slot, or null for <c>default</c>.</summary>
        public FeedbackState? State => Feedback?[Index];

        /// <summary>
        /// Sets or (null) removes the message of <paramref name="source"/> in this slot (<see cref="FeedbackState.SetMessage"/>);
        /// nothing for <c>default</c>. Removing does not create a slot.
        /// </summary>
        public void SetMessage(object source, MessageKind kind, string? text)
        {
            if (Feedback is null)
                return;
            if (text is not null)
                Feedback[Index].SetMessage(source, kind, text);
            else if (Feedback.TryGet(Index, out var state))
                state.SetMessage(source, kind, null);
        }
    }
}
