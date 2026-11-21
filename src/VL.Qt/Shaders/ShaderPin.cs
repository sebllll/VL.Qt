namespace VL.Qt.Shaders
{
    /// <summary>One uniform pin of a generated shader node.</summary>
    /// <param name="Name">Pin name: camelCase uniform name split into words ("pixelSize" → "Pixel Size").</param>
    /// <param name="UniformName">Name in the shader (key of the Uniform sent to QtNative and of the annotations).</param>
    /// <param name="Type">Pin type.</param>
    /// <param name="Default">
    /// Default as the CLR value of the pin type: float, Stride.Core.Mathematics.Vector2/3/4, Color4, int, bool, Matrix;
    /// null for Image. Without @default: GLSL zero (Matrix: identity, the VL default), @color: white.
    /// </param>
    /// <param name="Min">IOBox lower limit in the CLR type of the pin, null if not annotated.</param>
    /// <param name="Max">IOBox upper limit in the CLR type of the pin, null if not annotated.</param>
    /// <param name="Summary">@summary as pin help, null if not given.</param>
    internal sealed record ShaderPin(
        string Name,
        string UniformName,
        ShaderPinType Type,
        object? Default,
        object? Min,
        object? Max,
        string? Summary);
}
