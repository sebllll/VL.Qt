using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>A link with Auto Padding / Padding pins; <see cref="Effect.Fold"/> merges them into the one effect area.</summary>
    internal interface IManualPadding
    {
        bool AutoPadding { get; }
        /// <summary>Extra area in units: x = left, y = top, width = right, height = bottom (used with AutoPadding off).</summary>
        RectangleF Padding { get; }
    }
}
