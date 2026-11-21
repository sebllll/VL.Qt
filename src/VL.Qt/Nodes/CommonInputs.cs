using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Inputs every element node has (the common pins of <see cref="Element"/>). Value equality is used for
    /// change detection. Values are in units of the renderer's space; nothing is converted here (the reconciler does, with the SpaceMapping of the scene).
    /// A node that fixes some of them states them with <c>with</c>, e.g. <c>common with { Anchor = RectangleAnchor.TopLeft }</c>.
    /// </summary>
    internal readonly record struct CommonInputs
    {
        private readonly Matrix _transform;

        public CommonInputs(Vector2 size, Matrix transform, RectangleAnchor anchor, float opacity, bool clip, bool debug, Interaction? interaction,
            Effect? effect)
        {
            Size = size;
            Transform = transform;
            Anchor = anchor;
            Opacity = opacity;
            Clip = clip;
            Debug = debug;
            Interaction = interaction;
            Effect = effect;
        }

        public Vector2 Size { get; init; }

        /// <summary>Transform of the element; an all-zero matrix is stored as Identity (<see cref="NormalizeTransform"/>).</summary>
        public Matrix Transform { get => _transform; init => _transform = NormalizeTransform(value); }

        public RectangleAnchor Anchor { get; init; }
        public float Opacity { get; init; }
        public bool Clip { get; init; }
        public bool Debug { get; init; }

        /// <summary>Chain of the Interaction pin; compared by reference (an unchanged chain is the same instance).</summary>
        public Interaction? Interaction { get; init; }

        /// <summary>Chain of the Effect pin (plan effects/01); compared by reference like <see cref="Interaction"/>.</summary>
        public Effect? Effect { get; init; }

        /// <summary>
        /// An all-zero matrix (default(Matrix), e.g. a pin without a VL default) is treated as Identity: it would
        /// collapse the item to a point and is never produced by transform nodes (they always keep M44 = 1).
        /// </summary>
        public static Matrix NormalizeTransform(Matrix m) => m == default ? Matrix.Identity : m;
    }
}
