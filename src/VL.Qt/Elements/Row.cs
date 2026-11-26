using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>Stacks its children horizontally. Only the size of a child is used, Transform acts around the child's Anchor. Lengths in units.</summary>
    internal sealed class Row : Container
    {
        public Row() : base(ElementType.Row) { }
        internal Row(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.Row, common, feedback) { }

        public float Spacing { get; init; }
        /// <summary>Uniform padding on all sides.</summary>
        public float Padding { get; init; }
        /// <summary>The vertical alignment of the children.</summary>
        public VerticalAlignment ChildAlignment { get; init; } = VerticalAlignment.Top;
    }
}
