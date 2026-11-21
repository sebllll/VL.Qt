using VL.Qt.Images;
using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>Draws a <see cref="QtImage"/> (image resource of QtNative).</summary>
    internal sealed class Image : Element
    {
        public Image() : base(ElementType.Image) { }
        internal Image(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.Image, common, feedback) { }

        /// <summary>The image to draw, null = nothing. Compared by reference; QtNative gets its resource id.</summary>
        public QtImage? Source { get; init; }
        public ImageFillMode FillMode { get; init; } = ImageFillMode.Stretch;
        /// <summary>Qt default: true.</summary>
        public bool Smooth { get; init; } = true;
    }
}
