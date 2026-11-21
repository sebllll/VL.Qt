namespace VL.Qt.Qml
{
    /// <summary>An argument of a declared signal.</summary>
    /// <param name="Name">Name of the argument.</param>
    /// <param name="QmlType">Type as written ("int", "var", …).</param>
    /// <param name="Kind">Value kind, null for a type without one (then the pin carries object).</param>
    internal sealed record QmlSignalArg(string Name, string QmlType, QmlValueKind? Kind);
}
