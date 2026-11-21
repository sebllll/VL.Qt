using System;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Nodes;
using VL.Qt.Styles;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Immutable description of one UI element. Create a new instance only when an input changes
    /// (the reconciler uses reference equality as a shortcut). Pins carry it as <see cref="IElement"/>.
    /// </summary>
    internal abstract class Element : IElement
    {
        private protected Element(ElementType type) => Type = type;

        /// <summary>Takes the common inputs of an element node and the feedback slot of the node (the element node constructor of every concrete element).</summary>
        private protected Element(ElementType type, in CommonInputs common, FeedbackSlot feedback)
        {
            Type = type;
            Size = common.Size;
            Transform = common.Transform;
            Anchor = common.Anchor;
            Opacity = common.Opacity;
            Clip = common.Clip;
            Debug = common.Debug;
            Visible = common.Enabled;
            Interaction = common.Interaction;
            Effect = common.Effect;
            Feedback = feedback;
        }

        internal ElementType Type { get; }
        /// <summary>Item size in units (containers/widgets: only with Auto Size off). Position comes from <see cref="Transform"/>.</summary>
        public Vector2 Size { get; init; }

        /// <summary>
        /// Placement and visual transform: a point v of the element lands at (v - anchor) · Transform in its parent
        /// (like VL.Skia). In a Row/Column the layout places the element and Transform acts around <see cref="Anchor"/>.
        /// </summary>
        public Matrix Transform { get; init; } = Matrix.Identity;

        /// <summary>Point of the element that lies at the origin of its parent space; pivot of <see cref="Transform"/>.</summary>
        public RectangleAnchor Anchor { get; init; } = RectangleAnchor.TopLeft;
        public float Opacity { get; init; } = 1;
        public bool Clip { get; init; }

        /// <summary>Draws the debug overlay for this element only (not recursive).</summary>
        public bool Debug { get; init; }

        /// <summary>
        /// The Enabled pin: false takes the element out of the scene (Qt's visible: not drawn, no input, no space in
        /// Row/Column). Named after the Qt property because the widgets already have an <c>Enabled</c> of their own (greyed out).
        /// </summary>
        public bool Visible { get; init; } = true;
        internal FeedbackSlot Feedback { get; init; }

        /// <summary>
        /// Chain of interaction descriptions (Hover, Click, …); each link becomes an input handler at the item.
        /// Null = no handlers, the element is transparent for input.
        /// </summary>
        public Interaction? Interaction { get; init; }

        /// <summary>
        /// Chain of effects (Blur, DropShadow, …, shader effects), drawn as layers on the element's own item (see <see cref="Elements.Effect"/>);
        /// Size, Transform and layout stay the element's. Null = no effect layer.
        /// </summary>
        public Effect? Effect { get; init; }

        /// <summary>
        /// Own style, only read at Group and at the widgets (Button … TextField); null = inherit everything.
        /// Group passes its resolved style on to its children, other containers pass on what they inherited.
        /// </summary>
        public Style? Style { get; init; }

        /// <summary>
        /// Child elements. Containers (Group, Row, Column) override this with a settable value;
        /// on all other elements setting a non-empty value throws.
        /// </summary>
        public virtual ImmutableArray<Element> Children
        {
            get => ImmutableArray<Element>.Empty;
            init
            {
                if (!value.IsDefaultOrEmpty)
                    throw new NotSupportedException($"{GetType().Name} has no children.");
            }
        }

        /// <summary>Maps default(ImmutableArray) to Empty, so Children is never default.</summary>
        protected static ImmutableArray<Element> Normalize(ImmutableArray<Element> value)
            => value.IsDefault ? ImmutableArray<Element>.Empty : value;
    }
}
