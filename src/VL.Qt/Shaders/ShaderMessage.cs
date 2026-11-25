namespace VL.Qt.Shaders
{
    /// <summary>
    /// A problem in a shader file (unknown tag, value that does not parse, skipped uniform, wrong head tag). Warning (orange) or
    /// error (red) is given by the list it is in.
    /// </summary>
    /// <param name="Line">1-based line in the source file, 0 if not tied to a line (e.g. a reflection fact).</param>
    /// <param name="Text">Message without the line prefix.</param>
    internal sealed record ShaderMessage(int Line, string Text)
    {
        public override string ToString() => Line > 0 ? $"line {Line}: {Text}" : Text;
    }
}
