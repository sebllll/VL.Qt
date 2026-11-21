namespace VL.Qt.Elements
{
    /// <summary>
    /// Slider (QtQuick.Controls Slider, style Basic). User changes come from Qt's <c>moved</c> as EVENT_VALUE (f32)
    /// into <see cref="Element.Feedback"/>; SET VALUE is never echoed. Defaults match QtNative (0…1, continuous).
    /// </summary>
    internal sealed class Slider : Element
    {
        public Slider() : base(ElementType.Slider) { }

        /// <summary>On (default): Size is ignored and the implicit size applies. Off: Size counts.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Current value. Qt clamps it to Minimum…Maximum without an event, so it is kept clamped here too.</summary>
        public float Value { get; init; }

        public float Minimum { get; init; }

        public float Maximum { get; init; } = 1;

        /// <summary>Step size; 0 = continuous.</summary>
        public float Step { get; init; }

        public SliderOrientation Orientation { get; init; } = SliderOrientation.Horizontal;

        /// <summary>False greys the slider out; it then takes no input.</summary>
        public bool Enabled { get; init; } = true;
    }
}
