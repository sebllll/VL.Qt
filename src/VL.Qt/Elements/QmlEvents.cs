using System;
using System.Buffers.Binary;
using System.Text;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Decoding of the two QML events of QtNative (protocol.h "QmlElement"). Values come boxed as float, int, bool, string,
    /// Color4 (straight alpha), Vector2/3/4 or RectangleF, in the units of QML (no unit conversion). The events are sent on
    /// change only, so allocating the name and the box is fine.
    /// </summary>
    internal static class QmlEvents
    {
        private const int maxArguments = 64;

        /// <summary>
        /// EV_QML_PROPERTY: u16 name length, u16 value type, name (UTF-8), padding to 4, value. False for a short or
        /// malformed payload, an empty name or a value type that is not supported.
        /// </summary>
        public static bool TryDecodeProperty(ReadOnlySpan<byte> payload, out string name, out object value)
        {
            name = string.Empty;
            value = null!;
            if (payload.Length < 4)
                return false;
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2));
            int valueOffset = 4 + Align(nameLength);
            if (nameLength == 0 || valueOffset > payload.Length)
                return false;
            if (!TryReadValue(payload.Slice(valueOffset), type, out var v, out _))
                return false;
            name = Encoding.UTF8.GetString(payload.Slice(4, nameLength));
            value = v;
            return true;
        }

        /// <summary>
        /// EV_QML_SIGNAL: u16 name length, u16 argument count, name, padding to 4, then per argument u16 value type, u16 0,
        /// value, padding to 4. False for a short or malformed payload, an empty name or an argument of an unknown type
        /// (QtNative sends unsupported QML types as strings).
        /// </summary>
        public static bool TryDecodeSignal(ReadOnlySpan<byte> payload, out QmlSignal signal)
        {
            signal = null!;
            if (payload.Length < 4)
                return false;
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2));
            int offset = 4 + Align(nameLength);
            if (nameLength == 0 || count > maxArguments || offset > payload.Length)
                return false;
            var name = Encoding.UTF8.GetString(payload.Slice(4, nameLength));
            var arguments = Spread<object>.Empty;
            if (count > 0)
            {
                var builder = new SpreadBuilder<object>(count);
                for (int i = 0; i < count; i++)
                {
                    if (payload.Length - offset < 4)
                        return false;
                    ushort type = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset));
                    offset += 4;
                    if (!TryReadValue(payload.Slice(offset), type, out var v, out int size))
                        return false;
                    builder.Add(v);
                    offset += Align(size);
                }
                arguments = builder.ToSpread();
            }
            signal = new QmlSignal(name, arguments);
            return true;
        }

        /// <summary>
        /// Reads a value encoded like SET (f32, i32, bool as u8, string as u32 length + UTF-8, color and rect 4 x f32, vec2/3/4
        /// 2/3/4 x f32). <paramref name="size"/> is the number of bytes read, without padding.
        /// </summary>
        internal static bool TryReadValue(ReadOnlySpan<byte> p, ushort type, out object value, out int size)
        {
            value = null!;
            size = 0;
            switch (type)
            {
                case Protocol.Vt.F32:
                    if (p.Length < 4) return false;
                    value = F(p, 0);
                    size = 4;
                    return true;
                case Protocol.Vt.I32:
                    if (p.Length < 4) return false;
                    value = BinaryPrimitives.ReadInt32LittleEndian(p);
                    size = 4;
                    return true;
                case Protocol.Vt.Bool:
                    if (p.Length < 1) return false;
                    value = p[0] != 0;
                    size = 1;
                    return true;
                case Protocol.Vt.String:
                    if (p.Length < 4) return false;
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(p);
                    if (n > (uint)(p.Length - 4)) return false;
                    value = Encoding.UTF8.GetString(p.Slice(4, (int)n));
                    size = 4 + (int)n;
                    return true;
                case Protocol.Vt.Color:
                    if (p.Length < 16) return false;
                    value = new Color4(F(p, 0), F(p, 4), F(p, 8), F(p, 12));
                    size = 16;
                    return true;
                case Protocol.Vt.Rectf:
                    if (p.Length < 16) return false;
                    value = new RectangleF(F(p, 0), F(p, 4), F(p, 8), F(p, 12));
                    size = 16;
                    return true;
                case Protocol.Vt.Vec2:
                    if (p.Length < 8) return false;
                    value = new Vector2(F(p, 0), F(p, 4));
                    size = 8;
                    return true;
                case Protocol.Vt.Vec3:
                    if (p.Length < 12) return false;
                    value = new Vector3(F(p, 0), F(p, 4), F(p, 8));
                    size = 12;
                    return true;
                case Protocol.Vt.Vec4:
                    if (p.Length < 16) return false;
                    value = new Vector4(F(p, 0), F(p, 4), F(p, 8), F(p, 12));
                    size = 16;
                    return true;
                default:
                    return false;
            }
        }

        private static float F(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadSingleLittleEndian(p.Slice(offset));

        private static int Align(int n) => (n + (Protocol.CommandAlign - 1)) & ~(Protocol.CommandAlign - 1);
    }
}
