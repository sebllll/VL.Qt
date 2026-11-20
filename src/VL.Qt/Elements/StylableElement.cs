using VL.Qt.Styles;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Placeholder for an <see cref="IStylable"/> in a children array (#413), created by the children input of the
    /// container nodes. Never becomes a Qt object: the reconciler calls <see cref="IStylable.Build"/> with the style
    /// resolved at this position and reconciles the returned element in its place.
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

        /// <summary>Shown while Build returns null or has never succeeded (one instance per placeholder, created on first use).</summary>
        public Group Empty => _empty ??= new Group();
    }
}
