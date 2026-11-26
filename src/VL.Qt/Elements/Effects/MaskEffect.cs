namespace VL.Qt.Elements
{
    /// <summary>Mask link (MultiEffect maskSource, thresholds, spreads, maskInverted); holds the mask element.</summary>
    internal sealed class MaskEffect : Effect
    {
        public MaskEffect(Effect? previous, Element? source, float thresholdMin, float spreadAtMin, float thresholdMax,
            float spreadAtMax, bool inverted)
            : base(previous)
        {
            Source = source;
            ThresholdMin = thresholdMin;
            SpreadAtMin = spreadAtMin;
            ThresholdMax = thresholdMax;
            SpreadAtMax = spreadAtMax;
            Inverted = inverted;
        }

        /// <summary>The mask element; null = no mask (the link changes nothing).</summary>
        public Element? Source { get; }
        public float ThresholdMin { get; }
        public float SpreadAtMin { get; }
        public float ThresholdMax { get; }
        public float SpreadAtMax { get; }
        public bool Inverted { get; }

        internal override EffectKind Kind => EffectKind.Mask;

        internal override EffectDesc Apply(in EffectDesc desc)
            => Source is null ? desc : desc with
            {
                MaskSource = Source,
                MaskThresholdMin = ThresholdMin,
                MaskSpreadAtMin = SpreadAtMin,
                MaskThresholdMax = ThresholdMax,
                MaskSpreadAtMax = SpreadAtMax,
                MaskInverted = Inverted,
            };
    }
}
