using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.NodeFactories;
using VL.Qt.Nodes;
using VL.Qt.Wire;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Instance of a generated effect chain node: a <see cref="ShaderEffectBuilder{TProps}"/> over the baked fragment shader
    /// (held until the node is disposed or rebuilt by a file change), plus the uniform pins and the padding rule of
    /// <c>@padding</c>. Shader problems show at the node like those of the built-in effect nodes.
    /// </summary>
    internal sealed class ShaderEffectInstance : IDisposable
    {
        /// <summary>The inputs of the link besides the chain: compared by reference, a new spread means a pin changed.</summary>
        internal readonly record struct Props(Spread<Uniform> Uniforms, RectangleF Padding)
        {
            public bool Equals(Props other) => ReferenceEquals(Uniforms, other.Uniforms) && Padding == other.Padding;

            public override int GetHashCode() => HashCode.Combine(Uniforms, Padding);
        }

        private readonly ShaderEffectBuilder<Props> _builder;
        private readonly ShaderUniforms _uniforms;
        // @padding <uniform> [factor]: the pin and its factor, null for a fixed padding.
        private readonly IVLPin<float>? _paddingPin;
        private readonly float _paddingFactor;
        private RectangleF _padding;
        private float _paddingSource = float.NaN;
        private readonly ValuePin<Effect?> _input = new(null);
        private readonly ValuePin<Effect?> _output = new(null);

        public ShaderEffectInstance(NodeContext nodeContext, ShaderNodeLayout layout)
            : this(ResourceClient.ForApp(nodeContext.AppHost), layout, new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake resource API, no node errors.</summary>
        internal ShaderEffectInstance(ResourceClient? client, ShaderNodeLayout layout, NodeErrors? errors = null)
        {
            var template = layout.Passes == 2 ? Protocol.EffectTemplate.TwoPass : Protocol.EffectTemplate.Single;
            _builder = new ShaderEffectBuilder<Props>(client, new[] { ShaderEffectSource.Fragment(layout.FragmentSource) },
                (previous, ids, p) => new ShaderEffect(previous, template, ids, p.Uniforms, p.Padding), errors);
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
        internal int Created => _builder.Created;

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

        internal IEnumerable<IVLPin> Outputs => new IVLPin[] { _output };

        public IVLNode CreateNode(NodeBuilding.NodeInstanceBuildContext context) =>
            context.Node(Inputs, Outputs, update: Update, dispose: Dispose);

        public void Update()
        {
            var uniforms = _uniforms.Update();
            UpdatePadding();
            _output.Value = _builder.Describe(_input.Value, new Props(uniforms, _padding));
        }

        /// <summary>
        /// Recomputes the padding of <c>@padding &lt;uniform&gt; [factor]</c> (abs(value) * factor, all sides) when the pin
        /// value changed.
        /// </summary>
        private void UpdatePadding()
        {
            if (_paddingPin is null)
                return;
            var value = _paddingPin.Value;
            if (value.Equals(_paddingSource))
                return;
            _paddingSource = value;
            var dip = MathF.Abs(value) * _paddingFactor;
            if (!float.IsFinite(dip))
                dip = 0f;
            _padding = new RectangleF(dip, dip, dip, dip);
        }

        public void Dispose()
        {
            _builder.Dispose();
        }
    }
}
