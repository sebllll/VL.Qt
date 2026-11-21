using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Single-line text input (QtQuick.Controls TextField, style Basic). User changes come from Qt's
    /// <c>editingFinished</c> (Enter or focus loss, only if the text changed) as EVENT_VALUE (string) into
    /// <see cref="Element.Feedback"/>; SET VALUE is never echoed.
    /// </summary>
    internal sealed class TextField : Element
    {
        public TextField() : base(ElementType.TextField) { }
        internal TextField(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.TextField, common, feedback) { }

        /// <summary>On (default): Size is ignored and the implicit size applies. Off: Size counts.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Current text (never null).</summary>
        public string Value { get; init; } = string.Empty;

        /// <summary>Text shown while the field is empty (never null).</summary>
        public string Placeholder { get; init; } = string.Empty;

        /// <summary>The text can be edited (false = read-only).</summary>
        public bool Editable { get; init; } = true;

        /// <summary>False greys the field out; it then takes no input.</summary>
        public bool Enabled { get; init; } = true;
    }
}