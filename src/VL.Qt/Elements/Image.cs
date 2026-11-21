namespace VL.Qt.Elements
{
    /// <summary>Draws a <see cref="QtImage"/> (image resource of QtNative).</summary>
    public sealed class Image : Element
    {
        public Image() : base(ElementType.Image) { }

        /// <summary>The image to draw, null = nothing. Compared by reference; QtNative gets its resource id.</summary>
        public QtImage? Source { get; init; }
        public ImageFillMode FillMode { get; init; } = ImageFillMode.Stretch;
        /// <summary>Qt default: true.</summary>
        public bool Smooth { get; init; } = true;
    }
}
