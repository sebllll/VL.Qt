using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;
using VL.Core.Diagnostics;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Everything the init of a generated shader node needs, built once per node description: the file sources,
    /// the pin model (with pin names made unique against the fixed pins) and the messages shown at the node. Errors (red):
    /// file not readable, compile error (then only the fixed pins), duplicate file name. Warnings (orange): the pin
    /// model's warnings. Everything is logged too.
    /// </summary>
    /// <param name="HasInput">Node has an Input pin (sampler2D source).</param>
    /// <param name="Pins">Image and uniform pins in pin order, names unique.</param>
    /// <param name="Messages">Messages of the node description (errors first).</param>
    /// <param name="Summary">@summary of the file head, null if not given.</param>
    /// <param name="FragmentSource">Content of the .frag (empty if not readable).</param>
    /// <param name="VertexSource">Content of the .vert, null without .vert.</param>
    /// <param name="Padding">Fixed @padding of the file head in DIP (effect chain node only), zero if not given.</param>
    /// <param name="Passes">@passes of the file head (effect chain node only): 1 = template Single, 2 = TwoPass.</param>
    /// <param name="PaddingUniform">
    /// Uniform of <c>@padding &lt;uniform&gt; [factor]</c> (effect chain node only), null for a fixed padding; after
    /// <see cref="ForChain"/> only a valid float pin.
    /// </param>
    /// <param name="PaddingFactor">Factor of <c>@padding &lt;uniform&gt; [factor]</c>.</param>
    /// <param name="ChainErrors">Wrong head tags of the effect chain node (red there, see <see cref="ForChain"/>), empty if none.</param>
    internal sealed record ShaderNodeLayout(
        bool HasInput,
        ImmutableArray<ShaderPin> Pins,
        ImmutableArray<Message> Messages,
        string? Summary,
        string FragmentSource,
        string? VertexSource,
        RectangleF Padding = default,
        int Passes = 1,
        string? PaddingUniform = null,
        float PaddingFactor = 1f,
        ImmutableArray<string> ChainErrors = default)
    {
        /// <summary>Pin names a uniform pin must not take (the Input pin, the Apply pin of the effect chain node and the fixed Shader pins).</summary>
        public static readonly ImmutableHashSet<string> ReservedPinNames = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "Input", "Blending", "Mesh Resolution", "Size", "Auto Size", "Transform", "Anchor", "Opacity", "Clip", "Debug",
            "Interaction", "Effect", "Enabled", "Apply", "Output", "Measured Size", "Scene Bounds", "Error");

        // Used only when the shader does not compile: keeps the Input pin (and its link) through a typo.
        private static readonly Regex s_sourceSampler = new(@"\buniform\s+sampler2D\s+source\b", RegexOptions.CultureInvariant);

        /// <summary>
        /// The shader declares <c>sampler2D source</c> (comments ignored): a filter, which also becomes a link of the effect
        /// chain. Cheap text check for the scan; the node's init reflects for real.
        /// </summary>
        public static bool IsFilter(string fragmentSource)
        {
            foreach (var line in fragmentSource.Split('\n'))
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                if (s_sourceSampler.IsMatch(comment >= 0 ? line[..comment] : line))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The layout for the effect chain node of the same file: without the uniforms QtNative sets itself
        /// (<see cref="ReservedUniforms.ChainAutomatic"/>: texelSize, dpr, weight, ...), plus an error for each uniform of a
        /// name the chain reserves (<see cref="ReservedUniforms.ChainReserved"/>: base, glow, shadow; no pin), messages for a
        /// file that is no filter (any more) and for a .vert, which a chain link does not use. Errors too: a wrong @passes or @padding tag, and an
        /// @padding uniform the shader does not have or that is not a float (then <see cref="PaddingUniform"/> is null).
        /// </summary>
        public ShaderNodeLayout ForChain(ILogger logger, string path)
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var pins = ImmutableArray.CreateBuilder<ShaderPin>(Pins.Length);
            foreach (var pin in Pins)
            {
                if (ReservedUniforms.ChainAutomatic.Contains(pin.UniformName))
                    continue;
                if (ReservedUniforms.IsChainReserved(pin.UniformName))
                {
                    var text = $"uniform '{pin.UniformName}' is reserved in an effect chain (set by VL.Qt), no pin";
                    if (!errors.Contains(text))
                        errors.Add(text);
                }
                else
                    pins.Add(pin);
            }
            if (VertexSource != null)
                warnings.Add("the vertex shader (.vert) is not used by an effect chain link");

            if (!ChainErrors.IsDefault)
                errors.AddRange(ChainErrors);
            var compiled = !Messages.Any(static m => m.Type == MessageType.Error);
            if (!HasInput && FragmentSource.Length > 0 && compiled)
                errors.Add("the shader has no 'uniform sampler2D source' (any more), it is not a filter and cannot be a link of the effect chain");

            var paddingUniform = PaddingUniform;
            if (paddingUniform != null && compiled)
            {
                // The pins the node gets (not the uniforms QtNative sets itself); a shader that does not compile has none and already shows its error.
                var pin = pins.FirstOrDefault(p => p.Type != ShaderPinType.Sampler && p.UniformName == paddingUniform);
                if (pin is null)
                {
                    errors.Add($"@padding names the uniform '{paddingUniform}', which the shader does not have as an input");
                    paddingUniform = null;
                }
                else if (pin.Type != ShaderPinType.Float)
                {
                    errors.Add($"@padding names the uniform '{paddingUniform}', which is not a float");
                    paddingUniform = null;
                }
            }

            foreach (var w in warnings)
                logger.LogWarning("{Shader}: {Warning}", path, w);
            foreach (var e in errors)
                logger.LogError("{Shader}: {Error}", path, e);

            var messages = ImmutableArray.CreateBuilder<Message>(Messages.Length + errors.Count + warnings.Count);
            foreach (var m in Messages)
                if (m.Type == MessageType.Error)
                    messages.Add(m);
            foreach (var e in errors)
                messages.Add(new Message(MessageType.Error, e));
            foreach (var m in Messages)
                if (m.Type != MessageType.Error)
                    messages.Add(m);
            foreach (var w in warnings)
                messages.Add(new Message(MessageType.Warning, w));
            return this with { Pins = pins.ToImmutable(), Messages = messages.ToImmutable(), PaddingUniform = paddingUniform, ChainErrors = ImmutableArray<string>.Empty };
        }

        /// <summary>
        /// Reads, reflects (<paramref name="reflect"/>, normally <see cref="ShaderReflection.Reflect"/>) and builds the pins of
        /// <paramref name="file"/>.
        /// </summary>
        public static ShaderNodeLayout Load(ShaderFile file, Func<string, ShaderStage, ShaderReflection> reflect, ILogger logger)
        {
            var errors = new List<string>();
            if (file.DuplicateError != null)
                errors.Add(file.DuplicateError); // logged once per scan by the factory

            string fragmentSource;
            string? vertexSource = null;
            try
            {
                fragmentSource = File.ReadAllText(file.FragmentPath);
                if (file.VertexPath != null)
                    vertexSource = File.ReadAllText(file.VertexPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // E.g. locked by the editor that saves it; the next watcher event builds the node again.
                var text = $"Could not read the shader file: {e.Message}";
                logger.LogError("{Shader}: {Error}", file.FragmentPath, text);
                errors.Add(text);
                return new ShaderNodeLayout(false, ImmutableArray<ShaderPin>.Empty, ToMessages(errors, []), file.Summary, string.Empty, null);
            }

            var fragmentAnnotations = ShaderAnnotations.Parse(fragmentSource);
            var vertexAnnotations = vertexSource is null ? null : ShaderAnnotations.Parse(vertexSource);
            ShaderPinModel model;
            try
            {
                var fragment = reflect(fragmentSource, ShaderStage.Fragment);
                var vertex = vertexSource is null ? null : reflect(vertexSource, ShaderStage.Vertex);
                model = ShaderPinModel.Build(file.FullName, fragment, vertex, fragmentAnnotations, vertexAnnotations);
            }
            catch (Exception e)
            {
                // QtNative missing or broken: a node error, not a crash of the node browser.
                model = new ShaderPinModel(false, ImmutableArray<ShaderPin>.Empty, ImmutableArray<ShaderAnnotationWarning>.Empty, $"Reflection failed: {e.Message}");
            }

            var hasInput = model.HasInput;
            if (model.Error.Length > 0)
            {
                logger.LogError("{Shader}: {Error}", file.FragmentPath, model.Error);
                errors.Add(model.Error);
                hasInput = s_sourceSampler.IsMatch(fragmentSource) || (vertexSource != null && s_sourceSampler.IsMatch(vertexSource));
            }

            var warnings = new List<string>();
            foreach (var w in model.Warnings)
                warnings.Add(w.ToString());
            var pins = UniquePinNames(model.Pins, hasInput, warnings);
            foreach (var w in warnings)
                logger.LogWarning("{Shader}: {Warning}", file.FragmentPath, w);

            var chainErrors = fragmentAnnotations.Errors.IsDefault
                ? ImmutableArray<string>.Empty
                : fragmentAnnotations.Errors.Select(static e => e.ToString()).ToImmutableArray();
            return new ShaderNodeLayout(hasInput, pins, ToMessages(errors, warnings), fragmentAnnotations.Summary, fragmentSource, vertexSource, fragmentAnnotations.Padding,
                fragmentAnnotations.Passes, fragmentAnnotations.PaddingUniform, fragmentAnnotations.PaddingFactor, chainErrors);
        }

        /// <summary>A uniform whose pin name is taken (by a fixed pin, Input or another uniform) gets a number: "Size 2".</summary>
        internal static ImmutableArray<ShaderPin> UniquePinNames(ImmutableArray<ShaderPin> pins, bool hasInput, List<string> warnings)
        {
            var used = new HashSet<string>(ReservedPinNames, StringComparer.OrdinalIgnoreCase);
            if (!hasInput)
                used.Remove("Input");
            var result = ImmutableArray.CreateBuilder<ShaderPin>(pins.Length);
            foreach (var pin in pins)
            {
                var name = pin.Name;
                for (var i = 2; !used.Add(name); i++)
                    name = $"{pin.Name} {i}";
                if (name != pin.Name)
                {
                    warnings.Add($"uniform '{pin.UniformName}' shows as pin '{name}', '{pin.Name}' is taken");
                    result.Add(pin with { Name = name });
                }
                else
                    result.Add(pin);
            }
            return result.MoveToImmutable();
        }

        private static ImmutableArray<Message> ToMessages(List<string> errors, List<string> warnings)
        {
            var messages = ImmutableArray.CreateBuilder<Message>(errors.Count + warnings.Count);
            foreach (var e in errors)
                messages.Add(new Message(MessageType.Error, e));
            foreach (var w in warnings)
                messages.Add(new Message(MessageType.Warning, w));
            return messages.MoveToImmutable();
        }
    }
}
