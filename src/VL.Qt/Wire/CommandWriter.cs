using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;

namespace VL.Qt.Wire
{
    /// <summary>
    /// Reusable byte buffer holding the commands of one window for one frame (format: see protocol.h).
    /// Usage per frame: <see cref="Clear"/>, write commands, <see cref="EndFrame"/> → span for vlqt_scene_apply.
    /// The buffer only grows; writing does not allocate on the managed heap once it has reached its working size.
    /// Every command is little-endian (header: u16 op, u16 flags 0, u32 payloadLen) and padded with zeros to
    /// <see cref="Protocol.CommandAlign"/> bytes; payloadLen excludes that padding.
    /// Tracing: if the environment variable VLQT_TRACE_COMMANDS is set (non-empty, not "0"), <see cref="EndFrame"/>
    /// appends every frame buffer as <c>u32 length + bytes</c> to <c>%TEMP%/vlqt-trace-&lt;pid&gt;.bin</c>.
    /// Compound commands (SET_STYLE, SET_UNIFORMS, SET_QML_PROPERTIES, SET_EFFECT_CHAIN with its links) are written between a
    /// <c>Begin*</c> and its <c>End*</c>; the writer knows which one is open (<see cref="Open"/>) and throws an
    /// <see cref="InvalidOperationException"/> for every call that does not belong there: a plain command or a second
    /// <c>Begin*</c> inside an open one, an entry of another command, an <c>End*</c> that does not match. A call that throws
    /// changes nothing; <see cref="Clear"/> and <see cref="Abort"/> leave an open command.
    /// Not thread-safe; one writer per window, used from the main thread.
    /// </summary>
    internal sealed class CommandWriter : ICommandSink
    {
        public const string TraceEnvVar = "VLQT_TRACE_COMMANDS";

        private byte[] _buffer;
        private int _length;
        private int _count;

        public CommandWriter(int initialCapacity = 4096)
        {
            _buffer = new byte[Math.Max(initialCapacity, 64)];
        }

        /// <summary>Bytes written since the last <see cref="Clear"/>.</summary>
        public int Length => _length;

        /// <summary>Number of commands written since the last <see cref="Clear"/>.</summary>
        public int Count => _count;

        /// <summary>Current capacity of the internal buffer (grows, never shrinks).</summary>
        public int Capacity => _buffer.Length;

        /// <summary>The commands written since the last <see cref="Clear"/> (no side effects).</summary>
        public ReadOnlySpan<byte> Written => new ReadOnlySpan<byte>(_buffer, 0, _length);

        /// <summary>Clears the buffer for the next frame, keeping its capacity. Also drops an open compound command.</summary>
        public void Clear()
        {
            _length = 0;
            _count = 0;
            _open = Open.None;
        }

        /// <summary>
        /// Drops the open compound command (and its open link) as if it had never been begun; no-op if none is open.
        /// For callers that cannot guarantee valid entries: <c>try { ... } catch { writer.Abort(); throw; }</c>.
        /// </summary>
        public void Abort()
        {
            if (_open == Open.None)
                return;
            _length = _openStart;
            _count--;
            _open = Open.None;
        }

        /// <summary>The compound command being written: which <c>Begin*</c> is open and which <c>End*</c> or entries are allowed.</summary>
        private enum Open
        {
            None,
            Style,
            Uniforms,
            QmlProperties,
            /// <summary>SET_EFFECT_CHAIN between its links.</summary>
            EffectChain,
            /// <summary>A MULTI link inside the SET_EFFECT_CHAIN.</summary>
            MultiLink,
            /// <summary>A SHADER link inside the SET_EFFECT_CHAIN.</summary>
            ShaderLink,
        }

        private Open _open;
        private int _openStart; // offset of the open command's header
        private int _openCount; // its entries (style, uniforms, QML properties) or closed links (effect chain)
        private int _linkStart; // offset of the open link's header (MultiLink, ShaderLink)
        private int _linkCount; // entries of the open link

        /// <summary>Finishes the frame: traces the buffer if enabled and returns it for vlqt_scene_apply.</summary>
        public ReadOnlySpan<byte> EndFrame()
        {
            var span = Written;
            if (Trace.Enabled)
                Trace.Write(span);
            return span;
        }

        // --- commands ---

        public void Create(uint id, ushort type)
        {
            var p = Begin(Protocol.Op.Create, Protocol.CreatePayload);
            WriteU32(p, id);
            WriteU16(p.Slice(4), type);
        }

