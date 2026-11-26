using VL.Qt.Styles;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Placeholder for an <see cref="IStylable"/> wherever an element goes (children, Renderer, effect inputs …), one per
    /// stylable (<see cref="ElementConversion"/>). Never becomes a Qt object: the reconciler calls
    /// <see cref="IStylable.Build"/> with the style resolved at this position and reconciles the returned element in its place.
    /// </summary>
    internal sealed class StylableElement : Element
    {
        private Group? _empty;

        public StylableElement(IStylable stylable)
            : base(ElementType.Item)
        {
            Stylable = stylable;
        }

        public IStylable Stylable { get; }

        /// <summary>
        /// Element of the last Build at any position (null before the first); Snapshot and TooltipPreview locate it in the
        /// scene. Main thread only.
        /// </summary>
        public Element? Built { get; set; }

        /// <summary>Shown while Build returns null or has never succeeded (one instance per placeholder, created on first use).</summary>
        public Group Empty => _empty ??= new Group();
    }
}
