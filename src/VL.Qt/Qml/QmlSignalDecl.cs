using System.Collections.Immutable;

namespace VL.Qt.Qml
{
    /// <summary>
    /// A signal declared in the root object: <c>signal name</c>, <c>signal name()</c> or <c>signal name(type a, …)</c>
    /// (also the <c>a: type</c> form).
    /// </summary>
    /// <param name="Name">Name in QML.</param>
    /// <param name="Args">Arguments in declaration order.</param>
    /// <param name="Summary">The <c>//</c> comment lines directly above the declaration, null if none.</param>
    /// <param name="Line">1-based line of the declaration.</param>
    /// <param name="DeclaredIn">File name of the sibling root type that declares it, null for the file itself.</param>
    internal sealed record QmlSignalDecl(string Name, ImmutableArray<QmlSignalArg> Args, string? Summary, int Line, string? DeclaredIn = null)
    {
        /// <summary>Shape of its output pin.</summary>
        public QmlSignalShape Shape => QmlTypes.SignalShape(Args.Length);
    }
}
