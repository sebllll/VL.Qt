using System;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Input pin of a declared, not readonly property. Sending rule: the value is not sent while it equals
    /// <see cref="QmlPropertyDecl.Default"/> and was never different; once it differs it is sent, and from then on on every
    /// change (also back to the default). <see cref="AlwaysSend"/> (required property): sent from the first frame.
    /// </summary>
    /// <param name="Name">Pin name (unique on the node).</param>
    /// <param name="Property">The declaration.</param>
    /// <param name="EnumSlot">
    /// The pool type of the QML enum of the property (<see cref="QmlEnumPool"/>): its pin is a dropdown; null: the pin type of the property.
    /// </param>
    internal sealed record QmlInputPin(string Name, QmlPropertyDecl Property, QmlEnumSlot? EnumSlot = null)
    {
        /// <summary>CLR type of the pin: the pool type of the enum, else <see cref="QmlPropertyDecl.ClrType"/>.</summary>
        public Type PinType => EnumSlot?.Type ?? Property.ClrType;

        /// <summary>Default of the pin: the pool value of the declared int, else <see cref="QmlPropertyDecl.Default"/>.</summary>
        public object PinDefault => EnumSlot is { } slot ? slot.FromInt(Property.IntDefault) : Property.Default;

        /// <summary>True for a required property: always sent.</summary>
        public bool AlwaysSend => Property.IsRequired;
    }
}
