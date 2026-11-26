namespace VL.Qt.Qml
{
    /// <summary>Severity of a parser message; the node description turns them into VL messages (Info, Warning, Error).</summary>
    internal enum QmlMessageSeverity
    {
        /// <summary>
        /// A declaration that gets no pin (alias, var, list, object type, unsupported value type).
        /// All infos of a file are joined into one message on the node.
        /// </summary>
        Info,
        /// <summary>Something the parser skipped or guessed (e.g. an unknown head tag, a sibling file that cannot be read, a cycle of root types).</summary>
        Warning,
        /// <summary>The file does not parse (no root object, unbalanced braces): the node keeps only the common pins.</summary>
        Error,
    }
}
