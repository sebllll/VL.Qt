using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>Blur link (MultiEffect blur, blurMax, blurMultiplier).</summary>
    internal sealed class BlurEffect : Effect, IManualPadding
    {
        public BlurEffect(Effect? previous, float amount, int max, float multiplier, bool autoPadding = true, RectangleF padding = default)
            : base(previous)
        {
            Amount = amount;
            Max = max;
            Multiplier = multiplier;
            AutoPadding = autoPadding;
            Padding = padding;
        }

        public bool AutoPadding { get; }
        public RectangleF Padding { get; }

        public float Amount { get; }
        public int Max { get; }
        public float Multiplier { get; }

        internal override EffectKind Kind => EffectKind.Blur;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { BlurEnabled = true, Blur = Amount, BlurMax = Max, BlurMultiplier = Multiplier };
    }
}
