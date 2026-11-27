using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Writes SET_QML_PROPERTIES for a QmlElement as a diff by name against what was sent last: new or changed entries
    /// only, nothing at all if there are none. A key that is gone from <c>next</c> is not reset in QML (the protocol has no
    /// removal; documented behavior). All entries go into one command. A font goes with its size in pixels of the scene
    /// (<see cref="SpaceMapping.ToFontPixels"/>, as at Text) and is sent again when the unit scale changes.
    /// </summary>
    internal static class QmlPropertyWriter
    {
        /// <summary>
        /// Sends the entries of <paramref name="next"/> that differ from <paramref name="previous"/> (null = send all), and every
        /// font when <paramref name="rescale"/> (the unit scale of <paramref name="mapping"/> changed). Returns the number of entries sent.
        /// </summary>
        public static int Write(ICommandSink sink, uint id, Elements.QmlProperties next, Elements.QmlProperties? previous, in SpaceMapping mapping,
            bool rescale)
        {
            int sent = 0;
            foreach (var (name, value) in next)
            {
                if (previous is not null && previous.TryGetValue(name, out var old) && old == value
                    && !(rescale && value.ValueType == Protocol.ValueType.Font))
                    continue;
                if (sent == 0)
                    sink.BeginQmlProperties(id, 0);
                WriteEntry(sink, name, value, mapping);
                sent++;
            }
            if (sent > 0)
                sink.EndQmlProperties();
            return sent;
        }

        internal static void WriteEntry(ICommandSink sink, string name, in Elements.QmlValue v, in SpaceMapping mapping)
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
                case Protocol.ValueType.DateTime: sink.QmlPropertyDateTime(name, v.Long); break;
                case Protocol.ValueType.Font: sink.QmlPropertyFont(name, v.Text ?? string.Empty, mapping.ToFontPixels(v.A), v.Integer, v.B != 0); break;
                case Protocol.ValueType.List: WriteList(sink, name, v.Items); break;
            }
        }

        private static long[] DateTimes(Spread<DateTime> dates)
        {
            var milliseconds = new long[dates.Count];
            for (int i = 0; i < milliseconds.Length; i++)
                milliseconds[i] = QmlDateTime.ToWire(dates[i]);
            return milliseconds;
        }

        private static string[] PathTexts(Spread<Path> paths)
        {
            var texts = new string[paths.Count];
            for (int i = 0; i < texts.Length; i++)
                texts[i] = paths[i]?.Value ?? string.Empty;
            return texts;
        }

        // The vector, color and rect element types are plain runs of floats (checked by a test), so they go over as floats.
        private static void WriteList(ICommandSink sink, string name, object? items)
        {
            switch (items)
            {
                case Spread<float> s: sink.QmlPropertyListF32(name, Protocol.ValueType.F32, s.AsSpan()); break;
                case Spread<int> s: sink.QmlPropertyListI32(name, s.AsSpan()); break;
                case Spread<bool> s: sink.QmlPropertyListBool(name, s.AsSpan()); break;
                case Spread<string> s: sink.QmlPropertyListString(name, s.AsSpan()); break;
                // Only when the list changed, so the array of texts is no allocation per frame.
                case Spread<Path> s: sink.QmlPropertyListString(name, PathTexts(s)); break;
                case Spread<DateTime> s: sink.QmlPropertyListDateTime(name, DateTimes(s)); break;
                case Spread<Color4> s: sink.QmlPropertyListF32(name, Protocol.ValueType.Color, MemoryMarshal.Cast<Color4, float>(s.AsSpan())); break;
                case Spread<Vector2> s: sink.QmlPropertyListF32(name, Protocol.ValueType.Vec2, MemoryMarshal.Cast<Vector2, float>(s.AsSpan())); break;
                case Spread<Vector3> s: sink.QmlPropertyListF32(name, Protocol.ValueType.Vec3, MemoryMarshal.Cast<Vector3, float>(s.AsSpan())); break;
                case Spread<Vector4> s: sink.QmlPropertyListF32(name, Protocol.ValueType.Vec4, MemoryMarshal.Cast<Vector4, float>(s.AsSpan())); break;
                case Spread<RectangleF> s: sink.QmlPropertyListF32(name, Protocol.ValueType.Rectf, MemoryMarshal.Cast<RectangleF, float>(s.AsSpan())); break;
            }
        }
    }
}
