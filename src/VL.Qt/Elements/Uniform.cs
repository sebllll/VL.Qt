using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>
    /// One named shader input of a ShaderEffect: a member of the shader's uniform block or a sampler2D. Created by the
    /// nodes of category Qt.Elements.Uniform (Float, Int, Bool, Vector2/3/4, Color, Matrix, Texture, Image).
    /// The name must match the shader (case sensitive); a wrong name or type shows as warning at the ShaderEffect.
    /// </summary>
    public sealed record Uniform
    {
        private Uniform(string name, ushort valueType)
        {
            Name = name ?? string.Empty;
            ValueType = valueType;
        }

        /// <summary>Name of the uniform block member or sampler in the shader.</summary>
        public string Name { get; }

        /// <summary>Wire value type of SET_UNIFORMS (<see cref="Protocol.Vt"/>): F32, I32, Bool, Vec2/3/4, Color, Mat4, Ref (element texture), U64 (image).</summary>
        internal ushort ValueType { get; }

        /// <summary>Float, vector, color and matrix components (Float: M11; Vector2/3/4 and Color: M11..M14; Matrix: all).</summary>
        internal Matrix Numbers { get; private init; }

        /// <summary>Int and Bool (0/1).</summary>
        internal int Integer { get; private init; }

        /// <summary>
        /// The value is a length in units of the renderer's space (Float, Vector2): the reconciler converts it to logical
        /// pixels. Only the shader effect nodes of VL.Qt set it; uniforms of user shaders are sent as they are.
        /// </summary>
        internal bool IsLength { get; private init; }

        /// <summary>Texture: the element rendered as texture (becomes a hidden child of the ShaderEffect).</summary>
        internal Element? Texture { get; private init; }

        /// <summary>Image: the image resource sampled as texture.</summary>
        internal QtImage? Image { get; private init; }

        internal static Uniform Float(string name, float value) => new(name, Protocol.Vt.F32) { Numbers = new Matrix { M11 = value } };

        /// <summary>A float that is a length in units (<see cref="IsLength"/>).</summary>
        internal static Uniform Length(string name, float value) => Float(name, value) with { IsLength = true };

        /// <summary>A vec2 that is a length in units (<see cref="IsLength"/>).</summary>
        internal static Uniform LengthVector2(string name, Vector2 v) => Vector2(name, v) with { IsLength = true };

        internal static Uniform Int(string name, int value) => new(name, Protocol.Vt.I32) { Integer = value };

        internal static Uniform Bool(string name, bool value) => new(name, Protocol.Vt.Bool) { Integer = value ? 1 : 0 };

        internal static Uniform Vector2(string name, Vector2 v) => new(name, Protocol.Vt.Vec2) { Numbers = new Matrix { M11 = v.X, M12 = v.Y } };

        internal static Uniform Vector3(string name, Vector3 v) => new(name, Protocol.Vt.Vec3) { Numbers = new Matrix { M11 = v.X, M12 = v.Y, M13 = v.Z } };

        internal static Uniform Vector4(string name, Vector4 v) => new(name, Protocol.Vt.Vec4) { Numbers = new Matrix { M11 = v.X, M12 = v.Y, M13 = v.Z, M14 = v.W } };

        /// <summary>Straight alpha on the wire; QtNative premultiplies (like Qt's ShaderEffect).</summary>
        internal static Uniform Color(string name, Color4 c) => new(name, Protocol.Vt.Color) { Numbers = new Matrix { M11 = c.R, M12 = c.G, M13 = c.B, M14 = c.A } };

        /// <summary>Stride matrix, row-major on the wire like SET Transform; QtNative transposes for GLSL.</summary>
        internal static Uniform Matrix(string name, Matrix m) => new(name, Protocol.Vt.Mat4) { Numbers = m };

        /// <summary>Texture and Image: how the sampler reads it (null = as its source made it).</summary>
        internal Sampler? Sampler { get; private init; }

        /// <summary>Sampler-only entry for the sampler2D <paramref name="name"/> (pins of shader nodes; null = nothing is sent).</summary>
        internal static Uniform FromSampler(string name, Sampler? sampler) => new(name, Protocol.Vt.Sampler) { Sampler = sampler };

        internal static Uniform FromTexture(string name, Element? texture, Sampler? sampler = null) => new(name, Protocol.Vt.Ref) { Texture = texture, Sampler = sampler };

        internal static Uniform FromImage(string name, QtImage? image, Sampler? sampler = null) => new(name, Protocol.Vt.U64) { Image = image, Sampler = sampler };

        public override string ToString() => $"Uniform {Name}";
    }
}
