namespace VL.Qt.Qml
{
    /// <summary>A message of the parser.</summary>
    /// <param name="Line">1-based line in the file the message is about, 0 if not tied to a line. An info is shown without it.</param>
    /// <param name="Severity">Severity.</param>
    /// <param name="Text">Message without the line prefix.</param>
    /// <param name="File">File name of a sibling the message comes from (declarations of a sibling root type), null for the file itself.</param>
    internal sealed record QmlParseMessage(int Line, QmlMessageSeverity Severity, string Text, string? File = null)
    {
        public override string ToString()
        {
            var line = Severity == QmlMessageSeverity.Info ? 0 : Line;
            var where = File is null ? (line > 0 ? $"line {line}" : null) : (line > 0 ? $"{File}:{line}" : File);
            return where is null ? Text : $"{where}: {Text}";
        }
    }
}
