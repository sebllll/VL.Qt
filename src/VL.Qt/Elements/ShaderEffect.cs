using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Collections;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Draws a pre-baked .qsb fragment shader (optionally with a vertex shader) over its bounds, optionally sampling
    /// an <see cref="Input"/> element as sampler2D "source". Uniforms are matched by name through the shader's
    /// reflection (protocol.h "ShaderEffect").
    /// </summary>
    public sealed class ShaderEffect : Element
    {
        private readonly Element? _input;
        private readonly Spread<Uniform> _uniforms = Spread<Uniform>.Empty;
        private readonly ImmutableArray<Element> _children = ImmutableArray<Element>.Empty;

        public ShaderEffect() : base(ElementType.ShaderEffect) { }

        /// <summary>On (default): with an Input the implicit size (size of the input) applies; without Input Size counts anyway.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Element bound to the sampler2D named "source"; null = generative.</summary>
        public Element? Input
        {
            get => _input;
            init
            {
                _input = value;
                _children = BuildChildren(value, _uniforms);
            }
        }

        /// <summary>How the sampler "source" reads the Input (null = as its layer is).</summary>
        public Sampler? InputSampler { get; init; }

        /// <summary>Uniform values by name. Texture uniforms add their element as child (after Input, each element once).</summary>
        public Spread<Uniform> Uniforms
        {
            get => _uniforms;
            init
            {
                _uniforms = value ?? Spread<Uniform>.Empty;
                _children = BuildChildren(_input, _uniforms);
            }
        }

        /// <summary>Shader resource id (VLQT_RES_KIND_SHADER, stage fragment) owned by the node; 0 = none, nothing drawn.</summary>
        internal ulong FragmentShaderId { get; init; }

        /// <summary>Shader resource id (stage vertex) owned by the node; 0 = Qt's default vertex shader.</summary>
        internal ulong VertexShaderId { get; init; }

        /// <summary>Premultiplied alpha blending (default on).</summary>
        public bool Blending { get; init; } = true;

        /// <summary>Grid subdivisions of the mesh, x and y &gt;= 1 (default 1, 1 = one quad).</summary>
        public Int2 MeshResolution { get; init; } = new Int2(1, 1);

        public override ImmutableArray<Element> Children => _children;

        private static ImmutableArray<Element> BuildChildren(Element? input, Spread<Uniform> uniforms)
        {
            var builder = ImmutableArray.CreateBuilder<Element>();
            if (input is not null)
                builder.Add(input);
            foreach (var u in uniforms)
                if (u?.Texture is { } t && !builder.Contains(t))
                    builder.Add(t);
            return builder.Count == 0 ? ImmutableArray<Element>.Empty : builder.ToImmutable();
        }
    }
}
