using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>A link with Auto Padding / Padding pins; <see cref="Effect.Segment"/> merges them into the one effect area of their run.</summary>
    internal interface IEffectPadding
    {
        bool AutoPadding { get; }
        /// <summary>Extra area in DIP: x = left, y = top, width = right, height = bottom (used with AutoPadding off).</summary>
        RectangleF Padding { get; }
    }
}
