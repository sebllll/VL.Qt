namespace VL.Qt.Elements
{
    /// <summary>
    /// Push button (QtQuick.Controls Button, style Basic, #213). Clicks come from Qt's <c>clicked</c> signal into
    /// <see cref="Element.Sink"/> (release over the button; AutoRepeat repeats while held).
    /// Lengths in units.
    /// </summary>
    public sealed class Button : Element
    {
        public Button() : base(ElementType.Button) { }

        /// <summary>On (default): Size is ignored and the implicit size applies (text + padding of the style). Off: Size counts.</summary>
        public bool AutoSize { get; init; } = true;

        public string Text { get; init; } = string.Empty;

        /// <summary>False greys the button out; it then sends no clicks.</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>Repeats clicked while the button is held down.</summary>
        public bool AutoRepeat { get; init; }
    }
}
