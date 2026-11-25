using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Nodes;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Draws a fragment shader (and an optional vertex shader) over its bounds; <see cref="Input"/> is sampled as
    /// sampler2D "source". Uniforms are matched by name through the shader's reflection (protocol.h "ShaderEffect").
    /// </summary>
    internal sealed class Shader : Element
    {
        private readonly Element? _input;
        private readonly Spread<Uniform> _uniforms = Spread<Uniform>.Empty;
        // default = not computed yet; the element is immutable, so the first read is cached for good.
        private ImmutableArray<Element> _childItems;

        public Shader() : base(ElementType.Shader) { }
        internal Shader(in CommonInputs common, FeedbackSlot feedback) : base(ElementType.Shader, common, feedback) { }

        /// <summary>On (default): with an Input the implicit size (size of the input) applies; without Input Size counts anyway.</summary>
        public bool AutoSize { get; init; } = true;

        /// <summary>Element bound to the sampler2D named "source"; null = generative.</summary>
        public Element? Input
        {
            get => _input;
            init => _input = value;
        }

        /// <summary>How the sampler "source" reads the Input (null = as its layer is).</summary>
        public Sampler? InputSampler { get; init; }

        /// <summary>Uniform values by name. Texture uniforms add their element as child (after Input, each element once).</summary>
        public Spread<Uniform> Uniforms
        {
            get => _uniforms;
            init => _uniforms = value ?? Spread<Uniform>.Empty;
        }

        /// <summary>Shader resource id (VLQT_RESOURCE_KIND_SHADER, stage fragment) owned by the node; 0 = none, nothing drawn.</summary>
        internal ulong FragmentShaderId { get; init; }

        /// <summary>Shader resource id (stage vertex) owned by the node; 0 = Qt's default vertex shader.</summary>
        internal ulong VertexShaderId { get; init; }

        /// <summary>Premultiplied alpha blending (default on).</summary>
        public bool Blending { get; init; } = true;

        /// <summary>Grid subdivisions of the mesh, x and y &gt;= 1 (default 1, 1 = one quad).</summary>
        public Int2 MeshResolution { get; init; } = new Int2(1, 1);

        /// <summary>The hidden texture sources: <see cref="Input"/> first, then the texture uniforms, each element once.</summary>
        internal override ImmutableArray<Element> ChildItems
        {
            get
            {
                if (_childItems.IsDefault)
                    _childItems = BuildChildItems();
                return _childItems;
            }
        }

        private ImmutableArray<Element> BuildChildItems()
        {
            var builder = ImmutableArray.CreateBuilder<Element>();
            if (_input is not null)
                builder.Add(_input);
            foreach (var u in _uniforms)
                if (u?.Texture is { } t && !builder.Contains(t))
                    builder.Add(t);
            return builder.Count == 0 ? ImmutableArray<Element>.Empty : builder.ToImmutable();
        }
    }
}
