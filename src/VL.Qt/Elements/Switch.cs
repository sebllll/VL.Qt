namespace VL.Qt.Elements
{
    /// <summary>
    /// Switch without text (QtQuick.Controls Switch, style Basic). The Toggle node with look Switch.
    /// User changes come from Qt's <c>toggled</c> as EVENT_VALUE (bool) into <see cref="Element.Feedback"/>; SET VALUE is never echoed.
    /// </summary>
    internal sealed class Switch : Element
    {
        public Switch() : base(ElementType.Switch) { }

        /// <summary>On (default): Size is ignored and the implicit size applies. Off: Size counts.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Checked state.</summary>
        public bool Value { get; init; }

        /// <summary>False greys the switch out; it then takes no input.</summary>
        public bool Enabled { get; init; } = true;
    }
}