        public void Destroy(uint id)
        {
            WriteU32(Begin(Protocol.Op.Destroy, 4), id);
        }

        /// <summary>RESET: destroys every object of the window at once (resync after a failed apply).</summary>
        public void ResetScene()
        {
            Begin(Protocol.Op.Reset, 0);
        }

        public void Reparent(uint id, uint parent, uint index)
        {
            var p = Begin(Protocol.Op.Reparent, Protocol.ReparentPayload);
            WriteU32(p, id);
            WriteU32(p.Slice(4), parent);
            WriteU32(p.Slice(8), index);
        }

        /// <summary>BIND: feedbackId 0 = unbind.</summary>
        public void Bind(uint id, uint feedbackId)
        {
            var p = Begin(Protocol.Op.Bind, Protocol.BindPayload);
            WriteU32(p, id);
            WriteU32(p.Slice(4), feedbackId);
        }

        /// <summary>CLEAR_COLOR with straight alpha.</summary>
        public void ClearColor(float r, float g, float b, float a)
        {
            var p = Begin(Protocol.Op.SetClearColor, 16);
            WriteF32x4(p, r, g, b, a);
        }

        public void ClearColor(in Color4 c) => ClearColor(c.R, c.G, c.B, c.A);

        /// <summary>SET_CLEAR: false keeps the previous frame and draws the new one over it.</summary>
        public void SetClear(bool clear) => WriteU32(Begin(Protocol.Op.SetClear, 4), clear ? 1u : 0u);

        /// <summary>SET_SPACE: the space of the scene; QtNative moves the origin of the window root accordingly.</summary>
        public void SetSpace(Space space) => WriteU32(Begin(Protocol.Op.SetSpace, 4), (uint)space);

        // --- SET, one method per value type ---

        public void SetF32(uint id, ushort prop, float value)
        {
            WriteF32(BeginSet(id, prop, Protocol.ValueType.F32, 4), value);
        }

        public void SetI32(uint id, ushort prop, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(BeginSet(id, prop, Protocol.ValueType.I32, 4), value);
        }

        public void SetBool(uint id, ushort prop, bool value)
        {
            BeginSet(id, prop, Protocol.ValueType.Bool, 1)[0] = value ? (byte)1 : (byte)0;
        }

        /// <summary>Color with straight (non-premultiplied) alpha.</summary>
        public void SetColor(uint id, ushort prop, float r, float g, float b, float a)
        {
            WriteF32x4(BeginSet(id, prop, Protocol.ValueType.Color, 16), r, g, b, a);
        }

        public void SetRect(uint id, ushort prop, float x, float y, float width, float height)
        {
            WriteF32x4(BeginSet(id, prop, Protocol.ValueType.Rectf, 16), x, y, width, height);
        }

        /// <summary>4x4 matrix, 16 floats in row-major order (M11, M12, ..., M44 like Stride).</summary>
        public void SetMat4(uint id, ushort prop, ReadOnlySpan<float> rowMajor16)
        {
            if (rowMajor16.Length != 16)
                throw new ArgumentException("mat4 needs exactly 16 floats", nameof(rowMajor16));
            var p = BeginSet(id, prop, Protocol.ValueType.Mat4, 64);
            for (int i = 0; i < 16; i++)
                WriteF32(p.Slice(i * 4), rowMajor16[i]);
        }

        /// <summary>
        /// Stride matrix, written as M11, M12, ..., M44 (row vectors, translation in M41..M43).
        /// Note: Stride stores Matrix column by column in memory, so it is written field by field, not reinterpreted.
        /// </summary>
        public void SetMat4(uint id, ushort prop, in Matrix m)
        {
            WriteMatrix(BeginSet(id, prop, Protocol.ValueType.Mat4, 64), m);
        }

        /// <summary>string: u32 byte length + UTF-8 (no terminator). Payload incl. SET fields must fit in <see cref="Protocol.MaxCommandPayload"/>.</summary>
        public void SetString(uint id, ushort prop, ReadOnlySpan<char> value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            var p = BeginSet(id, prop, Protocol.ValueType.String, 4 + byteCount);
            WriteU32(p, (uint)byteCount);
            Encoding.UTF8.GetBytes(value, p.Slice(4));
        }

        /// <summary>f32 array: u32 count + floats. Payload incl. SET fields must fit in <see cref="Protocol.MaxCommandPayload"/>.</summary>
        public void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values)
        {
            var p = BeginSet(id, prop, Protocol.ValueType.F32Array, 4 + 4 * values.Length);
            WriteU32(p, (uint)values.Length);
            for (int i = 0; i < values.Length; i++)
                WriteF32(p.Slice(4 + 4 * i), values[i]);
        }

