using System.Collections.Generic;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Mirror of one Qt item as the reconciler last sent it: its id, type, the element it was last updated from,
    /// its children in order, its binding, and the state of its Effect pin, Shader element, handlers and styles.
    /// </summary>
    internal sealed class ItemMirror
    {
        public ItemMirror(uint id, ElementType type)
        {
            Id = id;
            Type = type;
        }

        // Identity

        /// <summary>
        /// Object id in QtNative; 0 (the window root id) marks a placeholder (a video shown elsewhere), which has no Qt
        /// object, no children and no binding.
        /// </summary>
        public uint Id { get; }

        /// <summary>Placeholder for a video that is shown elsewhere (<see cref="OccurrenceRegistry"/>).</summary>
        public bool IsPlaceholder => Id == 0;

        /// <summary>CREATE type; a different type at this position means destroy + create.</summary>
        public ElementType Type { get; }

        /// <summary>Parent mirror (null under the window root). Mirrors never change their parent.</summary>
        public ItemMirror? Parent { get; init; }

        /// <summary>Child mirrors in reconciled order (index = REPARENT index).</summary>
        public List<ItemMirror> Children { get; } = new();

        /// <summary>Element this mirror was last reconciled from; null right after CREATE.</summary>
        public Element? Element { get; set; }

        // Binding

        /// <summary>Feedback slot bound via BIND; <see cref="FeedbackSlot.IsNone"/> = unbound.</summary>
        public FeedbackSlot BoundSlot { get; set; }

        /// <summary>Feedback id sent with BIND (<see cref="FeedbackRouter"/>); 0 = unbound.</summary>
        public uint BoundFeedbackId { get; set; }

        /// <summary>
        /// This mirror is the primary occurrence of its element (first in traversal order of the owner renderer,
        /// see <see cref="OccurrenceRegistry"/>): only its feedback id reports state values. Written only by
        /// <see cref="PrimaryOccurrences"/>, which keeps the flag of the feedback id (<see cref="FeedbackRouter.IsPrimary"/>) in step.
        /// </summary>
        public bool IsPrimary { get; set; }

        // Traversal

        /// <summary>Update pass in which this mirror was last reconciled (not skipped); see <see cref="PassInfo.Pass"/>.</summary>
        public int VisitedPass { get; set; }

        /// <summary>Update pass in which this mirror's subtree was last skipped (reference-equal).</summary>
        public int SkippedPass { get; set; }

        /// <summary>
        /// Set when a video shown inside this subtree was taken over by an earlier occurrence: the
        /// subtree must be visited next time (not skipped), so the old owner turns into a placeholder.
        /// </summary>
        public bool MustVisit { get; set; }

        // Effect pin, Shader element, handlers

        /// <summary>State of the Effect pin; null until the element first has an effect chain.</summary>
        public EffectChainMirror? Effect { get; set; }

        /// <summary>State of a Shader element; null for other elements and until the first visit.</summary>
        public ShaderMirror? Shader { get; set; }

        /// <summary>
        /// Input handlers attached to this item, one per link of the element's interaction chain; not part of
        /// <see cref="Children"/> (layouts never see them). Null until the element first has an interaction.
        /// </summary>
        public List<HandlerMirror>? Handlers { get; set; }

        // Styles

        /// <summary>Resolved style of the parent chain this mirror was last reconciled with (reference; part of the skip test).</summary>
        public Style? InheritedStyle { get; set; }

        /// <summary>Resolved style of this mirror (Group, widgets): kept as the same reference while it stays equal.</summary>
        public Style? ResolvedStyle { get; set; }

        /// <summary>Widgets: resolved style QtNative holds (last SET_STYLE); null = nothing sent.</summary>
        public Style? SentStyle { get; set; }

        // Self-made widget

        /// <summary>
        /// Position holds an <see cref="IStylable"/>: its placeholder this mirror was reconciled from; null for
        /// plain elements. <see cref="Element"/> is then the element its Build returned.
        /// </summary>
        public StylableElement? Stylable { get; set; }

        /// <summary>Error text of the last failed Build at this position, shown at the parent's feedback; null if none.</summary>
        public string? BuildError { get; set; }

        /// <summary>
        /// This subtree contains an <see cref="IStylable"/>, among its children or among the objects of its effect chain
        /// (mask source, uniform texture): never skipped, so every Build runs once per pass.
        /// </summary>
        public bool ContainsStylable { get; set; }

        /// <summary>This position is an <see cref="IStylable"/> or contains one: the parent must not be skipped.</summary>
        public bool HoldsStylable => Stylable is not null || ContainsStylable;
    }
}
