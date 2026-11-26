using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>Colorize link (MultiEffect colorization, colorizationColor).</summary>
    internal sealed class ColorizeEffect : Effect
    {
        public ColorizeEffect(Effect? previous, Color4 color, float amount)
            : base(previous)
        {
            Color = color;
            Amount = amount;
        }

        public Color4 Color { get; }
        public float Amount { get; }

        internal override EffectKind Kind => EffectKind.Colorize;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { ColorizationColor = Color, Colorization = Amount };
    }
}
