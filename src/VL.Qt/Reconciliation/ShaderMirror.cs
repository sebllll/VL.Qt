using System.Collections.Generic;

namespace VL.Qt.Reconciliation
{
    /// <summary>State of a Shader element as QtNative holds it: the input child sent as Source and the uniform table.</summary>
    internal sealed class ShaderMirror
    {
        /// <summary>The input child mirror last sent as SET Source (null = none sent).</summary>
        public ItemMirror? Source { get; set; }

        /// <summary>The uniform table QtNative holds (last SET_UNIFORMS); empty = nothing sent.</summary>
        public Dictionary<UniformKey, UniformValue> Sent { get; private set; } = new();

        /// <summary>The table a visit builds; it becomes <see cref="Sent"/> through <see cref="Commit"/> when it was sent. Cleared by the next visit.</summary>
        public Dictionary<UniformKey, UniformValue> Scratch { get; private set; } = new();

        /// <summary>The table in <see cref="Scratch"/> was sent: it is now <see cref="Sent"/> (no new table is allocated).</summary>
        public void Commit() => (Sent, Scratch) = (Scratch, Sent);
    }
}
