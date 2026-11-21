namespace VL.Qt.Elements
{
    /// <summary>
    /// Loads a user .qml file into the element tree (protocol.h "QmlElement"). The root of the file must be an Item. The
    /// patch sets properties of the root by name (<see cref="Properties"/>), QtNative reports the property changes and the
    /// signals of the root back into <see cref="Element.Feedback"/>.
    /// </summary>
    internal sealed class QmlElement : Element
    {
        public QmlElement() : base(ElementType.Qml) { }

        /// <summary>On (default): the element takes the implicit size of the QML root. Off: Size sets width and height of the root.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Absolute path of the .qml file, empty = nothing loaded.</summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>
        /// Counter of the node's hot reload: a new value reloads the same file (QtNative clears its component cache) and sends all
        /// properties again.
        /// </summary>
        internal int Reload { get; init; }

        /// <summary>Values for properties of the QML root by name; the reconciler sends the entries that changed (<see cref="Reconciliation.QmlPropertyWriter"/>).</summary>
        internal QmlProperties Properties { get; init; } = QmlProperties.Empty;
    }
}
