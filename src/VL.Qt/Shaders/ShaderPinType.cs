namespace VL.Qt.Shaders
{
    /// <summary>Pin type of a uniform (docs/effects-and-shaders.md); all other GLSL types are skipped with a warning.</summary>
    internal enum ShaderPinType
    {
        /// <summary>float → float.</summary>
        Float,
        /// <summary>vec2 → Stride Vector2.</summary>
        Vector2,
        /// <summary>vec3 → Stride Vector3.</summary>
        Vector3,
        /// <summary>vec4 without @color → Stride Vector4.</summary>
        Vector4,
        /// <summary>vec4 with @color → Stride Color4.</summary>
        Color,
        /// <summary>int → int.</summary>
        Int,
        /// <summary>uint reflected, declared bool in the source (regex) → bool.</summary>
        Bool,
        /// <summary>mat4 → Stride Matrix.</summary>
        Matrix,
        /// <summary>sampler2D other than source → QtImage.</summary>
        Image,
        /// <summary>Sampler of the sampler2D named by UniformName (a hidden pin after its Image pin) → Sampler.</summary>
        Sampler,
    }
}
