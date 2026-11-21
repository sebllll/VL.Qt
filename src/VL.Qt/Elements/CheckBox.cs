namespace VL.Qt.Elements
{
    /// <summary>
    /// Check box without text (QtQuick.Controls CheckBox, style Basic). The Toggle node with look Checkbox.
    /// User changes come from Qt's <c>toggled</c> as EVENT_VALUE (bool) into <see cref="Element.Feedback"/>; SET VALUE is never echoed.
    /// </summary>
    internal sealed class CheckBox : Element
    {
        public CheckBox() : base(ElementType.CheckBox) { }

        /// <summary>On (default): Size is ignored and the implicit size applies. Off: Size counts.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Checked state.</summary>
        public bool Value { get; init; }

        /// <summary>False greys the box out; it then takes no input.</summary>
        public bool Enabled { get; init; } = true;
    }
}
