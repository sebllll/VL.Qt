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
        private readonly ValuePin<bool> _apply = new(true);
        private readonly ValuePin<Effect?> _output = new(null);

        public ShaderEffectInstance(NodeContext nodeContext, ShaderEffectLayout layout)
            : this(ResourceClient.ForApp(nodeContext.AppHost), layout, new NodeErrors(nodeContext))
        {
        }

        internal ShaderEffectInstance(ResourceClient? client, ShaderEffectLayout layout, NodeErrors? errors = null)
        {
            _builder = new ShaderEffectBuilder<Props>(client, new[] { ShaderEffectSource.Fragment(layout.FragmentSource) },
                (previous, ids, p) => new ShaderEffect(previous, layout.Template, ids, p.Uniforms, p.Padding), errors);
            _uniforms = new ShaderUniforms(layout.Pins);
            switch (layout.Padding)
            {
                case ShaderPadding.FixedPadding fixedPadding:
                    _padding = fixedPadding.Dip;
                    break;
                case ShaderPadding.UniformPadding uniformPadding:
                    // The layout only holds a uniform that is a float pin of its pins.
                    for (var i = 0; i < layout.Pins.Length; i++)
                    {
                        if (layout.Pins[i].Type == ShaderPinType.Float && layout.Pins[i].UniformName == uniformPadding.Uniform)
                        {
                            _paddingPin = (IVLPin<float>)_uniforms.Pins[i];
                            _paddingFactor = uniformPadding.Factor;
                            break;
                        }
                    }
                    break;
            }
        }

        /// <summary>Uniform pins in pin order.</summary>
        internal ShaderUniforms Uniforms => _uniforms;

        /// <summary>Input pins in the order of the description's inputs.</summary>
        internal IEnumerable<IVLPin> Inputs
        {
            get
            {
                yield return _input;
                foreach (var pin in _uniforms.Pins)
                    yield return pin;
                yield return _apply;
            }
        }

        internal IEnumerable<IVLPin> Outputs => new IVLPin[] { _output };

        public IVLNode CreateNode(NodeBuilding.NodeInstanceBuildContext context) =>
            context.Node(Inputs, Outputs, update: Update, dispose: Dispose);

        public void Update()
        {
            var uniforms = _uniforms.Update();
            UpdatePadding();
            // Off passes the chain on; the uniform pins are still read so their change tracking stays current.
            _output.Value = _apply.Value ? _builder.Describe(_input.Value, new Props(uniforms, _padding)) : _input.Value;
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
