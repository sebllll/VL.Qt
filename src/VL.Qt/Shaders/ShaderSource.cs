using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using VL.Qt.Wire;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// One loaded version of a shader file: the texts, their annotations and the pin model from the reflection. Does no
    /// logging; the layouts of the two generated nodes (<see cref="ShaderLayout"/>, <see cref="ShaderEffectLayout"/>) are made
    /// from it.
    /// </summary>
    /// <param name="FragmentSource">Content of the .frag (empty if not readable).</param>
    /// <param name="VertexSource">Content of the .vert, null without .vert.</param>
    /// <param name="FragmentAnnotations">Annotations of the .frag (<see cref="ShaderAnnotations.Empty"/> if not readable).</param>
    /// <param name="VertexAnnotations">Annotations of the .vert, null without .vert.</param>
    /// <param name="PinModel">Pins and messages from the reflection; empty with an error if the reflection failed or the file is not readable.</param>
    /// <param name="ReadError">Why the files could not be read, null if they were.</param>
    /// <param name="KeptError">
    /// Set when the file now fails (read or compile error) and the rest of the record is its last good version
    /// (<see cref="NodeFactories.LastGood{T}"/>): the error of the new version.
    /// </param>
    internal sealed record ShaderSource(
        string FragmentSource,
        string? VertexSource,
        ShaderAnnotations FragmentAnnotations,
        ShaderAnnotations? VertexAnnotations,
        ShaderPinModel PinModel,
        string? ReadError,
        string? KeptError = null)
    {
        // Used only when the shader does not compile: keeps the Input pin (and its link) through a typo.
        private static readonly Regex s_sourceSampler = new(@"\buniform\s+sampler2D\s+" + ReservedUniforms.InputSampler + @"\b", RegexOptions.CultureInvariant);

        /// <summary>
        /// The shader declares <c>sampler2D source</c> (comments ignored): a filter, which also becomes a node of the effect
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
        /// The shader has an Input: from the reflection, and from a text check when the shader does not compile (the pin model
        /// then knows nothing).
        /// </summary>
        public bool HasInput => PinModel.Error.Length > 0
            ? s_sourceSampler.IsMatch(FragmentSource) || (VertexSource != null && s_sourceSampler.IsMatch(VertexSource))
            : PinModel.HasInput;

        /// <summary>True if the files could not be read or the shader did not compile or reflect.</summary>
        public bool Failed => ReadError != null || PinModel.Error.Length > 0;

        /// <summary>
        /// Reads, annotates and reflects (<paramref name="reflect"/>, normally <see cref="ShaderReflection.Reflect"/>) the files of
        /// <paramref name="file"/>. Problems are in <see cref="ReadError"/> and <see cref="ShaderPinModel.Error"/>, nothing is thrown.
        /// </summary>
        public static ShaderSource Load(ShaderFile file, Func<string, int, ShaderReflection> reflect)
        {
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
                return new ShaderSource(string.Empty, null, ShaderAnnotations.Empty, null,
                    new ShaderPinModel(false, ImmutableArray<ShaderPin>.Empty, ImmutableArray<ShaderMessage>.Empty, string.Empty),
                    $"Could not read the shader file: {e.Message}");
            }

            var fragmentAnnotations = ShaderAnnotations.Parse(fragmentSource);
            var vertexAnnotations = vertexSource is null ? null : ShaderAnnotations.Parse(vertexSource);
            ShaderPinModel model;
            try
            {
                var fragment = reflect(fragmentSource, Protocol.ShaderStage.Fragment);
                var vertex = vertexSource is null ? null : reflect(vertexSource, Protocol.ShaderStage.Vertex);
                model = ShaderPinModel.Build(fragment, vertex, fragmentAnnotations, vertexAnnotations);
            }
            catch (Exception e)
            {
                // QtNative missing or broken: a node error, not a crash of the node browser.
                model = new ShaderPinModel(false, ImmutableArray<ShaderPin>.Empty, ImmutableArray<ShaderMessage>.Empty, $"Reflection failed: {e.Message}");
            }
            return new ShaderSource(fragmentSource, vertexSource, fragmentAnnotations, vertexAnnotations, model, null);
        }
    }
}
