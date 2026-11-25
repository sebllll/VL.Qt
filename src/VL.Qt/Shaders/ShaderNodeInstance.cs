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
    internal sealed class ShaderNodeInstance : IDisposable
    {
        private readonly ShaderNode _effect;
        private readonly string _fragmentSource;
        private readonly string? _vertexSource;
        private readonly ValuePin<IElement?>? _input;
        private readonly ValuePin<Sampler?>? _inputSampler;
        private readonly ShaderUniforms _uniforms;
        private readonly ValuePin<bool> _blending = new(true);
        private readonly ValuePin<Int2> _meshResolution = new(new Int2(1, 1));
        private readonly ValuePin<Vector2> _size = new(new Vector2(1f, 1f));
        private readonly ValuePin<bool> _autoSize = new(true);
        private readonly ValuePin<Matrix> _transform = new(Matrix.Identity);
        private readonly ValuePin<RectangleAnchor> _anchor = new(RectangleAnchor.TopLeft);
        private readonly ValuePin<float> _opacity = new(1f);
        private readonly ValuePin<bool> _clip = new(false);
        private readonly ValuePin<bool> _debug = new(false);
        private readonly ValuePin<Interaction?> _interaction = new(null);
        private readonly ValuePin<Effect?> _effectChain = new(null);
        private readonly ValuePin<bool> _enabled = new(true);
        private readonly ValuePin<IElement?> _output = new(null);
        private readonly ValuePin<Vector2> _measuredSize = new(Vector2.Zero);
        private readonly ValuePin<RectangleF> _sceneBounds = new(RectangleF.Empty);
        private readonly ValuePin<string> _error = new(string.Empty);

        public ShaderNodeInstance(NodeContext nodeContext, ShaderNodeLayout layout)
            : this(new ShaderNode(nodeContext), layout)
        {
        }

        /// <summary>For tests: with a ShaderNode built on the test constructor.</summary>
        internal ShaderNodeInstance(ShaderNode effect, ShaderNodeLayout layout)
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
                yield return _size;
                yield return _autoSize;
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

        internal IEnumerable<IVLPin> Outputs => new IVLPin[] { _output, _measuredSize, _sceneBounds, _error };

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
                out var sceneBounds,
                out var error);
            _measuredSize.Value = measuredSize;
            _sceneBounds.Value = sceneBounds;
            _error.Value = error;
        }

        public void Dispose() => _effect.Dispose();
    }
}
