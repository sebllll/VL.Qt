using System.Collections.Immutable;
using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>An element that holds child elements: <see cref="Group"/>, <see cref="Row"/> and <see cref="Column"/>.</summary>
    internal abstract class Container : Element
    {
        private ImmutableArray<Element> _children = ImmutableArray<Element>.Empty;

        private protected Container(ElementType type) : base(type) { }
        private protected Container(ElementType type, in CommonInputs common, FeedbackSlot feedback) : base(type, common, feedback) { }

        /// <summary>On (default): Size is ignored and the implicit size applies. Off: Size counts, also 0.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>The child elements; default(ImmutableArray) is mapped to empty, so this is never default.</summary>
        public ImmutableArray<Element> Children
        {
            get => _children;
            init => _children = value.IsDefault ? ImmutableArray<Element>.Empty : value;
        }

        internal override ImmutableArray<Element> ChildItems => _children;
    }
}
