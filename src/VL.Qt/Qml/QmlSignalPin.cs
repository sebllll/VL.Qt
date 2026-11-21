using System;

namespace VL.Qt.Qml
{
    /// <summary>Output pin of a declared signal, see <see cref="QmlTypes.SignalPinType"/>.</summary>
    /// <param name="Name">Pin name (unique on the node).</param>
    /// <param name="Signal">The declaration.</param>
    /// <param name="Type">CLR type of the pin: bool, Spread&lt;T&gt; or Spread&lt;QmlSignal&gt;.</param>
    internal sealed record QmlSignalPin(string Name, QmlSignalDecl Signal, Type Type)
    {
        /// <summary>Kind of the only argument of a <see cref="QmlSignalShape.Values"/> signal, null otherwise or if it has none.</summary>
        public QmlValueKind? ArgumentKind => Signal.Shape == QmlSignalShape.Values ? Signal.Args[0].Kind : null;
    }
}
