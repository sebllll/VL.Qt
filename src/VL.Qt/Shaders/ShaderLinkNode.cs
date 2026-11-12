using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Qt.Elements;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// A filter shader file as link of the Effect chain (#489): template Single, or TwoPass with @passes 2, one fragment
    /// shader resource owned by the node, the uniform pins as <see cref="ShaderLink.Uniforms"/>, @padding as padding.
    /// </summary>
    internal sealed class ShaderFileLink : ShaderLink
    {
        public ShaderFileLink(Effect? previous, ushort template, ulong shaderId, Spread<Uniform> uniforms, RectangleF padding)
            : base(previous, template, ImmutableArray.Create(shaderId), uniforms, padding, 0f)
        {
        }
    }

    /// <summary>
    /// Node description of the effect chain link of one filter shader file: same name as the element node, category
    /// <see cref="ShaderFile.LinkCategory"/>. Same lifecycle as <see cref="ShaderNodeDescription"/> (lazy init, content
    /// change invalidates the node and keeps links on pins with the same name, errors red at the node).
    /// </summary>
    internal static class ShaderLinkDescription
    {
        static readonly IVLPinDescription[] s_outputs =
        {
            new ShaderPinDescription("Output", typeof(Effect), null, null),
            new ShaderPinDescription("Error", typeof(string), string.Empty, "Shader bake errors at runtime (with line numbers), empty if none."),
        };

        const string inputSummary = "Effect chain to extend: what the other effect nodes before this one made of the element. This link works on that result.";

        /// <summary>Input pins: Input (the chain before), image and uniform pins.</summary>
        internal static List<IVLPinDescription> Inputs(ShaderNodeLayout layout)
        {
            var inputs = new List<IVLPinDescription> { new ShaderPinDescription("Input", typeof(Effect), null, inputSummary) };
            foreach (var pin in layout.Pins)
                inputs.Add(new ShaderPinDescription(pin.Name, UniformPin.ClrType(pin.Type), pin.Default, pin.Summary, isVisible: pin.Type != ShaderPinType.Sampler));
            return inputs;
        }

        internal static IReadOnlyList<IVLPinDescription> Outputs => s_outputs;

        public static IVLNodeDescription New(IVLNodeDescriptionFactory factory, ShaderFile file, ILogger logger) =>
            factory.NewNodeDescription(
                file.FullName,
                file.LinkCategory,
                fragmented: false, // like the element node: vvvv calls Update only for non-fragmented descriptions
                invalidated: ShaderNodeFactory.WatchContent(file),
                init: context =>
                {
                    var layout = ShaderNodeLayout.Load(file, ShaderReflection.Reflect, logger).ForChain(logger, file.FragmentPath);
                    return context.Node(
                        Inputs(layout),
                        Outputs,
                        newNode: c => new ShaderLinkNodeInstance(c.NodeContext, layout).CreateNode(c),
                        messages: layout.Messages,
                        openEditorAction: () => ShaderNodeDescription.OpenEditor(file, logger),
                        summary: layout.Summary ?? file.Summary,
                        filePath: file.FragmentPath);
                });
    }

    /// <summary>
    /// Instance of a generated effect chain node: bakes its shader once (held until the node is disposed or rebuilt by a
    /// file change), builds a new <see cref="ShaderFileLink"/> only when the chain at Input, the shader id or a pin changed
    /// (the reconciler compares by reference), passes the chain on unchanged while there is no shader id.
    /// </summary>
    internal sealed class ShaderLinkNodeInstance : IDisposable
    {
        readonly ShaderSlot _fragment;
        readonly NodeErrors? _errors;
        readonly string _fragmentSource;
        readonly ushort _template;
        readonly ShaderUniforms _uniforms;
        // @padding <uniform> [factor]: the pin and its factor, null for a fixed padding.
        readonly IVLPin<float>? _paddingPin;
        readonly float _paddingFactor;
        RectangleF _padding;
        float _paddingSource = float.NaN;
        readonly ValuePin<Effect?> _input = new(null);
        readonly ValuePin<Effect?> _output = new(null);
        readonly ValuePin<string> _error = new(string.Empty);
        ShaderFileLink? _last;
        Effect? _lastInput;
        ulong _lastId;
        Spread<Uniform>? _lastUniforms;

        public ShaderLinkNodeInstance(NodeContext nodeContext, ShaderNodeLayout layout)
            : this(ResourceClient.ForApp(nodeContext.AppHost), layout, new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake resource API, no node errors.</summary>
        internal ShaderLinkNodeInstance(ResourceClient? client, ShaderNodeLayout layout, NodeErrors? errors = null)
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
                    _last = new ShaderFileLink(input, _template, id, uniforms, _padding);
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

        /// <summary>Recomputes the padding of <c>@padding &lt;uniform&gt; [factor]</c> (abs(value) * factor, all sides) when the pin value changed; true if it did.</summary>
        bool UpdatePadding()
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
