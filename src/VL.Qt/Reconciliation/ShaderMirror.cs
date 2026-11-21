using System.Collections.Generic;

namespace VL.Qt.Reconciliation
{
    /// <summary>State of a Shader element as QtNative holds it: the input child sent as Source and the uniform table.</summary>
    internal sealed class ShaderMirror
    {
        /// <summary>The input child mirror last sent as SET Source (null = none sent).</summary>
        public ItemMirror? Source { get; set; }

        /// <summary>The uniform table QtNative holds (last SET_UNIFORMS), null = nothing sent.</summary>
        public Dictionary<string, UniformValue>? SentUniforms { get; set; }
    }
}
