using System.Text;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// Writes SET_QML_PROPERTIES for a QmlElement as a diff by name against what was sent last: new or changed entries
    /// only, nothing at all if there are none. A key that is gone from <c>next</c> is not reset in QML (the protocol has no
    /// removal; documented behavior). A large table is split into several commands (a command carries 64 KB).
    /// </summary>
    internal static class QmlPropertyWriter
    {
        /// <summary>Estimated size at which a command is closed and the next one started (well below the 64 KB limit).</summary>
        private const int maxChunkBytes = 32 * 1024;

        /// <summary>
        /// Sends the entries of <paramref name="next"/> that differ from <paramref name="previous"/> (null = send all).
        /// Returns the number of entries sent.
        /// </summary>
        public static int Write(ICommandSink sink, uint id, Elements.QmlProperties next, Elements.QmlProperties? previous)
        {
            int sent = 0, chunkBytes = 0;
            bool open = false;
            foreach (var (name, value) in next)
            {
                if (previous is not null && previous.TryGetValue(name, out var old) && old == value)
                    continue;
                int size = EntrySize(name, value);
                if (open && chunkBytes + size > maxChunkBytes)
                {
                    sink.EndQmlProperties();
                    open = false;
                }
                if (!open)
                {
                    sink.BeginQmlProperties(id, 0);
                    open = true;
                    chunkBytes = 0;
                }
                WriteEntry(sink, name, value);
                chunkBytes += size;
                sent++;
            }
            if (open)
                sink.EndQmlProperties();
            return sent;
        }

        private static int EntrySize(string name, in Elements.QmlValue v)
            => 8 + Encoding.UTF8.GetByteCount(name) + 16 + (v.Text is null ? 0 : 4 + Encoding.UTF8.GetByteCount(v.Text));

        internal static void WriteEntry(ICommandSink sink, string name, in Elements.QmlValue v)
        {
            switch (v.ValueType)
            {
                case Protocol.ValueType.F32: sink.UniformF32(name, v.A); break;
                case Protocol.ValueType.I32: sink.UniformI32(name, v.Integer); break;
                case Protocol.ValueType.Bool: sink.UniformBool(name, v.Integer != 0); break;
                case Protocol.ValueType.Color: sink.UniformColor(name, v.A, v.B, v.C, v.D); break;
                case Protocol.ValueType.Vec2: sink.UniformVec2(name, v.A, v.B); break;
                case Protocol.ValueType.Vec3: sink.UniformVec3(name, v.A, v.B, v.C); break;
                case Protocol.ValueType.Vec4: sink.UniformVec4(name, v.A, v.B, v.C, v.D); break;
                case Protocol.ValueType.Rectf: sink.QmlRect(name, v.A, v.B, v.C, v.D); break;
                case Protocol.ValueType.String: sink.QmlString(name, v.Text ?? string.Empty); break;
            }
        }
    }
}