        /// <summary>path: u32 verbCount, u32 pointCount, verbs padded with zeros to 4 bytes, points as x,y f32 pairs
        /// (<paramref name="points"/> holds 2 floats per point).</summary>
        public void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points)
        {
            if ((points.Length & 1) != 0)
                throw new ArgumentException("Path points are x,y pairs: the float count must be even.", nameof(points));
            int pointCount = points.Length / 2;
            int paddedVerbs = Align(verbs.Length);
            var p = BeginSet(id, prop, Protocol.ValueType.Path, 8 + paddedVerbs + 8 * pointCount);
            WriteU32(p, (uint)verbs.Length);
            WriteU32(p.Slice(4), (uint)pointCount);
            // The padding bytes are already zero (Begin clears the reserved area).
            verbs.CopyTo(p.Slice(8));
            var pts = p.Slice(8 + paddedVerbs);
            for (int i = 0; i < points.Length; i++)
                WriteF32(pts.Slice(4 * i), points[i]);
        }

        /// <summary>ref: id of another element (e.g. Shader source), 0 = none/root.</summary>
        public void SetRef(uint id, ushort prop, uint refId)
        {
            WriteU32(BeginSet(id, prop, Protocol.ValueType.Ref, 4), refId);
        }

        /// <summary>u64, e.g. an image resource id (PropertyId.Image), 0 = none.</summary>
        public void SetU64(uint id, ushort prop, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(BeginSet(id, prop, Protocol.ValueType.U64, 8), value);
        }

        /// <summary>ivec2, e.g. <see cref="Protocol.PropertyId.MeshResolution"/>.</summary>
        public void SetVec2(uint id, ushort prop, float x, float y)
        {
            var p = BeginSet(id, prop, Protocol.ValueType.Vec2, 8);
            WriteF32(p, x);
            WriteF32(p.Slice(4), y);
        }

        public void SetIvec2(uint id, ushort prop, int x, int y)
        {
            var p = BeginSet(id, prop, Protocol.ValueType.Ivec2, 8);
            BinaryPrimitives.WriteInt32LittleEndian(p, x);
            BinaryPrimitives.WriteInt32LittleEndian(p.Slice(4), y);
        }

        // --- SET_STYLE: u32 id, u16 count, u16 flags, entries (u16 field, u8 state, u8 valueType, value), each 4-byte aligned ---

        /// <summary>Starts a SET_STYLE; entries follow, <see cref="EndStyle"/> patches count and payload length.</summary>
        public void BeginStyle(uint id, ushort flags) => BeginList(Open.Style, Protocol.Op.SetStyle, id, flags);

        public void StyleColor(ushort field, byte state, float r, float g, float b, float a)
        {
            WriteF32x4(StyleEntry(field, state, Protocol.ValueType.Color, 16), r, g, b, a);
        }

        public void StyleF32(ushort field, byte state, float value)
        {
            WriteF32(StyleEntry(field, state, Protocol.ValueType.F32, 4), value);
        }

        public void StyleI32(ushort field, byte state, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(StyleEntry(field, state, Protocol.ValueType.I32, 4), value);
        }

        public void StyleString(ushort field, byte state, ReadOnlySpan<char> value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            var p = StyleEntry(field, state, Protocol.ValueType.String, 4 + byteCount);
            WriteU32(p, (uint)byteCount);
            Encoding.UTF8.GetBytes(value, p.Slice(4));
        }

        /// <summary>Closes the SET_STYLE: writes the entry count and the payload length (entries incl. their padding).</summary>
        public void EndStyle() => EndList(Open.Style, "SET_STYLE", "EndStyle", "BeginStyle");

        /// <summary>Appends one 4-byte aligned entry (zero padded) to the open SET_STYLE and returns the value span.</summary>
        private Span<byte> StyleEntry(ushort field, byte state, ushort valueType, int valueSize)
        {
            RequireOpen(Open.Style, "Style entry", "BeginStyle");
            int total = Align(Protocol.EntryHeaderSize + valueSize);
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, field);
            e[2] = state;
            e[3] = (byte)valueType;
            _length += total;
            _openCount++;
            return e.Slice(Protocol.EntryHeaderSize, valueSize);
        }

        // --- SET_UNIFORMS: u32 id, u16 count, u16 flags, entries (u16 valueType, u16 nameLen, name, pad 4, value, pad 4) ---

        /// <summary>Starts a SET_UNIFORMS; Uniform* entries follow, <see cref="EndUniforms"/> patches count and payload length.</summary>
        public void BeginUniforms(uint id, ushort flags) => BeginList(Open.Uniforms, Protocol.Op.SetUniforms, id, flags);

        public void UniformF32(ReadOnlySpan<char> name, float value) => WriteF32(UniformEntry(name, Protocol.ValueType.F32, 4), value);

        public void UniformI32(ReadOnlySpan<char> name, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(UniformEntry(name, Protocol.ValueType.I32, 4), value);

        public void UniformBool(ReadOnlySpan<char> name, bool value) => UniformEntry(name, Protocol.ValueType.Bool, 1)[0] = value ? (byte)1 : (byte)0;

        public void UniformVec2(ReadOnlySpan<char> name, float x, float y) => WriteF32x2(UniformEntry(name, Protocol.ValueType.Vec2, 8), x, y);

        public void UniformVec3(ReadOnlySpan<char> name, float x, float y, float z) => WriteF32x3(UniformEntry(name, Protocol.ValueType.Vec3, 12), x, y, z);

        public void UniformVec4(ReadOnlySpan<char> name, float x, float y, float z, float w)
            => WriteF32x4(UniformEntry(name, Protocol.ValueType.Vec4, 16), x, y, z, w);

        public void UniformColor(ReadOnlySpan<char> name, float r, float g, float b, float a)
            => WriteF32x4(UniformEntry(name, Protocol.ValueType.Color, 16), r, g, b, a);

        public void UniformIvec2(ReadOnlySpan<char> name, int x, int y) => WriteI32x2(UniformEntry(name, Protocol.ValueType.Ivec2, 8), x, y);

        public void UniformMat4(ReadOnlySpan<char> name, in Matrix m) => WriteMatrix(UniformEntry(name, Protocol.ValueType.Mat4, 64), m);

        public void UniformRef(ReadOnlySpan<char> name, uint refId) => WriteU32(UniformEntry(name, Protocol.ValueType.Ref, 4), refId);

        public void UniformU64(ReadOnlySpan<char> name, ulong value)
            => BinaryPrimitives.WriteUInt64LittleEndian(UniformEntry(name, Protocol.ValueType.U64, 8), value);

        public void UniformSampler(ReadOnlySpan<char> name, ulong packed)
            => BinaryPrimitives.WriteUInt64LittleEndian(UniformEntry(name, Protocol.ValueType.Sampler, 8), packed);

        /// <summary>Closes the SET_UNIFORMS: writes the entry count and the payload length (entries incl. their padding).</summary>
        public void EndUniforms() => EndList(Open.Uniforms, "SET_UNIFORMS", "EndUniforms", "BeginUniforms");

        /// <summary>Appends one entry to the open SET_UNIFORMS or to the open SHADER link of a SET_EFFECT_CHAIN and returns the value span.</summary>
        private Span<byte> UniformEntry(ReadOnlySpan<char> name, ushort valueType, int valueSize)
        {
            if (_open != Open.Uniforms && _open != Open.ShaderLink)
                throw new InvalidOperationException($"Uniform entry without BeginUniforms or BeginEffectChainLink (open: {_open}).");
            var value = NamedEntry(name, valueType, valueSize);
            if (_open == Open.ShaderLink)
                _linkCount++;
            else
                _openCount++;
            return value;
        }

        // --- SET_QML_PROPERTIES: the list of SET_UNIFORMS, but with strings and rects instead of refs, samplers and matrices ---

        /// <summary>Starts a SET_QML_PROPERTIES; QmlProperty* entries follow, <see cref="EndQmlProperties"/> patches count and payload length.</summary>
        public void BeginQmlProperties(uint id, ushort flags) => BeginList(Open.QmlProperties, Protocol.Op.SetQmlProperties, id, flags);

        public void QmlPropertyF32(ReadOnlySpan<char> name, float value) => WriteF32(QmlPropertyEntry(name, Protocol.ValueType.F32, 4), value);

        public void QmlPropertyI32(ReadOnlySpan<char> name, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(QmlPropertyEntry(name, Protocol.ValueType.I32, 4), value);

        public void QmlPropertyBool(ReadOnlySpan<char> name, bool value)
            => QmlPropertyEntry(name, Protocol.ValueType.Bool, 1)[0] = value ? (byte)1 : (byte)0;

        /// <summary>Color with straight alpha.</summary>
        public void QmlPropertyColor(ReadOnlySpan<char> name, float r, float g, float b, float a)
            => WriteF32x4(QmlPropertyEntry(name, Protocol.ValueType.Color, 16), r, g, b, a);

        public void QmlPropertyVec2(ReadOnlySpan<char> name, float x, float y) => WriteF32x2(QmlPropertyEntry(name, Protocol.ValueType.Vec2, 8), x, y);

        public void QmlPropertyVec3(ReadOnlySpan<char> name, float x, float y, float z)
            => WriteF32x3(QmlPropertyEntry(name, Protocol.ValueType.Vec3, 12), x, y, z);

        public void QmlPropertyVec4(ReadOnlySpan<char> name, float x, float y, float z, float w)
            => WriteF32x4(QmlPropertyEntry(name, Protocol.ValueType.Vec4, 16), x, y, z, w);

        public void QmlPropertyRect(ReadOnlySpan<char> name, float x, float y, float width, float height)
            => WriteF32x4(QmlPropertyEntry(name, Protocol.ValueType.Rectf, 16), x, y, width, height);

        /// <summary>string: u32 byte length + UTF-8.</summary>
        public void QmlPropertyString(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            var p = QmlPropertyEntry(name, Protocol.ValueType.String, 4 + byteCount);
            WriteU32(p, (uint)byteCount);
            Encoding.UTF8.GetBytes(value, p.Slice(4));
        }

        /// <summary>list: u16 element type, u16 0, u32 count, floats back to back (protocol.h "list").</summary>
        public void QmlPropertyListF32(ReadOnlySpan<char> name, ushort elementType, ReadOnlySpan<float> values)
        {
            int perElement = elementType switch
            {
                Protocol.ValueType.F32 => 1,
                Protocol.ValueType.Vec2 => 2,
                Protocol.ValueType.Vec3 => 3,
                Protocol.ValueType.Color or Protocol.ValueType.Rectf or Protocol.ValueType.Vec4 => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(elementType), elementType, "Not an element type made of floats."),
            };
            if (values.Length % perElement != 0)
                throw new ArgumentException($"{values.Length} floats are not a whole number of elements ({perElement} floats each).", nameof(values));
            int count = values.Length / perElement;
            var p = QmlPropertyList(name, elementType, count, values.Length * 4);
            for (int i = 0; i < values.Length; i++)
                WriteF32(p.Slice(i * 4), values[i]);
        }

        /// <summary>list of i32 (<see cref="QmlPropertyListF32"/>).</summary>
        public void QmlPropertyListI32(ReadOnlySpan<char> name, ReadOnlySpan<int> values)
        {
            var p = QmlPropertyList(name, Protocol.ValueType.I32, values.Length, values.Length * 4);
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteInt32LittleEndian(p.Slice(i * 4), values[i]);
        }

        /// <summary>list of bool, one byte each (<see cref="QmlPropertyListF32"/>).</summary>
        public void QmlPropertyListBool(ReadOnlySpan<char> name, ReadOnlySpan<bool> values)
        {
            var p = QmlPropertyList(name, Protocol.ValueType.Bool, values.Length, values.Length);
            for (int i = 0; i < values.Length; i++)
                p[i] = values[i] ? (byte)1 : (byte)0;
        }

        /// <summary>list of string: each u32 byte length + UTF-8, zero padded to 4 (<see cref="QmlPropertyListF32"/>).</summary>
        public void QmlPropertyListString(ReadOnlySpan<char> name, ReadOnlySpan<string> values)
        {
            int size = 0;
            foreach (var s in values)
                size += Align(4 + Encoding.UTF8.GetByteCount(s ?? string.Empty));
            var p = QmlPropertyList(name, Protocol.ValueType.String, values.Length, size);
            int offset = 0;
            foreach (var s in values)
            {
                var text = (s ?? string.Empty).AsSpan();
                int byteCount = Encoding.UTF8.GetByteCount(text);
                WriteU32(p.Slice(offset), (uint)byteCount);
                Encoding.UTF8.GetBytes(text, p.Slice(offset + 4));
                offset += Align(4 + byteCount);
            }
        }

        /// <summary>Appends a LIST entry with its header written and returns the span for the elements (<paramref name="elementBytes"/> long).</summary>
        private Span<byte> QmlPropertyList(ReadOnlySpan<char> name, ushort elementType, int count, int elementBytes)
        {
            if (count > Protocol.ListValueMaxCount)
                throw new ArgumentException($"A list can have at most {Protocol.ListValueMaxCount} elements, got {count}.", nameof(count));
            var p = QmlPropertyEntry(name, Protocol.ValueType.List, Protocol.ListValueHeaderSize + elementBytes);
            WriteU16(p, elementType);
            WriteU32(p.Slice(4), (uint)count);
            return p.Slice(Protocol.ListValueHeaderSize);
        }

        /// <summary>Closes the SET_QML_PROPERTIES started by <see cref="BeginQmlProperties"/> (entry count, payload length).</summary>
        public void EndQmlProperties() => EndList(Open.QmlProperties, "SET_QML_PROPERTIES", "EndQmlProperties", "BeginQmlProperties");

        /// <summary>Appends one entry to the open SET_QML_PROPERTIES and returns the value span.</summary>
        private Span<byte> QmlPropertyEntry(ReadOnlySpan<char> name, ushort valueType, int valueSize)
        {
            RequireOpen(Open.QmlProperties, "QML property entry", "BeginQmlProperties");
            var value = NamedEntry(name, valueType, valueSize);
            _openCount++;
            return value;
        }

        /// <summary>Appends one named entry (name and value each 4-byte aligned, zero padded) and returns the value span. The caller counts it.</summary>
        private Span<byte> NamedEntry(ReadOnlySpan<char> name, ushort valueType, int valueSize)
        {
            int nameBytes = Encoding.UTF8.GetByteCount(name);
            if (nameBytes < 1 || nameBytes > Protocol.UniformNameMax)
                throw new ArgumentException($"Name must be 1..{Protocol.UniformNameMax} UTF-8 bytes, got {nameBytes}.", nameof(name));
            int nameBlock = Align(Protocol.EntryHeaderSize + nameBytes);
            int total = nameBlock + Align(valueSize);
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, valueType);
            WriteU16(e.Slice(2), (ushort)nameBytes);
            Encoding.UTF8.GetBytes(name, e.Slice(4));
            _length += total;
            return e.Slice(nameBlock, valueSize);
        }

        // --- SET_EFFECT_CHAIN: u32 id, u16 linkCount, u16 flags, links (u16 kind, u16 count, u32 bodyLen, body) ---

        /// <summary>Starts a SET_EFFECT_CHAIN; links follow, <see cref="EndEffectChain"/> patches count and payload length.</summary>
        public void BeginEffectChain(uint id) => BeginList(Open.EffectChain, Protocol.Op.SetEffectChain, id, 0);

        /// <summary>Starts a MULTI link (one MultiEffect run); MultiEffect* entries follow, then <see cref="EndLink"/>.</summary>
        public void BeginMultiLink() => BeginLink(Open.MultiLink, Protocol.EffectChainLink.Multi, 0);

        public void MultiEffectBool(ushort prop, bool value) => MultiEffectEntry(prop, Protocol.ValueType.Bool, 1)[0] = value ? (byte)1 : (byte)0;

        public void MultiEffectF32(ushort prop, float value) => WriteF32(MultiEffectEntry(prop, Protocol.ValueType.F32, 4), value);

        public void MultiEffectI32(ushort prop, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(MultiEffectEntry(prop, Protocol.ValueType.I32, 4), value);

        public void MultiEffectColor(ushort prop, float r, float g, float b, float a)
            => WriteF32x4(MultiEffectEntry(prop, Protocol.ValueType.Color, 16), r, g, b, a);

        public void MultiEffectRect(ushort prop, float x, float y, float width, float height)
            => WriteF32x4(MultiEffectEntry(prop, Protocol.ValueType.Rectf, 16), x, y, width, height);

        public void MultiEffectRef(ushort prop, uint refId) => WriteU32(MultiEffectEntry(prop, Protocol.ValueType.Ref, 4), refId);

        /// <summary>
        /// Starts a SHADER link: template (<see cref="Protocol.EffectTemplate"/>), shader resource ids by role, padding in DIP,
        /// and strength (Pyramid: radius). Uniform* entries follow, then <see cref="EndLink"/>.
        /// </summary>
        public void BeginEffectChainLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop,
            float paddingRight, float paddingBottom, float strength)
        {
            int ids = shaderIds.Length * 8;
            var head = BeginLink(Open.ShaderLink, Protocol.EffectChainLink.Shader, 4 + ids + Protocol.EffectChainLinkShaderFixedSize);
            WriteU16(head, template);
            WriteU16(head.Slice(2), (ushort)shaderIds.Length);
            for (int i = 0; i < shaderIds.Length; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(head.Slice(4 + i * 8), shaderIds[i]);
            var rest = head.Slice(4 + ids);
            WriteF32x4(rest, paddingLeft, paddingTop, paddingRight, paddingBottom);
            WriteF32(rest.Slice(16), strength);
        }

        /// <summary>Closes the open link: writes its entry count and body length.</summary>
        public void EndLink()
        {
            if (_open != Open.MultiLink && _open != Open.ShaderLink)
                throw new InvalidOperationException($"EndLink without BeginMultiLink or BeginEffectChainLink (open: {_open}).");
            WriteU16(new Span<byte>(_buffer, _linkStart + 2, 2), (ushort)_linkCount);
            WriteU32(new Span<byte>(_buffer, _linkStart + 4, 4), (uint)(_length - _linkStart - Protocol.EffectChainLinkHeaderSize));
            _open = Open.EffectChain;
            _openCount++;
        }

        /// <summary>Closes the SET_EFFECT_CHAIN: writes the link count and the payload length.</summary>
        public void EndEffectChain()
        {
            if (_open == Open.MultiLink || _open == Open.ShaderLink)
                throw new InvalidOperationException("Effect link still open, call EndLink first.");
            EndList(Open.EffectChain, "SET_EFFECT_CHAIN", "EndEffectChain", "BeginEffectChain");
        }

        /// <summary>Appends a link header plus a zeroed head of <paramref name="headSize"/> bytes (multiple of 4) and returns the head.</summary>
        private Span<byte> BeginLink(Open link, ushort kind, int headSize)
        {
            if (_open == Open.MultiLink || _open == Open.ShaderLink)
                throw new InvalidOperationException("Effect link still open, call EndLink first.");
            RequireOpen(Open.EffectChain, "Effect link", "BeginEffectChain");
            int total = Protocol.EffectChainLinkHeaderSize + headSize;
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, kind);
            _open = link;
            _linkStart = _length;
            _linkCount = 0;
            _length += total;
            return e.Slice(Protocol.EffectChainLinkHeaderSize, headSize);
        }

        /// <summary>Appends one MULTI entry (u16 prop, u16 value type, value, zero padded to 4) and returns the value span.</summary>
        private Span<byte> MultiEffectEntry(ushort prop, ushort valueType, int valueSize)
        {
            RequireOpen(Open.MultiLink, "MultiEffect entry", "BeginMultiLink");
            int total = 4 + Align(valueSize);
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, prop);
            WriteU16(e.Slice(2), valueType);
            _length += total;
            _linkCount++;
            return e.Slice(4, valueSize);
        }

        // --- compound commands ---

        /// <summary>Writes the header of a compound command with the list fields (u32 id, u16 count 0, u16 flags) and opens it.</summary>
        private void BeginList(Open kind, ushort op, uint id, ushort flags)
        {
            int start = _length;
            var p = Begin(op, Protocol.ListFieldsSize); // throws if a compound command is open
            _open = kind;
            _openStart = start;
            _openCount = 0;
            WriteU32(p, id);
            WriteU16(p.Slice(4), 0);
            WriteU16(p.Slice(6), flags);
        }

        /// <summary>Closes the compound command <paramref name="kind"/>: patches the payload length and the count in the list fields.</summary>
        private void EndList(Open kind, string command, string end, string begin)
        {
            RequireOpen(kind, end, begin);
            int payload = _length - _openStart - Protocol.HeaderSize;
            if (payload > Protocol.MaxCommandPayload || _openCount > ushort.MaxValue)
            {
                Abort(); // drop the half written command
                throw new InvalidOperationException($"{command} payload exceeds {Protocol.MaxCommandPayload} bytes.");
            }
            WriteU32(new Span<byte>(_buffer, _openStart + 4, 4), (uint)payload);
            WriteU16(new Span<byte>(_buffer, _openStart + Protocol.HeaderSize + 4, 2), (ushort)_openCount);
            _open = Open.None;
        }

        private void RequireOpen(Open kind, string what, string begin)
        {
            if (_open != kind)
                throw new InvalidOperationException($"{what} without {begin} (open: {_open}).");
        }

        // --- encoding ---

        private Span<byte> BeginSet(uint id, ushort prop, ushort valueType, int valueSize)
        {
            var p = Begin(Protocol.Op.Set, Protocol.SetFieldsSize + valueSize);
            WriteU32(p, id);
            WriteU16(p.Slice(4), prop);
            WriteU16(p.Slice(6), valueType);
            return p.Slice(Protocol.SetFieldsSize);
        }

        /// <summary>Writes the header, reserves payload + padding (zeroed) and returns the payload span.</summary>
        private Span<byte> Begin(ushort op, int payloadLen)
        {
            if (_open != Open.None)
                throw new InvalidOperationException($"Command (op {op}) inside an open {_open} command, call its End* first.");
            if ((uint)payloadLen > Protocol.MaxCommandPayload)
                throw new ArgumentOutOfRangeException(nameof(payloadLen), payloadLen, $"Command payload exceeds {Protocol.MaxCommandPayload} bytes (op {op}).");

            int total = Align(Protocol.HeaderSize + payloadLen);
            EnsureCapacity(_length + total);

            var cmd = new Span<byte>(_buffer, _length, total);
            WriteU16(cmd, op);
            // Flags (reserved, 0): written explicitly, the buffer is reused and only the padding is cleared.
            WriteU16(cmd.Slice(2), 0);
            WriteU32(cmd.Slice(4), (uint)payloadLen);
            cmd.Slice(Protocol.HeaderSize + payloadLen).Clear(); // padding
            _length += total;
            _count++;
            return cmd.Slice(Protocol.HeaderSize, payloadLen);
        }

        private static int Align(int n) => (n + (Protocol.CommandAlign - 1)) & ~(Protocol.CommandAlign - 1);

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
                return;
            int newSize = Math.Max(required, _buffer.Length * 2);
            Array.Resize(ref _buffer, newSize);
        }

        private static void WriteU16(Span<byte> s, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(s, v);
        private static void WriteU32(Span<byte> s, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        private static void WriteF32(Span<byte> s, float v) => BinaryPrimitives.WriteSingleLittleEndian(s, v);

        private static void WriteF32x2(Span<byte> s, float a, float b)
        {
            WriteF32(s, a);
            WriteF32(s.Slice(4), b);
        }

        private static void WriteF32x3(Span<byte> s, float a, float b, float c)
        {
            WriteF32x2(s, a, b);
            WriteF32(s.Slice(8), c);
        }

        private static void WriteI32x2(Span<byte> s, int a, int b)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s, a);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(4), b);
        }

        private static void WriteMatrix(Span<byte> s, in Matrix m)
        {
            WriteF32x4(s, m.M11, m.M12, m.M13, m.M14);
            WriteF32x4(s.Slice(16), m.M21, m.M22, m.M23, m.M24);
            WriteF32x4(s.Slice(32), m.M31, m.M32, m.M33, m.M34);
            WriteF32x4(s.Slice(48), m.M41, m.M42, m.M43, m.M44);
        }

        private static void WriteF32x4(Span<byte> s, float a, float b, float c, float d)
        {
            WriteF32(s, a);
            WriteF32(s.Slice(4), b);
            WriteF32(s.Slice(8), c);
            WriteF32(s.Slice(12), d);
        }

        /// <summary>Process-wide trace file shared by all writers (frames of all windows in call order).</summary>
        private static class Trace
        {
            public static readonly bool Enabled = IsEnabled(Environment.GetEnvironmentVariable(TraceEnvVar));
            private static readonly object s_lock = new();
            private static FileStream? s_stream;
            private static bool s_failed;

            public static string FilePath => Path.Combine(Path.GetTempPath(), $"vlqt-trace-{Environment.ProcessId}.bin");

            private static bool IsEnabled(string? v) => !string.IsNullOrEmpty(v) && v != "0";

            public static void Write(ReadOnlySpan<byte> frame)
            {
                lock (s_lock)
                {
                    if (s_failed)
                        return;
                    try
                    {
                        s_stream ??= new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        Span<byte> prefix = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)frame.Length);
                        s_stream.Write(prefix);
                        s_stream.Write(frame);
                        s_stream.Flush(); // keep the trace usable after a crash
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        s_failed = true;
                        QtLog.Logger?.LogWarning(e, "VL.Qt: command trace disabled, {Path} is not writable", FilePath);
                    }
                }
            }
        }

        /// <summary>Path of this process' trace file (written only if tracing is enabled).</summary>
        public static string TraceFilePath => Trace.FilePath;

        /// <summary>Whether VLQT_TRACE_COMMANDS was set at startup.</summary>
        public static bool TraceEnabled => Trace.Enabled;
    }
}
