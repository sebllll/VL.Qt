using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>Stacks its children vertically. Only the size of a child is used, Transform acts around the child's Anchor. Lengths in units.</summary>
    internal sealed class Column : Container
    {
        public Column() : base(ElementType.Column) { }
        internal Column(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.Column, common, feedback) { }

        public float Spacing { get; init; }
        /// <summary>Uniform padding on all sides.</summary>
        public float Padding { get; init; }
        /// <summary>The horizontal alignment of the children.</summary>
        public HorizontalAlignment ChildAlignment { get; init; } = HorizontalAlignment.Left;
    }
}
