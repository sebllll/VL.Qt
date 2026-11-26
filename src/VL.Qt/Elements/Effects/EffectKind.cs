using System;

namespace VL.Qt.Elements
{
    /// <summary>Kinds of effect links (bit mask, for the duplicate check in a run of <see cref="Effect.Segment"/>).</summary>
    [Flags]
    internal enum EffectKind
    {
        None = 0,
        Blur = 1,
        DropShadow = 2,
        ColorAdjust = 4,
        Colorize = 8,
        Mask = 16,
    }
}
