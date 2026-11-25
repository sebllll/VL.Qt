using System;
using System.Runtime.CompilerServices;
using VL.Qt.Elements;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// What the <see cref="OccurrenceRegistry"/> counts as one element: the feedback slot of the element when it has one,
    /// the instance otherwise (docs/design/reconciler-identity.md, "Element key"). An element node outputs a new instance
    /// whenever an input changes, always with the same slot, so all instances of one node are one element.
    /// </summary>
    internal readonly struct OccurrenceKey : IEquatable<OccurrenceKey>
    {
        private readonly object _target; // ElementFeedback, or the Element when it has no slot
        private readonly int _index;     // slot index; -1 for an instance key

        private OccurrenceKey(object target, int index)
        {
            _target = target;
            _index = index;
        }

        /// <summary>Key of <paramref name="element"/>: its feedback slot, or the instance if it has none.</summary>
        public static OccurrenceKey Of(Element element)
            => element.Feedback is { Feedback: { } feedback, Index: var index } ? new(feedback, index) : new(element, -1);

        public static implicit operator OccurrenceKey(Element element) => Of(element);

        /// <summary>The feedback slot of the key; <c>default</c> for an element without one.</summary>
        public FeedbackSlot Slot => _target is ElementFeedback feedback ? new FeedbackSlot(feedback, _index) : default;

        public bool Equals(OccurrenceKey other) => ReferenceEquals(_target, other._target) && _index == other._index;

        public override bool Equals(object? obj) => obj is OccurrenceKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_target), _index);

        public static bool operator ==(OccurrenceKey a, OccurrenceKey b) => a.Equals(b);

        public static bool operator !=(OccurrenceKey a, OccurrenceKey b) => !a.Equals(b);
    }
}
