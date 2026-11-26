namespace VL.Qt.Elements
{
    /// <summary>Color adjustment link (MultiEffect brightness, contrast, saturation).</summary>
    internal sealed class ColorAdjustEffect : Effect
    {
        public ColorAdjustEffect(Effect? previous, float brightness, float contrast, float saturation)
            : base(previous)
        {
            Brightness = brightness;
            Contrast = contrast;
            Saturation = saturation;
        }

        public float Brightness { get; }
        public float Contrast { get; }
        public float Saturation { get; }

        internal override EffectKind Kind => EffectKind.ColorAdjust;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { Brightness = Brightness, Contrast = Contrast, Saturation = Saturation };
    }
}
