using System.Collections.Immutable;
using VL.Lib.Mathematics;
using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>Plain container (Qt Item). Draws nothing itself. With the default anchor TopLeft a pure transform container like the Skia group.</summary>
    internal sealed class Group : Element
    {
        private readonly ImmutableArray<Element> children = ImmutableArray<Element>.Empty;

        public Group() : base(ElementType.Item) { }
        internal Group(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.Item, common, feedback) { }

        /// <summary>On (default): Size is ignored and the implicit size applies. Off: Size counts, also 0.</summary>
        public bool AutoSize { get; init; } = true;

        public override ImmutableArray<Element> Children { get => children; init => children = Normalize(value); }
    }
}
