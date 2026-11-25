using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.NodeFactories;
using VL.Qt.Nodes;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Instance of a generated shader node: a <see cref="ShaderNode"/> fed with the file content (source path)
    /// and the uniform pins as Spread&lt;Uniform&gt;, rebuilt only when a pin changed.
    /// </summary>
    internal sealed class ShaderInstance : IDisposable
    {
        private readonly ShaderNode _effect;
        private readonly string _fragmentSource;
        private readonly string? _vertexSource;
        private readonly ValuePin<IElement?>? _input;
        private readonly ValuePin<Sampler?>? _inputSampler;
        private readonly ShaderUniforms _uniforms;
        private readonly ValuePin<bool> _blending = new(ShaderElementPins.Default<bool>("Blending"));
        private readonly ValuePin<Int2> _meshResolution = new(ShaderElementPins.Default<Int2>("Mesh Resolution"));
        private readonly ValuePin<bool> _autoSize = new(ShaderElementPins.Default<bool>("Auto Size"));
        private readonly ValuePin<Vector2> _size = new(ShaderElementPins.Default<Vector2>("Size"));
        private readonly ValuePin<Matrix> _transform = new(ShaderElementPins.Default<Matrix>("Transform"));
        private readonly ValuePin<RectangleAnchor> _anchor = new(ShaderElementPins.Default<RectangleAnchor>("Anchor"));
        private readonly ValuePin<float> _opacity = new(ShaderElementPins.Default<float>("Opacity"));
        private readonly ValuePin<bool> _clip = new(ShaderElementPins.Default<bool>("Clip"));
        private readonly ValuePin<bool> _debug = new(ShaderElementPins.Default<bool>("Debug"));
        private readonly ValuePin<Interaction?> _interaction = new(ShaderElementPins.Default<Interaction?>("Interaction"));
        private readonly ValuePin<Effect?> _effectChain = new(ShaderElementPins.Default<Effect?>("Effect"));
        private readonly ValuePin<bool> _enabled = new(ShaderElementPins.Default<bool>("Enabled"));
        private readonly ValuePin<IElement?> _output = new(null);
        private readonly ValuePin<Vector2> _measuredSize = new(ShaderElementPins.Default<Vector2>("Measured Size"));
        private readonly ValuePin<RectangleF> _sceneBounds = new(ShaderElementPins.Default<RectangleF>("Scene Bounds"));

        public ShaderInstance(NodeContext nodeContext, ShaderLayout layout)
            : this(new ShaderNode(nodeContext), layout)
        {
        }

        /// <summary>For tests: with a ShaderNode built on the test constructor.</summary>
        internal ShaderInstance(ShaderNode effect, ShaderLayout layout)
        {
            _effect = effect;
            _fragmentSource = layout.FragmentSource;
            _vertexSource = layout.VertexSource;
            _input = layout.HasInput ? new ValuePin<IElement?>(null) : null;
            _inputSampler = layout.HasInput ? new ValuePin<Sampler?>(null) : null;
            _uniforms = new ShaderUniforms(layout.Pins);
        }

        /// <summary>Uniform pins in pin order (tests).</summary>
        internal ShaderUniforms Uniforms => _uniforms;

        /// <summary>Input pins in the order of the description's inputs.</summary>
        internal IEnumerable<IVLPin> Inputs
        {
            get
            {
                if (_input != null)
                {
                    yield return _input;
                    yield return _inputSampler!;
                }
                foreach (var pin in _uniforms.Pins)
                    yield return pin;
                yield return _blending;
                yield return _meshResolution;
                yield return _autoSize;
                yield return _size;
                yield return _transform;
                yield return _anchor;
                yield return _opacity;
                yield return _clip;
                yield return _debug;
                yield return _interaction;
                yield return _effectChain;
                yield return _enabled;
            }
        }

        internal IEnumerable<IVLPin> Outputs => new IVLPin[] { _output, _measuredSize, _sceneBounds };

        public IVLNode CreateNode(NodeBuilding.NodeInstanceBuildContext context) =>
            context.Node(Inputs, Outputs, update: Update, dispose: Dispose);

        public void Update()
        {
            _output.Value = _effect.Update(
                _input?.Value,
                _inputSampler?.Value,
                _fragmentSource,
                fragmentShader: null,
                _vertexSource,
                vertexShader: null,
                _uniforms.Update(),
                _blending.Value,
                new Optional<Int2>(_meshResolution.Value),
                _autoSize.Value,
                _size.Value,
                _transform.Value,
                _anchor.Value,
                _opacity.Value,
                _clip.Value,
                _debug.Value,
                _interaction.Value,
                _effectChain.Value,
                _enabled.Value,
                out var measuredSize,
                out var sceneBounds);
            _measuredSize.Value = measuredSize;
            _sceneBounds.Value = sceneBounds;
        }

        public void Dispose() => _effect.Dispose();
    }
}
