using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>
    /// The folded effect chain: all values of one Qt MultiEffect (the Effect layer properties of protocol.h). Defaults
    /// are Qt's, everything off. Lengths in DIP.
    /// </summary>
    internal readonly record struct EffectDesc
    {
        public EffectDesc()
        {
        }

        /// <summary>All parts off, Qt defaults.</summary>
        public static readonly EffectDesc Default = new();

        public bool BlurEnabled { get; init; }
        /// <summary>0..1.</summary>
        public float Blur { get; init; }
        /// <summary>Maximum blur radius in DIP (Qt blurMax, default 32); also limits the shadow blur. Changing it switches shaders.</summary>
        public int BlurMax { get; init; } = 32;
        /// <summary>0..; values above 0 enlarge the blur beyond BlurMax with lower quality (Qt default 0).</summary>
        public float BlurMultiplier { get; init; }

        public bool ShadowEnabled { get; init; }
        public Color4 ShadowColor { get; init; } = Color4.Black;
        /// <summary>Shadow offset in DIP.</summary>
        public Vector2 ShadowOffset { get; init; }
        /// <summary>0..1 (Qt default 1).</summary>
        public float ShadowBlur { get; init; } = 1;
        /// <summary>Scale of the shadow around the item's center (Qt default 1).</summary>
        public float ShadowScale { get; init; } = 1;

        /// <summary>-1..1.</summary>
        public float Brightness { get; init; }
        /// <summary>-1..1.</summary>
        public float Contrast { get; init; }
        /// <summary>-1..1.</summary>
        public float Saturation { get; init; }

        /// <summary>0..1, strength of the colorization (0 = off).</summary>
        public float Colorization { get; init; }
        /// <summary>Colorization color (Qt default red).</summary>
        public Color4 ColorizationColor { get; init; } = new Color4(1, 0, 0, 1);

        /// <summary>On (Qt default): the effect area grows by what blur and shadow need. Off: <see cref="PaddingRect"/> applies.</summary>
        public bool AutoPadding { get; init; } = true;
        /// <summary>Extra effect area in DIP with AutoPadding off: x = left, y = top, width = right, height = bottom.</summary>
        public RectangleF PaddingRect { get; init; }

        /// <summary>Element used as alpha mask (its own description, reconciled as hidden texture source); null = no mask.</summary>
        public Element? MaskSource { get; init; }
        /// <summary>0..1, mask alpha below this is fully masked (Qt default 0).</summary>
        public float MaskThresholdMin { get; init; }
        /// <summary>0..1, softness at the lower threshold (Qt default 0).</summary>
        public float MaskSpreadAtMin { get; init; }
        /// <summary>0..1, mask alpha above this is fully masked (Qt default 1).</summary>
        public float MaskThresholdMax { get; init; } = 1;
        /// <summary>0..1, softness at the upper threshold (Qt default 0).</summary>
        public float MaskSpreadAtMax { get; init; }
        /// <summary>Inverts the mask.</summary>
        public bool MaskInverted { get; init; }
    }
}
