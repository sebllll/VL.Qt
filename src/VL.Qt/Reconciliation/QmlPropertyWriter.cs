using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Writes SET_QML_PROPERTIES for a QmlElement as a diff by name against what was sent last: new or changed entries
    /// only, nothing at all if there are none. A key that is gone from <c>next</c> is not reset in QML (the protocol has no
    /// removal; documented behavior). All entries go into one command.
    /// </summary>
    internal static class QmlPropertyWriter
    {
        /// <summary>
        /// Sends the entries of <paramref name="next"/> that differ from <paramref name="previous"/> (null = send all).
        /// Returns the number of entries sent.
        /// </summary>
        public static int Write(ICommandSink sink, uint id, Elements.QmlProperties next, Elements.QmlProperties? previous)
        {
            int sent = 0;
            foreach (var (name, value) in next)
            {
                if (previous is not null && previous.TryGetValue(name, out var old) && old == value)
                    continue;
                if (sent == 0)
                    sink.BeginQmlProperties(id, 0);
                WriteEntry(sink, name, value);
                sent++;
            }
            if (sent > 0)
                sink.EndQmlProperties();
            return sent;
        }

        internal static void WriteEntry(ICommandSink sink, string name, in Elements.QmlValue v)
        {
            switch (v.ValueType)
            {
                case Protocol.ValueType.F32: sink.QmlPropertyF32(name, v.A); break;
                case Protocol.ValueType.I32: sink.QmlPropertyI32(name, v.Integer); break;
                case Protocol.ValueType.Bool: sink.QmlPropertyBool(name, v.Integer != 0); break;
                case Protocol.ValueType.Color: sink.QmlPropertyColor(name, v.A, v.B, v.C, v.D); break;
                case Protocol.ValueType.Vec2: sink.QmlPropertyVec2(name, v.A, v.B); break;
                case Protocol.ValueType.Vec3: sink.QmlPropertyVec3(name, v.A, v.B, v.C); break;
                case Protocol.ValueType.Vec4: sink.QmlPropertyVec4(name, v.A, v.B, v.C, v.D); break;
                case Protocol.ValueType.Rectf: sink.QmlPropertyRect(name, v.A, v.B, v.C, v.D); break;
                case Protocol.ValueType.String: sink.QmlPropertyString(name, v.Text ?? string.Empty); break;
            }
        }
    }
}
