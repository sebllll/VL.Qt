using System;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Output pin of a declared property: the last value QML changed (EVENT_QML_PROPERTY), <see cref="QmlPropertyDecl.Default"/>
    /// until the first change and again after a reload. Same name as its input pin (the pin kind tells them apart).
    /// </summary>
    /// <param name="Name">Pin name, the same as the input pin of the property.</param>
    /// <param name="Property">The declaration.</param>
    /// <param name="IsVisible">Visible for a readonly or binding-initialised property, else an optional pin (hidden until shown).</param>
    /// <param name="EnumSlot">The pool type of the QML enum of the property, as for <see cref="QmlInputPin"/>; null: the pin type of the property.</param>
    internal sealed record QmlOutputPin(string Name, QmlPropertyDecl Property, bool IsVisible, QmlEnumSlot? EnumSlot = null)
    {
        /// <summary>CLR type of the pin: the pool type of the enum, else <see cref="QmlPropertyDecl.ClrType"/>.</summary>
        public Type PinType => EnumSlot?.Type ?? Property.ClrType;

        /// <summary>Default of the pin: the pool value of the declared int, else <see cref="QmlPropertyDecl.Default"/>.</summary>
        public object PinDefault => EnumSlot is { } slot ? slot.FromInt(Property.IntDefault) : Property.Default;
    }
}
