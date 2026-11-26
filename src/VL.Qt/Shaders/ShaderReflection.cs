using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Result of vlqt_shader_reflect: raw facts of one stage. Pin rules (filter qt_*, source → Input,
    /// type mapping) are not applied here but in <see cref="ShaderPinModel"/>.
    /// </summary>
    /// <param name="Ok">False if the bake failed; then Uniforms/Samplers are empty and Error holds the compiler lines.</param>
    /// <param name="Uniforms">Uniform block members in block order (offset), including qt_Matrix/qt_Opacity.</param>
    /// <param name="Samplers">Samplers in binding order.</param>
    /// <param name="Error">Error lines joined with '\n' ("&lt;stage&gt; code, line &lt;n&gt;: &lt;text&gt;"), empty if Ok.</param>
    internal sealed record ShaderReflection(
        bool Ok,
        ImmutableArray<ReflectedUniform> Uniforms,
        ImmutableArray<ReflectedSampler> Samplers,
        string Error)
    {
        private const int initialBufferSize = 4096;

        /// <summary>Failed reflection with the given error text (also used for QtNative/argument errors).</summary>
        public static ShaderReflection Failed(string error) =>
            new(false, ImmutableArray<ReflectedUniform>.Empty, ImmutableArray<ReflectedSampler>.Empty, error);

        /// <summary>
        /// Bakes source (full Qt GLSL, <paramref name="stage"/> a <see cref="Protocol.ShaderStage"/>) to SPIR-V in QtNative and reflects
        /// it. Synchronous, any thread, serialized with the
        /// runtime bakes in QtNative. A failure of QtNative becomes a failed reflection.
        /// </summary>
        public static ShaderReflection Reflect(string source, int stage)
        {
            var buffer = new byte[initialBufferSize];
            while (true)
            {
                var rc = Native.vlqt_shader_reflect(source, stage, ref buffer[0], buffer.Length, out var written, out var required);
                if (rc == Native.Result.BufferTooSmall)
                {
                    buffer = new byte[required];
                    continue;
                }
                if (rc != Native.Result.Ok)
                    return Failed($"vlqt_shader_reflect failed ({rc}): {Native.LastError()}");
                // written includes the terminating NUL.
                return Parse(Encoding.UTF8.GetString(buffer, 0, Math.Max(0, written - 1)));
            }
        }

        /// <summary>
        /// Parses the text format of vlqt_shader_reflect (see vlqt.h). Testable without QtNative. Unknown or malformed lines
        /// are ignored.
        /// </summary>
        public static ShaderReflection Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Failed("empty reflection");

            var lines = text.Split('\n');
            var first = lines[0].Trim();
            if (first == "error")
            {
                var errors = lines.Skip(1).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
                var message = string.Join('\n', errors);
                return Failed(message.Length > 0 ? message : "shader bake failed");
            }
            if (first != "ok")
                return Failed($"unexpected reflection output: {first}");

            var uniforms = ImmutableArray.CreateBuilder<ReflectedUniform>();
            var samplers = ImmutableArray.CreateBuilder<ReflectedSampler>();
            foreach (var raw in lines.Skip(1))
            {
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 4 && parts[0] == "uniform" && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var arrayLength))
                    uniforms.Add(new ReflectedUniform(parts[1], parts[2], arrayLength));
                else if (parts.Length == 3 && parts[0] == "sampler" && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var binding))
                    samplers.Add(new ReflectedSampler(parts[1], binding));
            }
            return new ShaderReflection(true, uniforms.ToImmutable(), samplers.ToImmutable(), string.Empty);
        }
    }
}
