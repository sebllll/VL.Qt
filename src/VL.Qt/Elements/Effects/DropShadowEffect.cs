using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>Drop shadow link (MultiEffect shadow*).</summary>
    internal sealed class DropShadowEffect : Effect, IManualPadding
    {
        public DropShadowEffect(Effect? previous, Color4 color, Vector2 offset, float blur, float scale, bool autoPadding = true, RectangleF padding = default)
            : base(previous)
        {
            Color = color;
            Offset = offset;
            Blur = blur;
            Scale = scale;
            AutoPadding = autoPadding;
            Padding = padding;
        }

        public bool AutoPadding { get; }
        public RectangleF Padding { get; }

        public Color4 Color { get; }
        /// <summary>In units.</summary>
        public Vector2 Offset { get; }
        public float Blur { get; }
        public float Scale { get; }

        internal override EffectKind Kind => EffectKind.DropShadow;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { ShadowEnabled = true, ShadowColor = Color, ShadowOffset = Offset, ShadowBlur = Blur, ShadowScale = Scale };
    }
}
