using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.NodeFactories;
using VL.Qt.Wire;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Instance of a generated effect chain node: bakes its shader once (held until the node is disposed or rebuilt by a
    /// file change), builds a new <see cref="ShaderFileEffect"/> only when the chain at Input, the shader id or a pin changed
    /// (the reconciler compares by reference), passes the chain on unchanged while there is no shader id.
    /// </summary>
    internal sealed class ShaderEffectInstance : IDisposable
    {
        private readonly ShaderSlot _fragment;
        private readonly NodeErrors? _errors;
        private readonly string _fragmentSource;
        private readonly ushort _template;
        private readonly ShaderUniforms _uniforms;
        // @padding <uniform> [factor]: the pin and its factor, null for a fixed padding.
        private readonly IVLPin<float>? _paddingPin;
        private readonly float _paddingFactor;
        private RectangleF _padding;
        private float _paddingSource = float.NaN;
        private readonly ValuePin<Effect?> _input = new(null);
        private readonly ValuePin<Effect?> _output = new(null);
        private readonly ValuePin<string> _error = new(string.Empty);
        private ShaderFileEffect? _last;
        private Effect? _lastInput;
        private ulong _lastId;
        private Spread<Uniform>? _lastUniforms;

        public ShaderEffectInstance(NodeContext nodeContext, ShaderNodeLayout layout)
            : this(ResourceClient.ForApp(nodeContext.AppHost), layout, new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake resource API, no node errors.</summary>
        internal ShaderEffectInstance(ResourceClient? client, ShaderNodeLayout layout, NodeErrors? errors = null)
        {
            _fragment = new ShaderSlot(client, Protocol.ShaderStage.Fragment, "Fragment shader");
            _errors = errors;
            _fragmentSource = layout.FragmentSource;
            _template = layout.Passes == 2 ? Protocol.EffectTemplate.TwoPass : Protocol.EffectTemplate.Single;
            _padding = layout.Padding;
            _uniforms = new ShaderUniforms(layout.Pins);
            if (layout.PaddingUniform != null)
            {
                for (var i = 0; i < layout.Pins.Length; i++)
                {
                    if (layout.Pins[i].Type == ShaderPinType.Float && layout.Pins[i].UniformName == layout.PaddingUniform)
                    {
                        _paddingPin = (IVLPin<float>)_uniforms.Pins[i];
                        _paddingFactor = layout.PaddingFactor;
                        break;
                    }
                }
            }
        }

        /// <summary>Links created so far (tests).</summary>
        internal int Created { get; private set; }

        /// <summary>Uniform pins in pin order (tests).</summary>
        internal ShaderUniforms Uniforms => _uniforms;

        /// <summary>Input pins in the order of the description's inputs.</summary>
        internal IEnumerable<IVLPin> Inputs
        {
            get
            {
                yield return _input;
                foreach (var pin in _uniforms.Pins)
                    yield return pin;
            }
        }

        internal IEnumerable<IVLPin> Outputs => new IVLPin[] { _output, _error };

        public IVLNode CreateNode(NodeBuilding.NodeInstanceBuildContext context) =>
            context.Node(Inputs, Outputs, update: Update, dispose: Dispose);

        public void Update()
        {
            var id = _fragment.Update(null, _fragmentSource);
            var uniforms = _uniforms.Update();
            var input = _input.Value;
            var paddingChanged = UpdatePadding();
            if (id == 0)
            {
                _last = null;
                _output.Value = input;
            }
            else
            {
                if (_last is null || !ReferenceEquals(input, _lastInput) || id != _lastId || !ReferenceEquals(uniforms, _lastUniforms) || paddingChanged)
                {
                    _last = new ShaderFileEffect(input, _template, id, uniforms, _padding);
                    _lastInput = input;
                    _lastId = id;
                    _lastUniforms = uniforms;
                    Created++;
                }
                _output.Value = _last;
            }

            var error = _fragment.Error;
            if (error != _error.Value)
            {
                _error.Value = error;
                _errors?.LogOnly(error);
            }
        }

        /// <summary>
        /// Recomputes the padding of <c>@padding &lt;uniform&gt; [factor]</c> (abs(value) * factor, all sides) when the pin
        /// value changed; true if it did.
        /// </summary>
        private bool UpdatePadding()
        {
            if (_paddingPin is null)
                return false;
            var value = _paddingPin.Value;
            if (value.Equals(_paddingSource))
                return false;
            _paddingSource = value;
            var dip = MathF.Abs(value) * _paddingFactor;
            if (!float.IsFinite(dip))
                dip = 0f;
            _padding = new RectangleF(dip, dip, dip, dip);
            return true;
        }

        public void Dispose()
        {
            _fragment.Dispose();
            _errors?.Dispose();
        }
    }
}
