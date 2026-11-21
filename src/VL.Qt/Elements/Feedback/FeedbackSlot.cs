namespace VL.Qt.Elements
{
    /// <summary>Reference from an element description to one slot of a feedback. Default = no feedback.</summary>
    internal readonly record struct FeedbackSlot(ElementFeedback? Feedback, int Index)
    {
        public bool IsNone => Feedback is null;

        /// <summary>State of the slot, or null for <c>default</c>.</summary>
        public FeedbackState? State => Feedback?[Index];
    }
}
