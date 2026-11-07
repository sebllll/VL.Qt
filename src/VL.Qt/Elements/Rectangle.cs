using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>Filled rectangle with optional rounded corners and stroke (Qt Rectangle). Lengths in units.</summary>
    public sealed class Rectangle : Element
    {
        public Rectangle() : base(ElementType.Rectangle) { }

        /// <summary>Qt default: white.</summary>
        public Color4 Fill { get; init; } = Color4.White;
        public float Radius { get; init; }
        /// <summary>Qt default border.color: black.</summary>
        public Color4 StrokeColor { get; init; } = Color4.Black;
        /// <summary>0 = no stroke (Qt draws no border unless border is set).</summary>
        public float StrokeWidth { get; init; }
    }
}
