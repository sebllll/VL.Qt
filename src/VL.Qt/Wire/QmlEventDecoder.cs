using System;
using System.Buffers.Binary;
using System.Text;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;

namespace VL.Qt.Wire
{
    /// <summary>
    /// Decoding of the two QML events of QtNative (protocol.h "QmlElement"). Values come boxed as float, int, bool, string,
    /// Color4 (straight alpha), Vector2/3/4, RectangleF, DateTime (local time) or Font, in the units of QML (no unit conversion)
    /// but for the size of a font, which comes in units of the given mapping like the font size of Text; a list comes as a
    /// Spread of one of these (Spread&lt;float&gt;, Spread&lt;Vector2&gt;, ...). The events are sent on change only, so
    /// allocating the name, the box and the spread is fine.
    /// </summary>
    internal static class QmlEventDecoder
    {
        private const int maxArguments = 64;

        /// <summary>
        /// EVENT_QML_PROPERTY: u16 name length, u16 value type, name (UTF-8), padding to 4, value. False for a short or
        /// malformed payload, an empty name or a value type that is not supported.
        /// </summary>
        public static bool TryDecodeProperty(ReadOnlySpan<byte> payload, out string name, out object value)
            => TryDecodeProperty(payload, SpaceMapping.Default, out name, out value);

        /// <summary>
        /// <see cref="TryDecodeProperty(ReadOnlySpan{byte}, out string, out object)"/> with the size of a font in units of <paramref name="mapping"/>.
        /// </summary>
        public static bool TryDecodeProperty(ReadOnlySpan<byte> payload, in SpaceMapping mapping, out string name, out object value)
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
            if (!TryReadValue(payload.Slice(valueOffset), type, mapping, out var v, out _))
                return false;
            name = Encoding.UTF8.GetString(payload.Slice(4, nameLength));
            value = v;
            return true;
        }

        /// <summary>
        /// EVENT_QML_SIGNAL: u16 name length, u16 argument count, name, padding to 4, then per argument u16 value type, u16 0,
        /// value, padding to 4. False for a short or malformed payload, an empty name or an argument of an unknown type
        /// (QtNative sends unsupported QML types as strings).
        /// </summary>
        public static bool TryDecodeSignal(ReadOnlySpan<byte> payload, out QmlSignal signal)
            => TryDecodeSignal(payload, SpaceMapping.Default, out signal);

