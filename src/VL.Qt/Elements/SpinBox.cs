namespace VL.Qt.Elements
{
    /// <summary>
    /// Integer input (QtQuick.Controls SpinBox, style Basic, #210). User changes come from Qt's <c>valueModified</c>
    /// as EV_VALUE into <see cref="Element.Sink"/>; SET VALUE is never echoed. Defaults match QtNative (full int range,
    /// not Qt's 0–99). Lengths in units.
    /// </summary>
    public sealed class SpinBox : Element
    {
        public SpinBox() : base(ElementType.SpinBox) { }

        /// <summary>On (default): Size is ignored and the implicit size applies. Off: Size counts.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Current value. Qt clamps it to Minimum…Maximum without an event, so it is kept clamped here too.</summary>
        public int Value { get; init; }

        public int Minimum { get; init; } = int.MinValue;

        public int Maximum { get; init; } = int.MaxValue;

        /// <summary>Step of +/-, arrow keys and wheel.</summary>
        public int Step { get; init; } = 1;

        /// <summary>The value can be typed (sent on Enter or focus loss).</summary>
        public bool Editable { get; init; } = true;

        /// <summary>False greys the input out; it then takes no input.</summary>
        public bool Enabled { get; init; } = true;
    }
}
