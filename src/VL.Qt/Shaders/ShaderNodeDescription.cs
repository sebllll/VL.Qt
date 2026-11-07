using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;

namespace VL.Qt.Shaders
{
    /// <summary>Pin description with help text and visibility (optional pins), like VL.Stride's EffectPinDescription.</summary>
    internal sealed class ShaderPinDescription : IVLPinDescription, IInfo, IVLPinDescriptionWithVisibility
    {
        public ShaderPinDescription(string name, Type type, object? defaultValue, string? summary, bool isVisible = true)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
            Summary = summary;
            IsVisible = isVisible;
        }

        public string Name { get; }

        public Type Type { get; }

        public object? DefaultValue { get; }

        public string? Summary { get; }

        public string? Remarks => null;

        public bool IsVisible { get; }
    }

    /// <summary>A plain input/output pin of a generated node instance.</summary>
    internal sealed class ValuePin<T> : IVLPin<T>
    {
        public ValuePin(T value) => Value = value;

        public T Value { get; set; }

        T? IVLPin<T>.Value
        {
            get => Value;
            set => Value = value!;
        }

        object? IVLPin.Value
        {
            get => Value;
            set => Value = value is T t ? t : default!;
        }
    }

    /// <summary>
    /// Node description of one shader file (#488). init (lazy, when vvvv needs the pins): read
    /// the files, reflect, build the pins (Input if the shader samples source, image and uniform pins, fixed ShaderEffect
    /// pins), errors and warnings as messages of the description. invalidated: content change of the own .frag/.vert;
    /// vvvv then asks for a new description and keeps links on pins with the same name.
    /// </summary>
    internal static class ShaderNodeDescription
    {
        // Fixed pins like the ShaderEffect node (without Fragment/Vertex Code/Shader and Uniforms), help texts from there.
        static readonly IVLPinDescription[] s_fixedInputs =
        {
            new ShaderPinDescription("Blending", typeof(bool), true, "Premultiplied alpha blending with what is behind. Off: the shader output replaces it."),
            new ShaderPinDescription("Mesh Resolution", typeof(Int2), new Int2(1, 1), "Grid subdivisions (x, y >= 1) for vertex shaders that deform the element.", isVisible: false),
            new ShaderPinDescription("Size", typeof(Vector2), new Vector2(1f, 1f), "Size of the element (width, height), only with Auto Size off or without Input. The position comes from Transform and Anchor."),
            new ShaderPinDescription("Auto Size", typeof(bool), true, "On: with an Input the implicit size (size of the input) applies. Without Input or off: Size counts.", isVisible: false),
            new ShaderPinDescription("Transform", typeof(Matrix), Matrix.Identity, "Position, rotation and scale; applied around the Anchor (like Skia)."),
            new ShaderPinDescription("Anchor", typeof(RectangleAnchor), RectangleAnchor.TopLeft, "Point of the element that lies at the origin of its parent space; Transform rotates/scales around it.", isVisible: false),
            new ShaderPinDescription("Opacity", typeof(float), 1f, "Opacity of the effect output (qt_Opacity in the shader). 0 = not drawn, but still takes its space and receives input.", isVisible: false),
            new ShaderPinDescription("Clip", typeof(bool), false, "Clip to the element's size.", isVisible: false),
            new ShaderPinDescription("Debug", typeof(bool), false, "Debug overlay for this element only.", isVisible: false),
            new ShaderPinDescription("Interaction", typeof(Interaction), null, "Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.", isVisible: false),
            new ShaderPinDescription("Effect", typeof(Effect), null, "Chain of effect nodes (Blur, DropShadow, …), drawn as one effect layer on the element.", isVisible: false),
        };

        static readonly IVLPinDescription[] s_outputs =
        {
            new ShaderPinDescription("Output", typeof(Element), null, null),
            new ShaderPinDescription("Measured Size", typeof(Vector2), Vector2.Zero, "Implicit size reported by Qt, in units (last frame).", isVisible: false),
            new ShaderPinDescription("Scene Bounds", typeof(RectangleF), RectangleF.Empty, "Bounds in window coordinates after layout and transform, in units (last frame).", isVisible: false),
            new ShaderPinDescription("Error", typeof(string), string.Empty, "Shader bake errors at runtime (with line numbers), empty if none."),
        };

        const string inputSummary = "Element sampled as sampler2D \"source\" (hidden itself, only the effect output is drawn).";

        /// <summary>Input pins: Input (if the shader samples source), image and uniform pins, fixed ShaderEffect pins.</summary>
        internal static List<IVLPinDescription> Inputs(ShaderNodeLayout layout)
        {
            var inputs = new List<IVLPinDescription>();
            if (layout.HasInput)
            {
                inputs.Add(new ShaderPinDescription("Input", typeof(Element), null, inputSummary));
                inputs.Add(new ShaderPinDescription("Input Sampler", typeof(Sampler), null, "How the shader reads the Input (filter, wrap, mipmaps), from the Sampler node. Unconnected: as the input's layer is (Nearest, Clamp).", isVisible: false));
            }
            foreach (var pin in layout.Pins)
                inputs.Add(new ShaderPinDescription(pin.Name, UniformPin.ClrType(pin.Type), pin.Default, pin.Summary, isVisible: pin.Type != ShaderPinType.Sampler));
            inputs.AddRange(s_fixedInputs);
            return inputs;
        }

        /// <summary>Output pins like the ShaderEffect node.</summary>
        internal static IReadOnlyList<IVLPinDescription> Outputs => s_outputs;

        public static IVLNodeDescription New(IVLNodeDescriptionFactory factory, ShaderFile file, ILogger logger) =>
            factory.NewNodeDescription(
                file.FullName,
                file.Category,
                // Not fragmented: vvvv calls IVLNode.Update only for non-fragmented node descriptions (like VL.ImGui's
                // immediate-mode nodes); fragmented ones (VL.Stride shader nodes) compute in output getters instead.
                fragmented: false,
                invalidated: ShaderNodeFactory.WatchContent(file),
                init: context =>
                {
                    var layout = ShaderNodeLayout.Load(file, ShaderReflection.Reflect, logger);
                    return context.Node(
                        Inputs(layout),
                        Outputs,
                        newNode: c => new ShaderNodeInstance(c.NodeContext, layout).CreateNode(c),
                        messages: layout.Messages,
                        openEditorAction: () => OpenEditor(file, logger),
                        summary: layout.Summary ?? file.Summary,
                        filePath: file.FragmentPath);
                });

        /// <summary>
        /// Middle click / Definition > Open (#498): opens the .frag and, if there is one, the .vert next to it in the
        /// default editor, like VL.Stride's shader nodes.
        /// </summary>
        internal static bool OpenEditor(ShaderFile file, ILogger logger)
        {
            var opened = Open(file.FragmentPath, logger);
            if (file.VertexPath != null && File.Exists(file.VertexPath))
                opened |= Open(file.VertexPath, logger);
            return opened;
        }

        static bool Open(string path, ILogger logger)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not open shader file {Path}", path);
                return false;
            }
        }
    }

    /// <summary>
    /// Instance of a generated shader node: a <see cref="ShaderEffectNode"/> fed with the file content (source path #483)
    /// and the uniform pins as Spread&lt;Uniform&gt;, rebuilt only when a pin changed.
    /// </summary>
    internal sealed class ShaderNodeInstance : IDisposable
    {
        readonly ShaderEffectNode _effect;
        readonly string _fragmentSource;
        readonly string? _vertexSource;
        readonly ValuePin<Element?>? _input;
        readonly ValuePin<Sampler?>? _inputSampler;
        readonly ShaderUniforms _uniforms;
        readonly ValuePin<bool> _blending = new(true);
        readonly ValuePin<Int2> _meshResolution = new(new Int2(1, 1));
        readonly ValuePin<Vector2> _size = new(new Vector2(1f, 1f));
        readonly ValuePin<bool> _autoSize = new(true);
        readonly ValuePin<Matrix> _transform = new(Matrix.Identity);
        readonly ValuePin<RectangleAnchor> _anchor = new(RectangleAnchor.TopLeft);
        readonly ValuePin<float> _opacity = new(1f);
        readonly ValuePin<bool> _clip = new(false);
        readonly ValuePin<bool> _debug = new(false);
        readonly ValuePin<Interaction?> _interaction = new(null);
        readonly ValuePin<Effect?> _effectChain = new(null);
        readonly ValuePin<Element?> _output = new(null);
        readonly ValuePin<Vector2> _measuredSize = new(Vector2.Zero);
        readonly ValuePin<RectangleF> _sceneBounds = new(RectangleF.Empty);
        readonly ValuePin<string> _error = new(string.Empty);

        public ShaderNodeInstance(NodeContext nodeContext, ShaderNodeLayout layout)
            : this(new ShaderEffectNode(nodeContext), layout)
        {
        }

        /// <summary>For tests: with a ShaderEffectNode built on the test constructor.</summary>
        internal ShaderNodeInstance(ShaderEffectNode effect, ShaderNodeLayout layout)
        {
            _effect = effect;
            _fragmentSource = layout.FragmentSource;
            _vertexSource = layout.VertexSource;
            _input = layout.HasInput ? new ValuePin<Element?>(null) : null;
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
                _size.Value,
                _autoSize.Value,
                _transform.Value,
                _anchor.Value,
                _opacity.Value,
                _clip.Value,
                _debug.Value,
                _interaction.Value,
                _effectChain.Value,
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
