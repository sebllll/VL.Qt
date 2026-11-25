namespace VL.Qt.Shaders
{
    /// <summary>
    /// Annotations of one uniform (comment at the end of its line and/or the comment lines directly above it).
    /// Values are kept as the raw text after the tag; <see cref="ShaderPinModel"/> parses them against the reflected type,
    /// because only then the component count is known (a single number for @min/@max applies to all components).
    /// Null = tag not given.
    /// </summary>
    /// <param name="Default">Raw text of @default (comma separated components, true/false for bool), null if not given.</param>
    /// <param name="Min">Raw text of @min (IOBox limit only, no clamping), null if not given.</param>
    /// <param name="Max">Raw text of @max (IOBox limit only, no clamping), null if not given.</param>
    /// <param name="IsColor">@color given: vec4 becomes a Color4 pin (default white).</param>
    /// <param name="Summary">@summary incl. continuation lines without tag, null if not given.</param>
    /// <param name="Line">1-based line of the uniform declaration (for warnings of the pin model).</param>
    internal sealed record UniformAnnotation(string? Default, string? Min, string? Max, bool IsColor, string? Summary, int Line);
}
