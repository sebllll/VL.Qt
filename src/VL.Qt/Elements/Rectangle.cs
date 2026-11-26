using Stride.Core.Mathematics;
using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>Filled rectangle with optional rounded corners and stroke (Qt Rectangle). Lengths in units.</summary>
    internal sealed class Rectangle : Element
    {
        public Rectangle() : base(ElementType.Rectangle) { }
        internal Rectangle(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.Rectangle, common, feedback) { }

        /// <summary>Qt default: white.</summary>
        public Color4 Fill { get; init; } = Color4.White;
        public float Radius { get; init; }
        /// <summary>Qt default border.color: black.</summary>
        public Color4 StrokeColor { get; init; } = Color4.Black;
        /// <summary>0 = no stroke (Qt draws no border unless border is set).</summary>
        public float StrokeWidth { get; init; }
    }
}