        /// <summary><see cref="TryDecodeSignal(ReadOnlySpan{byte}, out QmlSignal)"/> with the size of a font in units of <paramref name="mapping"/>.</summary>
        public static bool TryDecodeSignal(ReadOnlySpan<byte> payload, in SpaceMapping mapping, out QmlSignal signal)
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
                    if (!TryReadValue(payload.Slice(offset), type, mapping, out var v, out int size))
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
        /// 2/3/4 x f32, date as i64 milliseconds since 1970 UTC, font as in protocol.h, list as in protocol.h: u16 element type,
        /// u16 0, u32 count, elements). <paramref name="size"/> is the number of bytes read, without padding.
        /// </summary>
        internal static bool TryReadValue(ReadOnlySpan<byte> p, ushort type, in SpaceMapping mapping, out object value, out int size)
        {
            value = null!;
            size = 0;
            switch (type)
            {
                case Protocol.ValueType.F32:
                    if (p.Length < 4) return false;
                    value = F(p, 0);
                    size = 4;
                    return true;
                case Protocol.ValueType.I32:
                    if (p.Length < 4) return false;
                    value = BinaryPrimitives.ReadInt32LittleEndian(p);
                    size = 4;
                    return true;
                case Protocol.ValueType.Bool:
                    if (p.Length < 1) return false;
                    value = p[0] != 0;
                    size = 1;
                    return true;
                case Protocol.ValueType.String:
                    if (p.Length < 4) return false;
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(p);
                    if (n > (uint)(p.Length - 4)) return false;
                    value = Encoding.UTF8.GetString(p.Slice(4, (int)n));
                    size = 4 + (int)n;
                    return true;
                case Protocol.ValueType.Color:
                    if (p.Length < 16) return false;
                    value = new Color4(F(p, 0), F(p, 4), F(p, 8), F(p, 12));
                    size = 16;
                    return true;
                case Protocol.ValueType.Rectf:
                    if (p.Length < 16) return false;
                    value = new RectangleF(F(p, 0), F(p, 4), F(p, 8), F(p, 12));
                    size = 16;
                    return true;
                case Protocol.ValueType.Vec2:
                    if (p.Length < 8) return false;
                    value = new Vector2(F(p, 0), F(p, 4));
                    size = 8;
                    return true;
                case Protocol.ValueType.Vec3:
                    if (p.Length < 12) return false;
                    value = new Vector3(F(p, 0), F(p, 4), F(p, 8));
                    size = 12;
                    return true;
                case Protocol.ValueType.Vec4:
                    if (p.Length < 16) return false;
                    value = new Vector4(F(p, 0), F(p, 4), F(p, 8), F(p, 12));
                    size = 16;
                    return true;
                case Protocol.ValueType.DateTime:
                    if (p.Length < 8) return false;
                    value = QmlDateTime.FromWire(BinaryPrimitives.ReadInt64LittleEndian(p));
                    size = 8;
                    return true;
                case Protocol.ValueType.Font:
                    if (p.Length < Protocol.FontValueHeaderSize) return false;
                    uint familyLength = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(12));
                    if (familyLength > (uint)(p.Length - Protocol.FontValueHeaderSize)) return false;
                    value = new Font
                    {
                        Size = mapping.FromQt(F(p, 0)),
                        Weight = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(4)),
                        Italic = p[8] != 0,
                        Family = Encoding.UTF8.GetString(p.Slice(Protocol.FontValueHeaderSize, (int)familyLength)),
                    };
                    size = Protocol.FontValueHeaderSize + (int)familyLength;
                    return true;
                case Protocol.ValueType.List:
                    return TryReadList(p, out value, out size);
                default:
                    return false;
            }
        }

        /// <summary>
        /// A list value: false for a short payload, more than <see cref="Protocol.ListValueMaxCount"/> elements, an element type
        /// that is not a plain value type (also a nested list) or elements that do not fit into the payload.
        /// </summary>
        private static bool TryReadList(ReadOnlySpan<byte> p, out object value, out int size)
        {
            value = null!;
            size = 0;
            if (p.Length < Protocol.ListValueHeaderSize)
                return false;
            ushort elementType = BinaryPrimitives.ReadUInt16LittleEndian(p);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(4));
            if (count > Protocol.ListValueMaxCount)
                return false;
            var body = p.Slice(Protocol.ListValueHeaderSize);
            int n = (int)count;
            bool ok;
            int bodySize;
            switch (elementType)
            {
                case Protocol.ValueType.F32: ok = TryReadFixed(body, n, 4, static e => F(e, 0), out value, out bodySize); break;
                case Protocol.ValueType.I32: ok = TryReadFixed(body, n, 4, static e => BinaryPrimitives.ReadInt32LittleEndian(e), out value, out bodySize); break;
                case Protocol.ValueType.Bool: ok = TryReadFixed(body, n, 1, static e => e[0] != 0, out value, out bodySize); break;
                case Protocol.ValueType.Color: ok = TryReadFixed(body, n, 16, static e => new Color4(F(e, 0), F(e, 4), F(e, 8), F(e, 12)), out value, out bodySize); break;
                case Protocol.ValueType.Rectf: ok = TryReadFixed(body, n, 16, static e => new RectangleF(F(e, 0), F(e, 4), F(e, 8), F(e, 12)), out value, out bodySize); break;
                case Protocol.ValueType.Vec2: ok = TryReadFixed(body, n, 8, static e => new Vector2(F(e, 0), F(e, 4)), out value, out bodySize); break;
                case Protocol.ValueType.Vec3: ok = TryReadFixed(body, n, 12, static e => new Vector3(F(e, 0), F(e, 4), F(e, 8)), out value, out bodySize); break;
                case Protocol.ValueType.Vec4: ok = TryReadFixed(body, n, 16, static e => new Vector4(F(e, 0), F(e, 4), F(e, 8), F(e, 12)), out value, out bodySize); break;
                case Protocol.ValueType.String: ok = TryReadStrings(body, n, out value, out bodySize); break;
                case Protocol.ValueType.DateTime: ok = TryReadFixed(body, n, 8, static e => QmlDateTime.FromWire(BinaryPrimitives.ReadInt64LittleEndian(e)), out value, out bodySize); break;
                default: return false;
            }
            if (!ok)
                return false;
            size = Protocol.ListValueHeaderSize + bodySize;
            return true;
        }

        private delegate T ReadElement<T>(ReadOnlySpan<byte> element);

        private static bool TryReadFixed<T>(ReadOnlySpan<byte> body, int count, int elementSize, ReadElement<T> read, out object value, out int size)
        {
            value = null!;
            size = count * elementSize;
            if (size > body.Length)
                return false;
            var builder = new SpreadBuilder<T>(count);
            for (int i = 0; i < count; i++)
                builder.Add(read(body.Slice(i * elementSize)));
            value = builder.ToSpread();
            return true;
        }

        // Each element is u32 length + UTF-8, padded to 4 (the last one too).
        private static bool TryReadStrings(ReadOnlySpan<byte> body, int count, out object value, out int size)
        {
            value = null!;
            size = 0;
            var builder = new SpreadBuilder<string>(count);
            for (int i = 0; i < count; i++)
            {
                if (body.Length - size < 4)
                    return false;
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(size));
                if (length > (uint)(body.Length - size - 4))
                    return false;
                builder.Add(Encoding.UTF8.GetString(body.Slice(size + 4, (int)length)));
                size += Align(4 + (int)length);
                if (size > body.Length)
                    return false;
            }
            value = builder.ToSpread();
            return true;
        }

        private static float F(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadSingleLittleEndian(p.Slice(offset));

        private static int Align(int n) => (n + (Protocol.CommandAlign - 1)) & ~(Protocol.CommandAlign - 1);
    }
}
