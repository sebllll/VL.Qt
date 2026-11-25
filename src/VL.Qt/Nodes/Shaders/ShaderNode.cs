using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Lang;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Shaders;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Draws a custom shader over the element's area, optionally sampling an input element. The shader is GLSL text
    /// (Vulkan-style GLSL 440, baked at runtime while you edit it) or a pre-baked .qsb file (Qt's qsb tool); a file
    /// wins over the text. Fragment shader, optionally a vertex shader. Uniforms are matched by
    /// name with the shader's uniform block (which starts with mat4 qt_Matrix and float qt_Opacity, like in Qt's
    /// ShaderEffect). Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// The normal way to write an effect is a shader file in a qtshaders/ folder (it becomes a node of its own); use ShaderEffect for shader code that is built or loaded at runtime.
    /// </summary>
    // The node owns the shader resources (path -> vlqt_shader_load, ids in Shader.FragmentShaderId/
    // VertexShaderId); the reconciler sends the properties and the uniform diff (SET_UNIFORMS), only on change.
    // "builtin:<name>" (a shader shipped with VL.Qt) reaches QtNative as such; vvvv resolves a Path IOBox against the document directory, so
    // ResourceClient.TryGetBuiltinShader also accepts "C:\dir\builtin:<name>" and normalizes it.
    [ProcessNode(Name = "ShaderEffect", Category = Categories.Shaders), Smell(SymbolSmell.Advanced)]
    public sealed class ShaderNode : IDisposable
    {
        internal readonly record struct Props(
            Element? Input, ulong FragmentShaderId, ulong VertexShaderId, Spread<Uniform>? Uniforms, bool Blending,
            Int2 MeshResolution, bool AutoSize, Sampler? InputSampler);

        private readonly ElementNodeCore<Shader, Props> _node;
        private readonly ShaderSlot _fragment, _vertex;
        private readonly ElementInput _input;
        private readonly NodeErrors? _errors;
        private Spread<Uniform>? _checkedUniforms;
        private string _uniformProblems = string.Empty, _shownUniformProblems = string.Empty, _reservedUniforms = string.Empty;
        private string _fragmentError = string.Empty, _vertexError = string.Empty, _elementError = string.Empty, _error = string.Empty;

        public ShaderNode(NodeContext nodeContext)
        {
            var client = ResourceClient.ForApp(nodeContext.AppHost);
            _node = new ElementNodeCore<Shader, Props>(nodeContext, s_create);
            _input = new ElementInput(_node.Messages);
            _fragment = new ShaderSlot(client, Protocol.ShaderStage.Fragment, "Fragment shader");
            _vertex = new ShaderSlot(client, Protocol.ShaderStage.Vertex, "Vertex shader");
            _errors = new NodeErrors(nodeContext);
        }

        /// <summary>For tests without shader resources (no client, a path is reported as error): warnings go to <paramref name="addWarning"/>.</summary>
        internal ShaderNode(Func<string, IDisposable> addWarning)
            : this(null, addWarning)
        {
        }

        /// <summary>For tests: a client on a fake resource API; warnings go to <paramref name="addWarning"/>, nothing is logged.</summary>
        internal ShaderNode(ResourceClient? client, Func<string, IDisposable> addWarning)
        {
            _node = new ElementNodeCore<Shader, Props>(addWarning, s_create);
            _input = new ElementInput(_node.Messages);
            _fragment = new ShaderSlot(client, Protocol.ShaderStage.Fragment, "Fragment shader");
            _vertex = new ShaderSlot(client, Protocol.ShaderStage.Vertex, "Vertex shader");
        }

        private static readonly Func<CommonInputs, Props, FeedbackSlot, Shader> s_create = static (c, p, slot) => new Shader(c, slot)
            {
                Input = p.Input,
                InputSampler = p.InputSampler,
                FragmentShaderId = p.FragmentShaderId,
                VertexShaderId = p.VertexShaderId,
                Uniforms = p.Uniforms ?? Spread<Uniform>.Empty,
                Blending = p.Blending,
                MeshResolution = p.MeshResolution,
                AutoSize = p.AutoSize,
            };

        internal ElementNodeCore<Shader, Props> Core => _node;

        /// <param name="input">Element sampled as sampler2D "source" (hidden itself, only the effect output is drawn). Unconnected = generative shader.</param>
        /// <param name="inputSampler">How the shader reads the Input (filter, wrap, mipmaps), from the Sampler node. Unconnected = as the input's layer is (Nearest, Clamp).</param>
        /// <param name="fragmentCode">Fragment shader as full Qt GLSL (#version 440), baked at runtime in the background. The default is a commented template. Ignored while Fragment Shader is set. While a new text bakes or if it has errors, the last working version keeps drawing; errors (with line numbers) go to Error.</param>
        /// <param name="fragmentShader">Baked fragment shader (.qsb or builtin:&lt;name&gt;). Set: used instead of Fragment Code. A broken file = message at Error.</param>
        /// <param name="vertexCode">Vertex shader as full Qt GLSL (#version 440), optional. Empty = Qt's default vertex shader. Ignored while Vertex Shader is set.</param>
        /// <param name="vertexShader">Baked vertex shader (.qsb), optional. Set: used instead of Vertex Code.</param>
        /// <param name="uniforms">Uniform values by name (nodes of Qt.Elements.Shaders.Uniform). Unknown names or wrong types show as warning; a reserved name (qt_Matrix, qt_Opacity, source) is an error.</param>
        /// <param name="blending">Premultiplied alpha blending with what is behind. Off: the shader output replaces it.</param>
        /// <param name="meshResolution">Grid subdivisions (x, y &gt;= 1) for vertex shaders that deform the element. Unconnected = 1, 1 (one quad).</param>
        /// <param name="size">Size of the element (width, height), only with Auto Size off or without Input. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: with an Input the implicit size (size of the input) applies. Without Input or off: Size counts.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Set it here, not on the input.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <param name="opacity">Opacity of the effect output (qt_Opacity in the shader). 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip to the element's size.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='measuredSize']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <param name="error">Shader load errors and uniform problems (unknown name, wrong type, reserved name), empty if none.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            IElement? input,
            [Pin(Visibility = PinVisibility.Optional)] Sampler? inputSampler,
            [DefaultValue(ShaderTemplates.Fragment)] string? fragmentCode,
            Path? fragmentShader,
            [Pin(Visibility = PinVisibility.Optional)] string? vertexCode,
            [Pin(Visibility = PinVisibility.Optional)] Path? vertexShader,
            Spread<Uniform>? uniforms,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool blending,
            [Pin(Visibility = PinVisibility.Optional)] Optional<Int2> meshResolution,
            [DefaultValue("1, 1")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds,
            out string error)
        {
            var mesh = meshResolution.HasValue ? meshResolution.Value : new Int2(1, 1);
            var fragmentId = _fragment.Update(fragmentShader?.Value, fragmentCode);
            var vertexId = _vertex.Update(vertexShader?.Value, vertexCode);
            if (!ReferenceEquals(uniforms, _checkedUniforms))
            {
                _checkedUniforms = uniforms;
                _uniformProblems = CheckUniforms(uniforms);
                _reservedUniforms = CheckReserved(uniforms);
                // At the node (and logged once), like every message of a node; the Error output carries it too.
                _errors?.Set("Reserved uniforms", MessageSeverity.Error, _reservedUniforms);
            }
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new Props(_input.Update(input), fragmentId, vertexId, uniforms, blending, mesh, autoSize, inputSampler),
                out measuredSize, out sceneBounds);
            error = UpdateError();
            return element;
        }

        /// <summary>Joins shader load errors, uniform name problems, reserved uniform names and the element error (Qt) into the Error output, one line each; recomputed only when a part changes.</summary>
        private string UpdateError()
        {
            string fragment = _fragment.Error, vertex = _vertex.Error, element = _node.Error ?? string.Empty;
            var problems = JoinLines(_reservedUniforms, _uniformProblems);
            if (fragment != _fragmentError || vertex != _vertexError || element != _elementError || problems != _shownUniformProblems)
            {
                _fragmentError = fragment;
                _vertexError = vertex;
                _elementError = element;
                _shownUniformProblems = problems;
                _error = JoinLines(fragment, vertex, problems, element);
                // Qt's element error is already logged and shown as warning by the element node, the reserved names by Set in Update.
                _errors?.LogOnly(JoinLines(fragment, vertex, _uniformProblems, string.Empty));
            }
            return _error;
        }

        private static string JoinLines(params string[] parts) => string.Join('\n', Array.FindAll(parts, static p => p.Length > 0));

        /// <summary>Name problems QtNative could not tell (it never sees them): empty or too long names, a name set twice (the last one counts).</summary>
        internal static string CheckUniforms(Spread<Uniform>? uniforms)
        {
            if (uniforms is null || uniforms.Count == 0)
                return string.Empty;
            var lines = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < uniforms.Count; i++)
            {
                var u = uniforms[i];
                if (u is null)
                    continue;
                if (!Protocol.IsValidName(u.Name))
                    lines.Add(string.IsNullOrEmpty(u.Name)
                        ? $"Uniform #{i}: the name is empty"
                        : $"Uniform #{i}: the name is longer than {Protocol.UniformNameMax} bytes");
                else if (!seen.Add(u.Name))
                    lines.Add($"Uniform '{u.Name}' is set more than once, the last one counts");
            }
            return string.Join('\n', lines);
        }

        /// <summary>One line per uniform of a reserved name (<see cref="ReservedUniforms.Item"/>): QtNative would ignore it with an error line.</summary>
        internal static string CheckReserved(Spread<Uniform>? uniforms)
        {
            if (uniforms is null || uniforms.Count == 0)
                return string.Empty;
            var lines = new System.Collections.Generic.List<string>();
            foreach (var u in uniforms)
                if (u?.Name is { } name && ReservedUniforms.IsItemReserved(name) && !lines.Contains(ReservedUniforms.Describe(name)))
                    lines.Add(ReservedUniforms.Describe(name));
            return string.Join('\n', lines);
        }

        public void Dispose()
        {
            _fragment.Dispose();
            _vertex.Dispose();
            _errors?.Dispose();
            _input.Dispose();
            _node.Dispose();
        }
    }
}
