namespace VL.Qt.Qml
{
    /// <summary>
    /// Input pin of a declared, not readonly property. Sending rule: the value is not sent while it equals
    /// <see cref="QmlPropertyDecl.Default"/> and was never different; once it differs it is sent, and from then on on every
    /// change (also back to the default). <see cref="AlwaysSend"/> (required property): sent from the first frame.
    /// </summary>
    /// <param name="Name">Pin name (unique on the node).</param>
    /// <param name="Property">The declaration.</param>
    internal sealed record QmlInputPin(string Name, QmlPropertyDecl Property)
    {
        /// <summary>True for a required property: always sent.</summary>
        public bool AlwaysSend => Property.IsRequired;
    }
}
