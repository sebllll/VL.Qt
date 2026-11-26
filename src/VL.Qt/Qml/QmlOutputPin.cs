namespace VL.Qt.Qml
{
    /// <summary>
    /// Output pin of a declared property: the last value QML changed (EVENT_QML_PROPERTY), <see cref="QmlPropertyDecl.Default"/>
    /// until the first change and again after a reload. Same name as its input pin (the pin kind tells them apart).
    /// </summary>
    /// <param name="Name">Pin name, the same as the input pin of the property.</param>
    /// <param name="Property">The declaration.</param>
    /// <param name="IsVisible">Visible for a readonly or binding-initialised property, else an optional pin (hidden until shown).</param>
    internal sealed record QmlOutputPin(string Name, QmlPropertyDecl Property, bool IsVisible);
}
