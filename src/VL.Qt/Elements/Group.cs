using VL.Lib.Mathematics;
using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>Plain container (Qt Item). Draws nothing itself. With the default anchor TopLeft a pure transform container like the Skia group.</summary>
    internal sealed class Group : Container
    {
        public Group() : base(ElementType.Item) { }
        internal Group(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.Item, common, feedback) { }
    }
}
