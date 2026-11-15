using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// Reusable byte buffer holding the commands of one window for one frame (format: see protocol.h).
    /// Usage per frame: <see cref="Reset"/>, write commands, <see cref="EndFrame"/> → span for vlqt_window_apply.
    /// The buffer only grows; writing does not allocate on the managed heap once it has reached its working size.
    /// Every command is little-endian and padded with zeros to <see cref="Protocol.CommandAlign"/> bytes;
    /// the header's payloadLen excludes that padding.
    /// Tracing: if the environment variable VLQT_TRACE_COMMANDS is set (non-empty, not "0"), <see cref="EndFrame"/>
    /// appends every frame buffer as <c>u32 length + bytes</c> to <c>%TEMP%/vlqt-trace-&lt;pid&gt;.bin</c>.
    /// Not thread-safe; one writer per window, used from the main thread.
    /// </summary>
    internal sealed class CommandWriter : ICommandSink
    {
        public const string TraceEnvVar = "VLQT_TRACE_COMMANDS";

        byte[] _buffer;
        int _length;
        int _count;

        public CommandWriter(int initialCapacity = 4096)
        {
            _buffer = new byte[Math.Max(initialCapacity, 64)];
        }

        /// <summary>Bytes written since the last <see cref="Reset"/>.</summary>
        public int Length => _length;

        /// <summary>Number of commands written since the last <see cref="Reset"/>.</summary>
        public int Count => _count;

        /// <summary>Current capacity of the internal buffer (grows, never shrinks).</summary>
        public int Capacity => _buffer.Length;

        /// <summary>The commands written since the last <see cref="Reset"/> (no side effects).</summary>
        public ReadOnlySpan<byte> Written => new ReadOnlySpan<byte>(_buffer, 0, _length);

        /// <summary>Clears the buffer for the next frame, keeping its capacity.</summary>
        public void Reset()
        {
            _length = 0;
            _count = 0;
        }

        /// <summary>Finishes the frame: traces the buffer if enabled and returns it for vlqt_window_apply.</summary>
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
            var p = Begin(Protocol.Op.Create, 6);
            WriteU32(p, id);
            WriteU16(p.Slice(4), type);
        }

        public void Destroy(uint id)
        {
            WriteU32(Begin(Protocol.Op.Destroy, 4), id);
        }

        /// <summary>RESET: destroys every object of the window at once (resync after a failed apply, #92).</summary>
        public void ResetScene()
        {
            Begin(Protocol.Op.Reset, 0);
        }

        public void Reparent(uint id, uint parent, uint index)
        {
            var p = Begin(Protocol.Op.Reparent, 12);
            WriteU32(p, id);
            WriteU32(p.Slice(4), parent);
            WriteU32(p.Slice(8), index);
        }

        /// <summary>BIND: sinkHandle 0 = unbind; flags = <see cref="Protocol.Bind"/> bit mask.</summary>
        public void Bind(uint id, uint sinkHandle, ushort flags)
        {
            var p = Begin(Protocol.Op.Bind, 10);
            WriteU32(p, id);
            WriteU32(p.Slice(4), sinkHandle);
            WriteU16(p.Slice(8), flags);
        }

        /// <summary>CLEAR_COLOR with straight alpha.</summary>
        public void ClearColor(float r, float g, float b, float a)
        {
            var p = Begin(Protocol.Op.ClearColor, 16);
            WriteF32x4(p, r, g, b, a);
        }

        public void ClearColor(in Color4 c) => ClearColor(c.R, c.G, c.B, c.A);

        /// <summary>SET_CLEAR (#117): false keeps the previous frame and draws the new one over it.</summary>
        public void SetClear(bool clear) => WriteU32(Begin(Protocol.Op.SetClear, 4), clear ? 1u : 0u);

        // --- SET, one method per value type ---

        public void SetF32(uint id, ushort prop, float value)
        {
            WriteF32(BeginSet(id, prop, Protocol.Vt.F32, 4), value);
        }

        public void SetI32(uint id, ushort prop, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(BeginSet(id, prop, Protocol.Vt.I32, 4), value);
        }

        public void SetBool(uint id, ushort prop, bool value)
        {
            BeginSet(id, prop, Protocol.Vt.Bool, 1)[0] = value ? (byte)1 : (byte)0;
        }

        /// <summary>Color with straight (non-premultiplied) alpha.</summary>
        public void SetColor(uint id, ushort prop, float r, float g, float b, float a)
        {
            WriteF32x4(BeginSet(id, prop, Protocol.Vt.Color, 16), r, g, b, a);
        }

        public void SetColor(uint id, ushort prop, in Color4 c) => SetColor(id, prop, c.R, c.G, c.B, c.A);

        public void SetRect(uint id, ushort prop, float x, float y, float width, float height)
        {
            WriteF32x4(BeginSet(id, prop, Protocol.Vt.Rectf, 16), x, y, width, height);
        }

        public void SetRect(uint id, ushort prop, in RectangleF r) => SetRect(id, prop, r.X, r.Y, r.Width, r.Height);

        /// <summary>4x4 matrix, 16 floats in row-major order (M11, M12, ..., M44 like Stride).</summary>
        public void SetMat4(uint id, ushort prop, ReadOnlySpan<float> rowMajor16)
        {
            if (rowMajor16.Length != 16)
                throw new ArgumentException("mat4 needs exactly 16 floats", nameof(rowMajor16));
            var p = BeginSet(id, prop, Protocol.Vt.Mat4, 64);
            for (int i = 0; i < 16; i++)
                WriteF32(p.Slice(i * 4), rowMajor16[i]);
        }

        /// <summary>
        /// Stride matrix, written as M11, M12, ..., M44 (row vectors, translation in M41..M43).
        /// Note: Stride stores Matrix column by column in memory, so it is written field by field, not reinterpreted.
        /// </summary>
        public void SetMat4(uint id, ushort prop, in Matrix m)
        {
            var p = BeginSet(id, prop, Protocol.Vt.Mat4, 64);
            WriteF32x4(p, m.M11, m.M12, m.M13, m.M14);
            WriteF32x4(p.Slice(16), m.M21, m.M22, m.M23, m.M24);
            WriteF32x4(p.Slice(32), m.M31, m.M32, m.M33, m.M34);
            WriteF32x4(p.Slice(48), m.M41, m.M42, m.M43, m.M44);
        }

        public void SetString(uint id, ushort prop, string? value) => SetString(id, prop, value.AsSpan());

        /// <summary>string: u32 byte length + UTF-8 (no terminator). Payload incl. SET fields must fit in u16.</summary>
        public void SetString(uint id, ushort prop, ReadOnlySpan<char> value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            var p = BeginSet(id, prop, Protocol.Vt.String, 4 + byteCount);
            WriteU32(p, (uint)byteCount);
            Encoding.UTF8.GetBytes(value, p.Slice(4));
        }

        /// <summary>ref: id of another element (e.g. ShaderEffect source), 0 = none/root.</summary>
        public void SetRef(uint id, ushort prop, uint refId)
        {
            WriteU32(BeginSet(id, prop, Protocol.Vt.Ref, 4), refId);
        }

        /// <summary>u64, e.g. an image resource id (Prop.Image, #361), 0 = none.</summary>
        public void SetU64(uint id, ushort prop, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(BeginSet(id, prop, Protocol.Vt.U64, 8), value);
        }

        /// <summary>ivec2, e.g. <see cref="Protocol.Prop.MeshResolution"/> (#437).</summary>
        public void SetVec2(uint id, ushort prop, float x, float y)
        {
            var p = BeginSet(id, prop, Protocol.Vt.Vec2, 8);
            WriteF32(p, x);
            WriteF32(p.Slice(4), y);
        }

        public void SetIvec2(uint id, ushort prop, int x, int y)
        {
            var p = BeginSet(id, prop, Protocol.Vt.Ivec2, 8);
            BinaryPrimitives.WriteInt32LittleEndian(p, x);
            BinaryPrimitives.WriteInt32LittleEndian(p.Slice(4), y);
        }

        // --- SET_STYLE (#292): u32 id, u16 count, u16 flags, entries (u16 field, u8 state, u8 valueType, value), each 4-byte aligned ---

        const int StyleFieldsSize = 8; // u32 id, u16 count, u16 flags
        const int StyleEntryHeaderSize = 4; // u16 field, u8 state, u8 valueType
        int _styleStart = -1; // offset of the open SET_STYLE command, -1 = none
        int _styleEntries;

        /// <summary>Starts a SET_STYLE; entries follow, <see cref="EndStyle"/> patches count and payload length.</summary>
        public void BeginStyle(uint id, ushort flags)
        {
            if (_styleStart >= 0)
                throw new InvalidOperationException("SET_STYLE already open, call EndStyle first.");
            _styleStart = _length;
            _styleEntries = 0;
            var p = Begin(Protocol.Op.SetStyle, StyleFieldsSize);
            WriteU32(p, id);
            WriteU16(p.Slice(4), 0);
            WriteU16(p.Slice(6), flags);
        }

        public void StyleColor(ushort field, byte state, float r, float g, float b, float a)
        {
            WriteF32x4(StyleEntry(field, state, Protocol.Vt.Color, 16), r, g, b, a);
        }

        public void StyleF32(ushort field, byte state, float value)
        {
            WriteF32(StyleEntry(field, state, Protocol.Vt.F32, 4), value);
        }

        public void StyleI32(ushort field, byte state, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(StyleEntry(field, state, Protocol.Vt.I32, 4), value);
        }

        public void StyleString(ushort field, byte state, ReadOnlySpan<char> value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            var p = StyleEntry(field, state, Protocol.Vt.String, 4 + byteCount);
            WriteU32(p, (uint)byteCount);
            Encoding.UTF8.GetBytes(value, p.Slice(4));
        }

        /// <summary>Closes the SET_STYLE: writes the entry count and the payload length (entries incl. their padding).</summary>
        public void EndStyle()
        {
            if (_styleStart < 0)
                throw new InvalidOperationException("EndStyle without BeginStyle.");
            int payload = _length - _styleStart - Protocol.HeaderSize;
            int start = _styleStart;
            _styleStart = -1;
            if (payload > MaxPayload || _styleEntries > ushort.MaxValue)
            {
                _length = start; // drop the half written command
                _count--;
                throw new InvalidOperationException($"SET_STYLE payload exceeds {MaxPayload} bytes.");
            }
            WriteU16(new Span<byte>(_buffer, start + 2, 2), (ushort)payload);
            WriteU16(new Span<byte>(_buffer, start + Protocol.HeaderSize + 4, 2), (ushort)_styleEntries);
        }

        /// <summary>Appends one 4-byte aligned entry (zero padded) to the open SET_STYLE and returns the value span.</summary>
        Span<byte> StyleEntry(ushort field, byte state, ushort valueType, int valueSize)
        {
            if (_styleStart < 0)
                throw new InvalidOperationException("Style entry without BeginStyle.");
            int total = Align(StyleEntryHeaderSize + valueSize);
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, field);
            e[2] = state;
            e[3] = (byte)valueType;
            _length += total;
            _styleEntries++;
            return e.Slice(StyleEntryHeaderSize, valueSize);
        }

        // --- SET_UNIFORMS (#437): u32 id, u16 count, u16 flags, entries (u16 valueType, u16 nameLen, name, pad 4, value, pad 4) ---

        const int UniformFieldsSize = 8; // u32 id, u16 count, u16 flags
        int _uniformsStart = -1; // offset of the open SET_UNIFORMS command, -1 = none
        int _uniformEntries;

        /// <summary>Starts a SET_UNIFORMS; entries follow, <see cref="EndUniforms"/> patches count and payload length.</summary>
        public void BeginUniforms(uint id, ushort flags) => BeginEntries(Protocol.Op.SetUniforms, id, flags);

        /// <summary>
        /// Starts a SET_QML_PROPERTIES (#104); the entries are encoded exactly like those of SET_UNIFORMS, so they are written
        /// with the Uniform* methods and the command is closed by <see cref="EndUniforms"/> (or <see cref="EndQmlProperties"/>).
        /// </summary>
        public void BeginQmlProperties(uint id, ushort flags) => BeginEntries(Protocol.Op.SetQmlProperties, id, flags);

        /// <summary>Closes the SET_QML_PROPERTIES started by <see cref="BeginQmlProperties"/> (entry count, payload length).</summary>
        public void EndQmlProperties() => EndUniforms();

        void BeginEntries(ushort op, uint id, ushort flags)
        {
            if (_uniformsStart >= 0)
                throw new InvalidOperationException("SET_UNIFORMS or SET_QML_PROPERTIES already open, call EndUniforms first.");
            _uniformsStart = _length;
            _uniformEntries = 0;
            var p = Begin(op, UniformFieldsSize);
            WriteU32(p, id);
            WriteU16(p.Slice(4), 0);
            WriteU16(p.Slice(6), flags);
        }

        public void UniformF32(ReadOnlySpan<char> name, float value) => WriteF32(UniformEntry(name, Protocol.Vt.F32, 4), value);

        public void UniformI32(ReadOnlySpan<char> name, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(UniformEntry(name, Protocol.Vt.I32, 4), value);

        public void UniformBool(ReadOnlySpan<char> name, bool value) => UniformEntry(name, Protocol.Vt.Bool, 1)[0] = value ? (byte)1 : (byte)0;

        public void UniformVec2(ReadOnlySpan<char> name, float x, float y)
        {
            var p = UniformEntry(name, Protocol.Vt.Vec2, 8);
            WriteF32(p, x);
            WriteF32(p.Slice(4), y);
        }

        public void UniformVec3(ReadOnlySpan<char> name, float x, float y, float z)
        {
            var p = UniformEntry(name, Protocol.Vt.Vec3, 12);
            WriteF32(p, x);
            WriteF32(p.Slice(4), y);
            WriteF32(p.Slice(8), z);
        }

        public void UniformVec4(ReadOnlySpan<char> name, float x, float y, float z, float w)
            => WriteF32x4(UniformEntry(name, Protocol.Vt.Vec4, 16), x, y, z, w);

        public void UniformColor(ReadOnlySpan<char> name, float r, float g, float b, float a)
            => WriteF32x4(UniformEntry(name, Protocol.Vt.Color, 16), r, g, b, a);

        /// <summary>Entry of value type rect (x, y, width, height) of an open SET_QML_PROPERTIES (#104).</summary>
        public void QmlRect(ReadOnlySpan<char> name, float x, float y, float width, float height)
            => WriteF32x4(UniformEntry(name, Protocol.Vt.Rectf, 16), x, y, width, height);

        /// <summary>Entry of value type string (u32 byte length + UTF-8) of an open SET_QML_PROPERTIES (#104).</summary>
        public void QmlString(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            var p = UniformEntry(name, Protocol.Vt.String, 4 + byteCount);
            WriteU32(p, (uint)byteCount);
            Encoding.UTF8.GetBytes(value, p.Slice(4));
        }

        public void UniformIvec2(ReadOnlySpan<char> name, int x, int y)
        {
            var p = UniformEntry(name, Protocol.Vt.Ivec2, 8);
            BinaryPrimitives.WriteInt32LittleEndian(p, x);
            BinaryPrimitives.WriteInt32LittleEndian(p.Slice(4), y);
        }

        public void UniformMat4(ReadOnlySpan<char> name, in Matrix m)
        {
            var p = UniformEntry(name, Protocol.Vt.Mat4, 64);
            WriteF32x4(p, m.M11, m.M12, m.M13, m.M14);
            WriteF32x4(p.Slice(16), m.M21, m.M22, m.M23, m.M24);
            WriteF32x4(p.Slice(32), m.M31, m.M32, m.M33, m.M34);
            WriteF32x4(p.Slice(48), m.M41, m.M42, m.M43, m.M44);
        }

        public void UniformRef(ReadOnlySpan<char> name, uint refId) => WriteU32(UniformEntry(name, Protocol.Vt.Ref, 4), refId);

        public void UniformU64(ReadOnlySpan<char> name, ulong value)
            => BinaryPrimitives.WriteUInt64LittleEndian(UniformEntry(name, Protocol.Vt.U64, 8), value);

        public void UniformSampler(ReadOnlySpan<char> name, ulong packed)
            => BinaryPrimitives.WriteUInt64LittleEndian(UniformEntry(name, Protocol.Vt.Sampler, 8), packed);

        /// <summary>Closes the SET_UNIFORMS: writes the entry count and the payload length (entries incl. their padding).</summary>
        public void EndUniforms()
        {
            if (_uniformsStart < 0)
                throw new InvalidOperationException("EndUniforms without BeginUniforms.");
            int payload = _length - _uniformsStart - Protocol.HeaderSize;
            int start = _uniformsStart;
            _uniformsStart = -1;
            if (payload > MaxPayload || _uniformEntries > ushort.MaxValue)
            {
                _length = start; // drop the half written command
                _count--;
                throw new InvalidOperationException($"SET_UNIFORMS or SET_QML_PROPERTIES payload exceeds {MaxPayload} bytes.");
            }
            WriteU16(new Span<byte>(_buffer, start + 2, 2), (ushort)payload);
            WriteU16(new Span<byte>(_buffer, start + Protocol.HeaderSize + 4, 2), (ushort)_uniformEntries);
        }

        /// <summary>
        /// Appends one entry (name and value each 4-byte aligned, zero padded) to the open SET_UNIFORMS or the open shader
        /// link of a SET_EFFECT_CHAIN and returns the value span.
        /// </summary>
        Span<byte> UniformEntry(ReadOnlySpan<char> name, ushort valueType, int valueSize)
        {
            var inLink = _uniformsStart < 0;
            if (inLink && _linkKind != Protocol.EffectLink.Shader)
                throw new InvalidOperationException("Uniform entry without BeginUniforms or BeginShaderLink.");
            int nameBytes = Encoding.UTF8.GetByteCount(name);
            if (nameBytes < 1 || nameBytes > Protocol.UniformNameMax)
                throw new ArgumentException($"Uniform name must be 1..{Protocol.UniformNameMax} UTF-8 bytes, got {nameBytes}.", nameof(name));
            int nameBlock = Align(4 + nameBytes);
            int total = nameBlock + Align(valueSize);
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, valueType);
            WriteU16(e.Slice(2), (ushort)nameBytes);
            Encoding.UTF8.GetBytes(name, e.Slice(4));
            _length += total;
            if (inLink)
                _linkEntries++;
            else
                _uniformEntries++;
            return e.Slice(nameBlock, valueSize);
        }

        // --- SET_EFFECT_CHAIN (#489): u32 id, u16 linkCount, u16 flags, links (u16 kind, u16 count, u32 bodyLen, body) ---

        const int ChainFieldsSize = 8; // u32 id, u16 linkCount, u16 flags
        const int LinkHeaderSize = 8; // u16 kind, u16 count, u32 bodyLen
        const int ShaderLinkFixedSize = 4 + 24; // u16 template, u16 shaderCount | 4 x f32 padding, f32 strength, u16 nameLen, u16 0
        int _chainStart = -1; // offset of the open SET_EFFECT_CHAIN command, -1 = none
        int _chainLinks;
        int _linkStart = -1; // offset of the open link header, -1 = none
        ushort _linkKind; // kind of the open link, 0 = none
        int _linkEntries;

        /// <summary>Starts a SET_EFFECT_CHAIN; links follow, <see cref="EndEffectChain"/> patches count and payload length.</summary>
        public void BeginEffectChain(uint id)
        {
            if (_chainStart >= 0)
                throw new InvalidOperationException("SET_EFFECT_CHAIN already open, call EndEffectChain first.");
            _chainStart = _length;
            _chainLinks = 0;
            var p = Begin(Protocol.Op.SetEffectChain, ChainFieldsSize);
            WriteU32(p, id);
            WriteU16(p.Slice(4), 0);
            WriteU16(p.Slice(6), 0);
        }

        /// <summary>Starts a MULTI link (one MultiEffect run); Effect* entries follow, then <see cref="EndLink"/>.</summary>
        public void BeginMultiLink() => BeginLink(Protocol.EffectLink.Multi, 0);

        public void EffectBool(ushort prop, bool value) => EffectEntry(prop, Protocol.Vt.Bool, 1)[0] = value ? (byte)1 : (byte)0;

        public void EffectF32(ushort prop, float value) => WriteF32(EffectEntry(prop, Protocol.Vt.F32, 4), value);

        public void EffectI32(ushort prop, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(EffectEntry(prop, Protocol.Vt.I32, 4), value);

        public void EffectColor(ushort prop, float r, float g, float b, float a)
            => WriteF32x4(EffectEntry(prop, Protocol.Vt.Color, 16), r, g, b, a);

        public void EffectRect(ushort prop, float x, float y, float width, float height)
            => WriteF32x4(EffectEntry(prop, Protocol.Vt.Rectf, 16), x, y, width, height);

        public void EffectRef(ushort prop, uint refId) => WriteU32(EffectEntry(prop, Protocol.Vt.Ref, 4), refId);

        /// <summary>
        /// Starts a SHADER link: template (<see cref="Protocol.EffectTemplate"/>), shader resource ids by role, padding in DIP,
        /// and strength (Pyramid: radius). Uniform* entries follow, then <see cref="EndLink"/>.
        /// </summary>
        public void BeginShaderLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop,
            float paddingRight, float paddingBottom, float strength)
        {
            int ids = shaderIds.Length * 8;
            var head = BeginLink(Protocol.EffectLink.Shader, ShaderLinkFixedSize + ids);
            WriteU16(head, template);
            WriteU16(head.Slice(2), (ushort)shaderIds.Length);
            for (int i = 0; i < shaderIds.Length; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(head.Slice(4 + i * 8), shaderIds[i]);
            var rest = head.Slice(4 + ids);
            WriteF32x4(rest, paddingLeft, paddingTop, paddingRight, paddingBottom);
            WriteF32(rest.Slice(16), strength);
            // Reserved (formerly the length of the strength uniform name): 0.
            WriteU16(rest.Slice(20), 0);
        }

        /// <summary>Closes the open link: writes its entry count and body length.</summary>
        public void EndLink()
        {
            if (_linkStart < 0)
                throw new InvalidOperationException("EndLink without BeginMultiLink or BeginShaderLink.");
            WriteU16(new Span<byte>(_buffer, _linkStart + 2, 2), (ushort)_linkEntries);
            WriteU32(new Span<byte>(_buffer, _linkStart + 4, 4), (uint)(_length - _linkStart - LinkHeaderSize));
            _linkStart = -1;
            _linkKind = 0;
            _chainLinks++;
        }

        /// <summary>Closes the SET_EFFECT_CHAIN: writes the link count and the payload length.</summary>
        public void EndEffectChain()
        {
            if (_chainStart < 0)
                throw new InvalidOperationException("EndEffectChain without BeginEffectChain.");
            if (_linkStart >= 0)
                throw new InvalidOperationException("Effect link still open, call EndLink first.");
            int payload = _length - _chainStart - Protocol.HeaderSize;
            int start = _chainStart;
            _chainStart = -1;
            if (payload > MaxPayload || _chainLinks > ushort.MaxValue)
            {
                _length = start; // drop the half written command
                _count--;
                throw new InvalidOperationException($"SET_EFFECT_CHAIN payload exceeds {MaxPayload} bytes.");
            }
            WriteU16(new Span<byte>(_buffer, start + 2, 2), (ushort)payload);
            WriteU16(new Span<byte>(_buffer, start + Protocol.HeaderSize + 4, 2), (ushort)_chainLinks);
        }

        /// <summary>Appends a link header plus a zeroed head of <paramref name="headSize"/> bytes (multiple of 4) and returns the head.</summary>
        Span<byte> BeginLink(ushort kind, int headSize)
        {
            if (_chainStart < 0)
                throw new InvalidOperationException("Effect link without BeginEffectChain.");
            if (_linkStart >= 0)
                throw new InvalidOperationException("Effect link still open, call EndLink first.");
            int total = LinkHeaderSize + headSize;
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, kind);
            _linkStart = _length;
            _linkKind = kind;
            _linkEntries = 0;
            _length += total;
            return e.Slice(LinkHeaderSize, headSize);
        }

        /// <summary>Appends one MULTI entry (u16 prop, u16 value type, value, zero padded to 4) and returns the value span.</summary>
        Span<byte> EffectEntry(ushort prop, ushort valueType, int valueSize)
        {
            if (_linkKind != Protocol.EffectLink.Multi)
                throw new InvalidOperationException("Effect entry without BeginMultiLink.");
            int total = 4 + Align(valueSize);
            EnsureCapacity(_length + total);
            var e = new Span<byte>(_buffer, _length, total);
            e.Clear();
            WriteU16(e, prop);
            WriteU16(e.Slice(2), valueType);
            _length += total;
            _linkEntries++;
            return e.Slice(4, valueSize);
        }

        // --- encoding ---

        /// <summary>Largest payload a single command can carry (u16 payloadLen).</summary>
        public const int MaxPayload = ushort.MaxValue;

        const int SetFieldsSize = 8; // u32 id, u16 prop, u16 valueType

        Span<byte> BeginSet(uint id, ushort prop, ushort valueType, int valueSize)
        {
            var p = Begin(Protocol.Op.Set, SetFieldsSize + valueSize);
            WriteU32(p, id);
            WriteU16(p.Slice(4), prop);
            WriteU16(p.Slice(6), valueType);
            return p.Slice(SetFieldsSize);
        }

        /// <summary>Writes the header, reserves payload + padding (zeroed) and returns the payload span.</summary>
        Span<byte> Begin(ushort op, int payloadLen)
        {
            if ((uint)payloadLen > MaxPayload)
                throw new ArgumentOutOfRangeException(nameof(payloadLen), payloadLen, $"Command payload exceeds {MaxPayload} bytes (op {op}).");

            int total = Align(Protocol.HeaderSize + payloadLen);
            EnsureCapacity(_length + total);

            var cmd = new Span<byte>(_buffer, _length, total);
            WriteU16(cmd, op);
            WriteU16(cmd.Slice(2), (ushort)payloadLen);
            cmd.Slice(Protocol.HeaderSize + payloadLen).Clear(); // padding
            _length += total;
            _count++;
            return cmd.Slice(Protocol.HeaderSize, payloadLen);
        }

        static int Align(int n) => (n + (Protocol.CommandAlign - 1)) & ~(Protocol.CommandAlign - 1);

        void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
                return;
            int newSize = Math.Max(required, _buffer.Length * 2);
            Array.Resize(ref _buffer, newSize);
        }

        static void WriteU16(Span<byte> s, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(s, v);
        static void WriteU32(Span<byte> s, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        static void WriteF32(Span<byte> s, float v) => BinaryPrimitives.WriteSingleLittleEndian(s, v);

        static void WriteF32x4(Span<byte> s, float a, float b, float c, float d)
        {
            WriteF32(s, a);
            WriteF32(s.Slice(4), b);
            WriteF32(s.Slice(8), c);
            WriteF32(s.Slice(12), d);
        }

        /// <summary>Process-wide trace file shared by all writers (frames of all windows in call order).</summary>
        static class Trace
        {
            public static readonly bool Enabled = IsEnabled(Environment.GetEnvironmentVariable(TraceEnvVar));
            static readonly object s_lock = new();
            static FileStream? s_stream;
            static bool s_failed;

            public static string FilePath => Path.Combine(Path.GetTempPath(), $"vlqt-trace-{Environment.ProcessId}.bin");

            static bool IsEnabled(string? v) => !string.IsNullOrEmpty(v) && v != "0";

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
                        QtApp.Logger?.LogWarning(e, "VL.Qt: command trace disabled, {Path} is not writable", FilePath);
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
