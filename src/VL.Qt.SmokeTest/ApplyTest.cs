using System;
using System.Collections.Generic;
using NUnit.Framework;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;
using static Pixels;

// Tests of vlqt_scene_apply / vlqt_scene_poll. Buffers are built by a tiny local
// encoder so this test does not depend on CommandWriter.
[TestFixture, NonParallelizable]
public static class ApplyTest
{
    private sealed class Buf
    {
        private byte[] _b = new byte[1 << 16];
        public int Length;
        private int _lastStart;
        public ReadOnlySpan<byte> Span => _b.AsSpan(0, Length);
        public Buf Clear() { Length = 0; return this; }

        private Span<byte> Begin(ushort op, int payloadLen)
        {
            var size = (Protocol.HeaderSize + payloadLen + 3) & ~3;
            if (Length + size > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, Length + size));
            var s = _b.AsSpan(Length, size);
            _lastStart = Length;
            s.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(s, op);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)payloadLen);
            Length += size;
            return s[Protocol.HeaderSize..];
        }

        /// <summary>Raw command with an arbitrary declared payloadLen (for error tests).</summary>
        public Buf Raw(ushort op, int declaredLen, int actualLen)
        {
            var s = Begin(op, actualLen);
            BinaryPrimitives.WriteUInt32LittleEndian(_b.AsSpan(Length - ((Protocol.HeaderSize + actualLen + 3) & ~3) + 4), (uint)declaredLen);
            return this;
        }
        /// <summary>Sets the reserved header flags of the last command (QtNative rejects anything but 0).</summary>
        public Buf Flags(ushort flags)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_b.AsSpan(_lastStart + 2), flags);
            return this;
        }
        /// <summary>Command with a hand-built payload (declared length = payload length).</summary>
        public Buf Cmd(ushort op, byte[] payload)
        {
            payload.CopyTo(Begin(op, payload.Length));
            return this;
        }
        public Buf Create(uint id, ushort type)
        {
            var s = Begin(Protocol.Op.Create, 6);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], type);
            return this;
        }
        public Buf Destroy(uint id) { BinaryPrimitives.WriteUInt32LittleEndian(Begin(Protocol.Op.Destroy, 4), id); return this; }
        public Buf Reparent(uint id, uint parent, uint index)
        {
            var s = Begin(Protocol.Op.Reparent, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], parent);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], index);
            return this;
        }
        public Buf Bind(uint id, uint feedbackId)
        {
            var s = Begin(Protocol.Op.Bind, Protocol.BindPayload);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], feedbackId);
            return this;
        }
        public Buf ClearColor(float r, float g, float b, float a = 1)
        {
            var s = Begin(Protocol.Op.SetClearColor, 16);
            WriteFloats(s, r, g, b, a);
            return this;
        }
        private Span<byte> SetHeader(uint id, ushort prop, ushort vt, int valueLen)
        {
            var s = Begin(Protocol.Op.Set, 8 + valueLen);
            BinaryPrimitives.WriteUInt32LittleEndian(s, id);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], prop);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], vt);
            return s[8..];
        }
        public Buf SetFloats(uint id, ushort prop, ushort vt, params float[] f) { WriteFloats(SetHeader(id, prop, vt, f.Length * 4), f); return this; }
        public Buf SetBool(uint id, ushort prop, bool b) { SetHeader(id, prop, Protocol.ValueType.Bool, 1)[0] = (byte)(b ? 1 : 0); return this; }
        public Buf SetRef(uint id, ushort prop, uint refId) { BinaryPrimitives.WriteUInt32LittleEndian(SetHeader(id, prop, Protocol.ValueType.Ref, 4), refId); return this; }
        public Buf SetI32(uint id, ushort prop, int v) { BinaryPrimitives.WriteInt32LittleEndian(SetHeader(id, prop, Protocol.ValueType.I32, 4), v); return this; }
        public Buf Size(uint id, float w, float h) => SetFloats(id, Protocol.PropertyId.Size, Protocol.ValueType.Vec2, w, h);
        // no position property any more: SIZE + ANCHOR TopLeft + TRANSFORM translation.
        public Buf Place(uint id, float x, float y, float w, float h) => Size(id, w, h).SetI32(id, Protocol.PropertyId.Anchor, GeometryExtensions.AlignTopLeft)
            .SetFloats(id, Protocol.PropertyId.Transform, Protocol.ValueType.Mat4, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, 0, 1);
        public Buf Color(uint id, float r, float g, float b, float a = 1) => SetFloats(id, Protocol.PropertyId.Color, Protocol.ValueType.Color, r, g, b, a);

        private static void WriteFloats(Span<byte> s, params float[] f)
        {
            for (int i = 0; i < f.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(s[(i * 4)..], f[i]);
        }
    }

    /// <summary>Little-endian payload writer for the entry-based commands (entries are written by hand, not by CommandWriter, so malformed
    /// ones are possible).</summary>
    private sealed class Pay
    {
        private readonly List<byte> _b = new();
        public Pay U16(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); return this; }
        public Pay U32(uint v) { U16((int)(v & 0xFFFF)); U16((int)(v >> 16)); return this; }
        public Pay U64(ulong v) { U32((uint)v); U32((uint)(v >> 32)); return this; }
        public Pay F32(float v) { var t = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(t, v); _b.AddRange(t); return this; }
        public Pay Zeros(int n) { _b.AddRange(new byte[n]); return this; }
        public Pay Pad() { while (_b.Count % 4 != 0) _b.Add(0); return this; }
        public Pay Bytes(params byte[] v) { _b.AddRange(v); return this; }
        public int Length => _b.Count;
        public byte[] ToArray() => _b.ToArray();

        /// <summary>One entry of SET_UNIFORMS / SET_QML_PROPERTIES / a SHADER link: u16 value type, u16 name length, name, pad, value, pad.</summary>
        public Pay Entry(ushort vt, string name, byte[] value, int? nameLen = null)
        {
            var n = System.Text.Encoding.UTF8.GetBytes(name);
            U16(vt).U16(nameLen ?? n.Length).Bytes(n).Pad().Bytes(value).Pad();
            return this;
        }
        public Pay F4Entry(ushort vt, string name, int floats) => Entry(vt, name, new byte[floats * 4]);

        /// <summary>A LIST value: u16 element type, u16 reserved (0), u32 count, then the elements as given (nothing is checked).</summary>
        public static byte[] ListValue(int elementType, uint count, byte[] elements, int reserved = 0)
            => new Pay().U16(elementType).U16(reserved).U32(count).Bytes(elements).ToArray();
        /// <summary>STRING elements of a list: u32 length, UTF-8, zero padding to 4, each.</summary>
        public static byte[] StringElements(params string[] values)
        {
            var p = new Pay();
            foreach (var v in values)
            {
                var utf8 = System.Text.Encoding.UTF8.GetBytes(v);
                p.U32((uint)utf8.Length).Bytes(utf8).Pad();
            }
            return p.ToArray();
        }

        /// <summary>SET_UNIFORMS / SET_QML_PROPERTIES head: u32 id, u16 count, u16 flags.</summary>
        public static Pay Head(uint id, int count, int flags = 0) => new Pay().U32(id).U16(count).U16(flags);
        /// <summary>SET_EFFECT_CHAIN head: u32 id, u16 link count, u16 reserved.</summary>
        public static Pay ChainHead(uint id, int links) => new Pay().U32(id).U16(links).U16(0);
        /// <summary>Link header + body: u16 kind, u16 count, u32 body length, body.</summary>
        public Pay Link(ushort kind, int count, Pay body, uint? bodyLen = null) => U16(kind).U16(count).U32(bodyLen ?? (uint)body.Length).Bytes(body.ToArray());
        /// <summary>SHADER link body head: u16 template, u16 shader count, shader ids, 16 bytes padding, f32 strength.</summary>
        public static Pay ShaderBody(ushort templ, int shaderCount)
        {
            var p = new Pay().U16(templ).U16(shaderCount);
            for (int i = 0; i < shaderCount; i++) p.U64(0);
            return p.Zeros(16).F32(0);
        }
        /// <summary>SET of a Shape path: u32 id, u16 prop, u16 value type, u32 verbCount, u32 pointCount, verbs (padded), points.</summary>
        public static Pay PathSet(uint id, byte[] verbs, uint pointCount)
        {
            var p = new Pay().U32(id).U16(Protocol.PropertyId.ShapePath).U16(Protocol.ValueType.Path).U32((uint)verbs.Length).U32(pointCount).Bytes(verbs).Pad();
            return p.Zeros((int)pointCount * 8);
        }
    }

    // Rejection branches of the entry-based commands and of path values: each case asserts the return code and a part
    // of the reason. A malformed command changes nothing, so the elements stay valid across the cases.
    private static void RejectCases(Scenario s, SceneHandle w, Buf b)
    {
        const uint fx = 3000, qml = 3001, shape = 3002, rect = 3003;
        var rc = Native.Apply(w, b.Clear().Create(fx, Protocol.ElementType.Shader).Create(qml, Protocol.ElementType.Qml)
            .Create(shape, Protocol.ElementType.Shape).Create(rect, Protocol.ElementType.Rectangle).Span);
        s.Check(rc == 0, $"reject cases: create elements ({rc}) {(rc != 0 ? Native.LastError() : "")}");

        void Case(string what, ushort op, Pay p, Native.Result expected, string mustContain)
        {
            var r = Native.Apply(w, b.Clear().Cmd(op, p.ToArray()).Span);
            var e = Native.LastError();
            var got = Native.GetApplyError(w, out var ae);
            var ok = r == expected && (expected == Native.Result.Ok || (e.Contains(mustContain) && got == 0 && ae.Rc == (int)expected && ae.Reason.Contains(mustContain)));
            s.Check(ok, $"reject {what} -> {r} \"{(expected == 0 ? "" : e)}\"");
        }
        const ushort uni = Protocol.Op.SetUniforms, qop = Protocol.Op.SetQmlProperties, chain = Protocol.Op.SetEffectChain;
        const ushort F = Protocol.ValueType.F32, V4 = Protocol.ValueType.Vec4, Str = Protocol.ValueType.String;
        var name256 = new string('n', 256);

        // SET_UNIFORMS
        Case("uniforms: valid entry (control)", uni, Pay.Head(fx, 1).F4Entry(F, "a", 1), 0, "");
        Case("uniforms: payload < 8", uni, new Pay().U32(fx).U16(0), Native.Result.BadLength, "too short");
        Case("uniforms: name length 0", uni, Pay.Head(fx, 1).Entry(F, "a", new byte[4], nameLen: 0), Native.Result.BadArgument, "name length 0");
        Case("uniforms: name length 256", uni, Pay.Head(fx, 1).Entry(F, name256, new byte[4]), Native.Result.BadArgument, "name length 256");
        Case("uniforms: entry past the payload", uni, Pay.Head(fx, 1).U16(V4).U16(1).Bytes((byte)'a', 0, 0, 0).Zeros(4), Native.Result.BadLength, "exceeds the payload");
        Case("uniforms: count larger than the entries", uni, Pay.Head(fx, 2).F4Entry(F, "a", 1), Native.Result.BadLength, "truncated entry");
        Case("uniforms: trailing bytes", uni, Pay.Head(fx, 1).F4Entry(F, "a", 1).Zeros(4), Native.Result.BadLength, "bytes after the last entry");
        Case("uniforms: STRING not allowed", uni, Pay.Head(fx, 1).Entry(Str, "a", new byte[4]), Native.Result.BadArgument, "not allowed");
        Case("uniforms: unknown value type", uni, Pay.Head(fx, 1).Entry(999, "a", new byte[4]), Native.Result.BadArgument, "not allowed");
        Case("uniforms: sampler out of range", uni, Pay.Head(fx, 1).Entry(Protocol.ValueType.Sampler, "a", new byte[] { 9, 0, 0, 0, 0, 0, 0, 0 }), Native.Result.BadArgument, "sampler state");
        Case("uniforms: unknown ref", uni, Pay.Head(fx, 1).Entry(Protocol.ValueType.Ref, "a", new byte[] { 0x39, 5, 0, 0 }), Native.Result.UnknownObject, "unknown ref id 1337");
        Case("uniforms: unknown id", uni, Pay.Head(9999, 0), Native.Result.UnknownObject, "unknown id 9999");
        Case("uniforms: not a Shader", uni, Pay.Head(rect, 0), Native.Result.UnknownProperty, "ShaderEffect only");

        // SET_QML_PROPERTIES
        Case("qml: valid entries (control)", qop, Pay.Head(qml, 2).F4Entry(F, "a", 1).Entry(Str, "s", new Pay().U32(2).Bytes((byte)'h', (byte)'i').ToArray()), 0, "");
        Case("qml: payload < 8", qop, new Pay().U32(qml).U16(0), Native.Result.BadLength, "too short");
        Case("qml: name length 0", qop, Pay.Head(qml, 1).Entry(F, "a", new byte[4], nameLen: 0), Native.Result.BadArgument, "name length 0");
        Case("qml: name length 256", qop, Pay.Head(qml, 1).Entry(F, name256, new byte[4]), Native.Result.BadArgument, "name length 256");
        Case("qml: entry past the payload", qop, Pay.Head(qml, 1).U16(V4).U16(1).Bytes((byte)'a', 0, 0, 0).Zeros(4), Native.Result.BadLength, "exceeds the payload");
        Case("qml: string length past the payload", qop, Pay.Head(qml, 1).Entry(Str, "s", new Pay().U32(1000).Bytes(1, 2, 3, 4).ToArray()), Native.Result.BadLength, "exceeds the payload");
        Case("qml: string without its length field", qop, Pay.Head(qml, 1).U16(Str).U16(1).Bytes((byte)'s', 0, 0, 0), Native.Result.BadLength, "exceeds the payload");
        Case("qml: count larger than the entries", qop, Pay.Head(qml, 2).F4Entry(F, "a", 1), Native.Result.BadLength, "truncated entry");
        Case("qml: trailing bytes", qop, Pay.Head(qml, 1).F4Entry(F, "a", 1).Zeros(4), Native.Result.BadLength, "bytes after the last entry");
        Case("qml: unknown value type", qop, Pay.Head(qml, 1).Entry(999, "a", new byte[4]), Native.Result.BadArgument, "unknown value type");
        Case("qml: PATH not allowed", qop, Pay.Head(qml, 1).Entry(Protocol.ValueType.Path, "a", new byte[8]), Native.Result.BadArgument, "not allowed");
        Case("qml: not a QmlElement", qop, Pay.Head(rect, 0), Native.Result.UnknownProperty, "QmlElement only");

        // LIST values (protocol.h "Values"): allowed in SET_QML_PROPERTIES only, checked as a whole before anything is used.
        const ushort List = Protocol.ValueType.List;
        var listMax = (uint)Protocol.ListValueMaxCount;
        foreach (var (typeName, elemType, size) in new[] { ("F32", Protocol.ValueType.F32, 4), ("I32", Protocol.ValueType.I32, 4), ("BOOL", Protocol.ValueType.Bool, 1),
                     ("COLOR", Protocol.ValueType.Color, 16), ("RECTF", Protocol.ValueType.Rectf, 16), ("VEC2", Protocol.ValueType.Vec2, 8),
                     ("VEC3", Protocol.ValueType.Vec3, 12), ("VEC4", Protocol.ValueType.Vec4, 16) })
        {
            Case($"qml list: {typeName} elements (control)", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(elemType, 5, new byte[5 * size])), 0, "");
            Case($"qml list: empty {typeName} list (control)", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(elemType, 0, Array.Empty<byte>())), 0, "");
        }
        Case("qml list: STRING elements of every padding (control)", qop,
            Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Str, 6, Pay.StringElements("", "a", "ab", "abc", "abcd", "hä€😀"))), 0, "");
        Case("qml list: empty STRING list (control)", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Str, 0, Array.Empty<byte>())), 0, "");
        Case("qml list: a list and a real in one command (control)", qop,
            Pay.Head(qml, 3).Entry(List, "l", Pay.ListValue(F, 1, new byte[4])).F4Entry(F, "a", 1).Entry(List, "m", Pay.ListValue(Protocol.ValueType.Bool, 3, new byte[3])), 0, "");
        Case("qml list: exactly 65536 elements are accepted", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(F, listMax, new byte[listMax * 4])), 0, "");
        Case("qml list: LIST elements (a list in a list)", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(List, 1, new byte[4])), Native.Result.BadArgument, "element type 12 not allowed");
        Case("qml list: element type 0", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(0, 1, new byte[4])), Native.Result.BadArgument, "element type 0 not allowed");
        Case("qml list: unknown element type", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(999, 1, new byte[4])), Native.Result.BadArgument, "element type 999 not allowed");
        Case("qml list: MAT4 elements", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Protocol.ValueType.Mat4, 1, new byte[64])), Native.Result.BadArgument, "element type 6 not allowed");
        Case("qml list: REF elements", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Protocol.ValueType.Ref, 1, new byte[4])), Native.Result.BadArgument, "element type 8 not allowed");
        Case("qml list: F32_ARRAY elements", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Protocol.ValueType.F32Array, 1, new byte[8])), Native.Result.BadArgument, "element type 10 not allowed");
        Case("qml list: reserved field not 0", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(F, 1, new byte[4], reserved: 1)), Native.Result.BadArgument, "reserved field is 1");
        Case("qml list: 65537 elements", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(F, listMax + 1, new byte[(listMax + 1) * 4])), Native.Result.BadArgument, "more than 65536");
        Case("qml list: count 0xFFFFFFFF without elements", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(F, 0xFFFFFFFF, Array.Empty<byte>())), Native.Result.BadArgument, "more than 65536");
        Case("qml list: elements past the entry", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(F, 3, new byte[8])), Native.Result.BadLength, "3 elements exceed the entry");
        Case("qml list: VEC4 elements past the entry", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Protocol.ValueType.Vec4, 2, new byte[16])), Native.Result.BadLength, "2 elements exceed the entry");
        Case("qml list: 65536 elements, one missing", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(F, listMax, new byte[(listMax - 1) * 4])), Native.Result.BadLength, "65536 elements exceed the entry");
        Case("qml list: truncated header", qop, Pay.Head(qml, 1).Entry(List, "l", new byte[4]), Native.Result.BadLength, "truncated header");
        Case("qml list: no value at all", qop, Pay.Head(qml, 1).U16(List).U16(1).Bytes((byte)'l', 0, 0, 0), Native.Result.BadLength, "truncated header");
        Case("qml list: a string element cut off", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Str, 1, new Pay().U32(100).Bytes(1, 2, 3, 4).ToArray())), Native.Result.BadLength, "exceeds the entry");
        Case("qml list: a string element without its length", qop,
            Pay.Head(qml, 1).U16(List).U16(1).Bytes((byte)'l', 0, 0, 0).Bytes(Pay.ListValue(Str, 1, new byte[2])), Native.Result.BadLength, "exceeds the entry");
        Case("qml list: second string missing", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Str, 2, Pay.StringElements("ab"))), Native.Result.BadLength, "exceeds the entry");
        Case("qml list: padding of the last string cut off", qop,
            Pay.Head(qml, 1).U16(List).U16(1).Bytes((byte)'l', 0, 0, 0).Bytes(Pay.ListValue(Str, 1, new Pay().U32(1).Bytes((byte)'a').ToArray())), Native.Result.BadLength, "exceeds the entry");
        Case("qml list: string count past the elements", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(Str, listMax, Pay.StringElements("a"))), Native.Result.BadLength, "exceeds the entry");
        Case("qml list: trailing bytes after the entry", qop, Pay.Head(qml, 1).Entry(List, "l", Pay.ListValue(F, 1, new byte[4])).Zeros(4), Native.Result.BadLength, "bytes after the last entry");
        // A LIST goes nowhere else.
        Case("set: LIST value refused", Protocol.Op.Set, new Pay().U32(shape).U16(Protocol.PropertyId.ShapePath).U16(List).Bytes(Pay.ListValue(F, 1, new byte[4])), Native.Result.BadArgument, "only goes into SET_QML_PROPERTIES");
        Case("uniforms: LIST not allowed", uni, Pay.Head(fx, 1).Entry(List, "a", Pay.ListValue(F, 1, new byte[4])), Native.Result.BadArgument, "not allowed");
        Case("chain: SHADER uniform LIST not allowed", chain,
            Pay.ChainHead(rect, 1).Link(Protocol.EffectChainLink.Shader, 1, Pay.ShaderBody(Protocol.EffectTemplate.Single, 1).Entry(List, "a", Pay.ListValue(F, 1, new byte[4]))), Native.Result.BadArgument, "not allowed");
        Case("chain: MULTI entry LIST not allowed", chain,
            Pay.ChainHead(rect, 1).Link(Protocol.EffectChainLink.Multi, 1, new Pay().U16(Protocol.MultiEffectKey.BlurEnabled).U16(List).Bytes(Pay.ListValue(F, 1, new byte[4]))), Native.Result.BadArgument, "takes");

        // SET_EFFECT_CHAIN
        const ushort multi = Protocol.EffectChainLink.Multi, shader = Protocol.EffectChainLink.Shader, single = Protocol.EffectTemplate.Single;
        Case("chain: empty chain (control)", chain, Pay.ChainHead(rect, 0), 0, "");
        Case("chain: SHADER link (control)", chain, Pay.ChainHead(rect, 1).Link(shader, 0, Pay.ShaderBody(single, 1)), 0, "");
        Case("chain: payload < 8", chain, new Pay().U32(rect).U16(0), Native.Result.BadLength, "too short");
        Case("chain: unknown id", chain, Pay.ChainHead(9999, 0), Native.Result.UnknownObject, "unknown id 9999");
        Case("chain: truncated link header", chain, Pay.ChainHead(rect, 1).U32(0), Native.Result.BadLength, "truncated link header");
        Case("chain: misaligned bodyLen", chain, Pay.ChainHead(rect, 1).Link(multi, 0, new Pay().Zeros(4), bodyLen: 2), Native.Result.BadLength, "not aligned");
        Case("chain: bodyLen past the payload", chain, Pay.ChainHead(rect, 1).Link(multi, 0, new Pay(), bodyLen: 64), Native.Result.BadLength, "exceeds the payload");
        Case("chain: unknown link kind", chain, Pay.ChainHead(rect, 1).Link(3, 0, new Pay()), Native.Result.BadArgument, "unknown kind 3");
        Case("chain: unknown template", chain, Pay.ChainHead(rect, 1).Link(shader, 0, Pay.ShaderBody(9, 1)), Native.Result.BadArgument, "unknown template 9");
        Case("chain: SINGLE with 2 shaders", chain, Pay.ChainHead(rect, 1).Link(shader, 0, Pay.ShaderBody(single, 2)), Native.Result.BadArgument, "takes 1 shader id");
        Case("chain: PYRAMID with 1 shader", chain, Pay.ChainHead(rect, 1).Link(shader, 0, Pay.ShaderBody(Protocol.EffectTemplate.Pyramid, 1)), Native.Result.BadArgument, "takes 2 shader id");
        Case("chain: truncated SHADER head", chain, Pay.ChainHead(rect, 1).Link(shader, 0, new Pay().U16(single).U16(1).Zeros(8)), Native.Result.BadLength, "truncated SHADER head");
        Case("chain: shader uniform name length 0", chain, Pay.ChainHead(rect, 1).Link(shader, 1, Pay.ShaderBody(single, 1).Entry(F, "a", new byte[4], nameLen: 0)), Native.Result.BadArgument, "name length 0");
        Case("chain: shader uniform name length 256", chain, Pay.ChainHead(rect, 1).Link(shader, 1, Pay.ShaderBody(single, 1).Entry(F, name256, new byte[4])), Native.Result.BadArgument, "name length 256");
        Case("chain: shader uniform past the body", chain, Pay.ChainHead(rect, 1).Link(shader, 1, Pay.ShaderBody(single, 1).U16(V4).U16(1).Bytes((byte)'a', 0, 0, 0).Zeros(4)), Native.Result.BadLength, "exceeds the body");
        Case("chain: shader uniform count larger", chain, Pay.ChainHead(rect, 1).Link(shader, 2, Pay.ShaderBody(single, 1).F4Entry(F, "a", 1)), Native.Result.BadLength, "truncated entry");
        Case("chain: shader uniform STRING not allowed", chain, Pay.ChainHead(rect, 1).Link(shader, 1, Pay.ShaderBody(single, 1).Entry(Str, "a", new byte[4])), Native.Result.BadArgument, "not allowed");
        Case("chain: trailing bytes in a body", chain, Pay.ChainHead(rect, 1).Link(shader, 0, Pay.ShaderBody(single, 1).Zeros(4)), Native.Result.BadLength, "bytes after the last entry of the body");
        Case("chain: trailing bytes after the last link", chain, Pay.ChainHead(rect, 1).Link(shader, 0, Pay.ShaderBody(single, 1)).Zeros(4), Native.Result.BadLength, "bytes after the last link");
        Case("chain: link count larger than the links", chain, Pay.ChainHead(rect, 2).Link(shader, 0, Pay.ShaderBody(single, 1)), Native.Result.BadLength, "truncated link header");
        Case("chain: MULTI truncated entry", chain, Pay.ChainHead(rect, 1).Link(multi, 1, new Pay()), Native.Result.BadLength, "truncated entry");
        Case("chain: MULTI unknown key", chain, Pay.ChainHead(rect, 1).Link(multi, 1, new Pay().U16(1).U16(F).F32(0)), Native.Result.UnknownProperty, "is no MULTI key");
        Case("chain: MULTI wrong value type", chain, Pay.ChainHead(rect, 1).Link(multi, 1, new Pay().U16(Protocol.MultiEffectKey.BlurEnabled).U16(F).F32(0)), Native.Result.BadArgument, "takes");
        Case("chain: MULTI entry past the body", chain, Pay.ChainHead(rect, 1).Link(multi, 1, new Pay().U16(Protocol.MultiEffectKey.BlurEnabled).U16(Protocol.ValueType.Bool)), Native.Result.BadLength, "exceeds the body");
        Case("chain: MULTI trailing bytes in the body", chain, Pay.ChainHead(rect, 1).Link(multi, 0, new Pay().Zeros(4)), Native.Result.BadLength, "bytes after the last entry of the body");

        // Path values (SET of ShapePath)
        const ushort set = Protocol.Op.Set;
        Case("path: valid (control)", set, Pay.PathSet(shape, new[] { Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Close }, 2), 0, "");
        Case("path: unknown verb", set, Pay.PathSet(shape, new byte[] { Protocol.Verb.Move, 9 }, 2), Native.Result.BadArgument, "unknown verb 9");
        Case("path: too few points", set, Pay.PathSet(shape, new[] { Protocol.Verb.Move, Protocol.Verb.Cubic }, 2), Native.Result.BadArgument, "the verbs take 4");
        Case("path: too many points", set, Pay.PathSet(shape, new[] { Protocol.Verb.Move, Protocol.Verb.Line }, 3), Native.Result.BadArgument, "the verbs take 2");
        Case("path: counts past the payload", set, new Pay().U32(shape).U16(Protocol.PropertyId.ShapePath).U16(Protocol.ValueType.Path).U32(0xFFFFFFFF).U32(0xFFFFFFFF), Native.Result.BadLength, "value length");
        Case("string: length past the payload", set, new Pay().U32(shape).U16(Protocol.PropertyId.ShapePath).U16(Protocol.ValueType.String).U32(0xFFFFFFFF).Bytes(1, 2, 3, 4), Native.Result.BadLength, "value length");
        Case("string: without its length field", set, new Pay().U32(shape).U16(Protocol.PropertyId.ShapePath).U16(Protocol.ValueType.String).Bytes(1, 2), Native.Result.BadLength, "value length");
        Case("f32 array: count past the payload", set, new Pay().U32(shape).U16(Protocol.PropertyId.ShapePath).U16(Protocol.ValueType.F32Array).U32(0x40000000).Zeros(8), Native.Result.BadLength, "value length");

        // Rejected commands changed nothing: the elements still take a valid command.
        rc = Native.Apply(w, b.Clear().Cmd(uni, Pay.Head(fx, 1).F4Entry(F, "a", 1).ToArray()).Span);
        s.Check(rc == 0, $"reject cases: element still valid ({rc}) {(rc != 0 ? Native.LastError() : "")}");
    }

    // Grabs are in device pixels (HiDPI); test coordinates are logical (800x600).


    /// <summary>Apply: tree commands, ordering, errors and the cost of 10000 SET.</summary>
    [Test, Category("apply")]
    public static void Apply() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var w = NativeCalls.CreateWindow(800, 600, "VL.Qt apply test");
        s.Check(w != SceneHandle.Null, "apply: window_create");
        Native.vlqt_window_show(w);
        var b = new Buf();
        Native.Result Apply(Buf buf) => Native.Apply(w, buf.Span);

        // Scene: grey background, red rect at root, green rect inside an empty GROUP.
        var rc = Apply(b.Clear()
            .ClearColor(0.125f, 0.125f, 0.125f)
            .Create(1, Protocol.ElementType.Rectangle).Place(1, 100, 100, 200, 150).Color(1, 1, 0, 0).Reparent(1, 0, 0)
            .Create(2, Protocol.ElementType.Group).Place(2, 400, 300, 300, 200).Reparent(2, 0, 1)
            .Create(3, Protocol.ElementType.Rectangle).Place(3, 0, 0, 100, 100).Color(3, 0, 1, 0).Reparent(3, 2, 0)
            .Create(4, Protocol.ElementType.Rectangle).Place(4, 150, 120, 100, 100).Color(4, 0, 0, 1).Reparent(4, 0, 0)
            .SetFloats(1, Protocol.PropertyId.Opacity, Protocol.ValueType.F32, 1f).SetBool(2, Protocol.PropertyId.Clip, false)
            .Bind(1, 42));
        s.Check(rc == 0, $"apply: build scene ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(5);
        using (var a = s.Grab(w, "apply1.png"))
        {
            s.Check(Near(Px(a, 200, 175), 255, 0, 0), $"apply: red rect on top of blue (index 0) ({Px(a, 200, 175)})");
            s.Check(Near(Px(a, 450, 350), 0, 255, 0), $"apply: green rect inside group at 400,300 ({Px(a, 450, 350)})");
            s.Check(Near(Px(a, 5, 5), 32, 32, 32), $"apply: CLEAR_COLOR background ({Px(a, 5, 5)})");
        }

        // Stacking: blue to the top; mat4 row-major like Stride (translation in row 4) moves the group (anchor TopLeft: 400,300 -> 500,300).
        rc = Apply(b.Clear().Reparent(4, 0, 99)
            .SetFloats(2, Protocol.PropertyId.Transform, Protocol.ValueType.Mat4, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 500, 300, 0, 1));
        s.Check(rc == 0, $"apply: reparent to top + transform ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        s.Pump(3);
        using (var a = s.Grab(w, "apply2.png"))
        {
            s.Check(Near(Px(a, 200, 175), 0, 0, 255), $"apply: blue on top after REPARENT index 99 ({Px(a, 200, 175)})");
            s.Check(Near(Px(a, 550, 350), 0, 255, 0) && Near(Px(a, 450, 350), 32, 32, 32), $"apply: transform translates group ({Px(a, 550, 350)})");
        }

        // DESTROY is recursive: child 3 is released together with group 2.
        s.Check(Apply(b.Clear().Destroy(2)) == 0, "apply: destroy group");
        rc = Apply(b.Clear().Color(3, 1, 1, 1));
        s.Check(rc == Native.Result.UnknownObject, $"apply: child id released by recursive DESTROY ({rc}: {Native.LastError()})");

        // Error cases: rc < 0, message names command index + reason, no crash.
        void Err(string what, Buf buf, Native.Result expected, string mustContain)
        {
            var r = Apply(buf);
            var e = Native.LastError();
            s.Check(r == expected && e.Contains(mustContain), $"apply error: {what} -> {r} \"{e}\"");
        }
        Err("truncated buffer", b.Clear().Raw(Protocol.Op.Destroy, 40, 4), Native.Result.BadLength, "exceeds buffer");
        Err("payloadLen over the cap", b.Clear().Raw(Protocol.Op.Destroy, Protocol.MaxCommandPayload + 1, 4), Native.Result.BadLength, "");
        Err("header flags not 0", b.Clear().Destroy(1).Flags(1), Native.Result.BadArgument, "header flags 1");
        Err("wrong payloadLen", b.Clear().Raw(Protocol.Op.Destroy, 8, 8), Native.Result.BadLength, "expected 4");
        Err("wrong value length", b.Clear().Create(5, Protocol.ElementType.Rectangle).SetFloats(5, Protocol.PropertyId.Size, Protocol.ValueType.Vec2, 1, 2, 3), Native.Result.BadLength, "command #1");
        Err("unknown op", b.Clear().Raw(99, 0, 0), Native.Result.UnknownOp, "unknown opcode");
        Err("unknown id", b.Clear().Place(777, 0, 0, 1, 1), Native.Result.UnknownObject, "unknown id 777");
        Err("unknown parent", b.Clear().Reparent(1, 777, 0), Native.Result.UnknownObject, "unknown parent id 777");
        Err("unknown ref", b.Clear().SetRef(1, Protocol.PropertyId.Size, 777), Native.Result.UnknownObject, "unknown ref id 777");
        Err("unknown type", b.Clear().Create(6, 999), Native.Result.UnknownType, "unknown type 999");
        Err("unknown property", b.Clear().Create(7, Protocol.ElementType.Group).Color(7, 1, 1, 1), Native.Result.UnknownProperty, "not supported by type GROUP");
        Err("value type mismatch", b.Clear().SetFloats(1, Protocol.PropertyId.Size, Protocol.ValueType.F32, 1), Native.Result.BadArgument, "expects value type");
        // One finite check in Elements::set for every float value: NaN/inf never reach a setter.
        Err("NaN opacity", b.Clear().SetFloats(1, Protocol.PropertyId.Opacity, Protocol.ValueType.F32, float.NaN), Native.Result.BadArgument, "must be finite");
        Err("NaN size component", b.Clear().SetFloats(1, Protocol.PropertyId.Size, Protocol.ValueType.Vec2, 1, float.NaN), Native.Result.BadArgument, "must be finite");
        Err("infinite transform component", b.Clear().SetFloats(1, Protocol.PropertyId.Transform, Protocol.ValueType.Mat4,
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, float.PositiveInfinity, 0, 0, 1), Native.Result.BadArgument, "must be finite");
        s.Check(Apply(b.Clear().Create(10, Protocol.ElementType.Text)) == 0, "apply: create text for the font size");
        Err("infinite font size", b.Clear().SetFloats(10, Protocol.PropertyId.FontSize, Protocol.ValueType.F32, float.PositiveInfinity), Native.Result.BadArgument, "must be finite");
        s.Check(Apply(b.Clear().Destroy(10)) == 0, "apply: destroy text");
        Err("duplicate id", b.Clear().Create(1, Protocol.ElementType.Group), Native.Result.Invalid, "already exists");
        Err("create id 0", b.Clear().Create(0, Protocol.ElementType.Group), Native.Result.Invalid, "window root");
        Err("reparent into own subtree", b.Clear().Create(8, Protocol.ElementType.Group).Reparent(8, 1, 0).Reparent(1, 8, 0), Native.Result.Invalid, "command #2");
        Err("bind unknown id", b.Clear().Bind(777, 1), Native.Result.UnknownObject, "command #0");
        s.Check(Native.Apply(SceneHandle.Null, b.Span) == Native.Result.NullHandle, "apply: null window -> NullHandle");

        // Structured error: object id, op and property of the bad command, reason without prefix.
        ApplyError ae = default;
        rc = Apply(b.Clear().Create(9, Protocol.ElementType.Group).SetFloats(9, Protocol.PropertyId.Size, Protocol.ValueType.F32, 1));
        s.Check(rc == Native.Result.BadArgument && Native.GetApplyError(w, out ae) == 0 && ae.Rc == (int)Native.Result.BadArgument && ae.Command == 1 && ae.Id == 9
            && ae.Op == Protocol.Op.Set && ae.Prop == Protocol.PropertyId.Size && ae.Reason.StartsWith("property ")
            && Native.LastError().Contains("id 9"), $"apply error id: SET -> id {ae.Id} op {ae.Op} prop {ae.Prop} \"{ae.Reason}\"");
        Err("unknown property id", b.Clear().Color(9, 1, 1, 1), Native.Result.UnknownProperty, "id 9");
        s.Check(Native.GetApplyError(w, out ae) == 0 && ae.Id == 9 && ae.Prop == Protocol.PropertyId.Color, $"apply error id: -6 -> {ae.Id}/{ae.Prop}");
        Err("clear color, no id", b.Clear().Raw(Protocol.Op.SetClearColor, 4, 4), Native.Result.BadLength, "expected 16");
        s.Check(Native.GetApplyError(w, out ae) == 0 && ae.Id == 0 && ae.Op == Protocol.Op.SetClearColor, $"apply error id: CLEAR_COLOR -> id {ae.Id}");
        // The op table checks the minimum length of a variable payload; id and property still come from the row.
        var shortSet = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(shortSet, 9);
        BinaryPrimitives.WriteUInt16LittleEndian(shortSet.AsSpan(4), Protocol.PropertyId.Color);
        Err("SET shorter than its fields", b.Clear().Cmd(Protocol.Op.Set, shortSet), Native.Result.BadLength, "(op 4 SET, id 9, offset 0)");
        s.Check(Native.GetApplyError(w, out ae) == 0 && ae.Id == 9 && ae.Op == Protocol.Op.Set && ae.Prop == Protocol.PropertyId.Color
            && ae.Reason == $"payloadLen 6 too short for SET (>= {Protocol.SetFieldsSize})", $"apply error id: short SET -> {ae.Id}/{ae.Prop} \"{ae.Reason}\"");
        s.Check(Apply(b.Clear().Destroy(9)) == 0 && Native.GetApplyError(w, out ae) == 0 && ae.Rc == 0 && ae.Id == 0 && ae.Reason.Length == 0,
            "apply error id: cleared by a successful apply");
        s.Check(Native.GetApplyError(SceneHandle.Null, out _) == Native.Result.NullHandle, "apply error id: null window -> NullHandle");
        s.Check(Apply(b.Clear()) == 0, "apply: empty buffer ok");

        // An invalid buffer resets the apply error too: it must not report the apply before it (here: rc -2, command 0).
        Err("clear color, no id again", b.Clear().Raw(Protocol.Op.SetClearColor, 4, 4), Native.Result.BadLength, "expected 16");
        var nullBuf = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.NullRef<byte>(), 4);
        rc = Native.Apply(w, nullBuf);
        s.Check(rc == Native.Result.BadLength && Native.GetApplyError(w, out ae) == 0 && ae.Rc == (int)Native.Result.BadLength
            && ae.Command == 0 && ae.Id == 0 && ae.Op == 0 && ae.Reason.Contains("invalid buffer"),
            $"apply error id: null buffer after a failed apply -> rc {ae.Rc} command {ae.Command} op {ae.Op} \"{ae.Reason}\" ({rc})");
        s.Check(Apply(b.Clear()) == 0 && Native.GetApplyError(w, out ae) == 0 && ae.Rc == 0, "apply error id: empty buffer clears the error");

        // Earlier commands of a failing buffer stay applied.
        rc = Apply(b.Clear().Color(4, 1, 1, 0).Place(777, 0, 0, 1, 1).Color(4, 1, 0, 1));
        s.Check(rc == Native.Result.UnknownObject && Native.LastError().Contains("command #1"), $"apply: stops at bad command #1 ({rc})");
        s.Pump(3);
        using (var a = s.Grab(w, "apply3.png"))
            s.Check(Near(Px(a, 200, 175), 255, 255, 0), $"apply: command before the error stays applied (yellow) ({Px(a, 200, 175)})");

        // Poll: id 1 is bound (handle 42) -> EVENT_MEASURED + EVENT_SCENE_BOUNDS once, plus the first
        // EVENT_WINDOW_GEOMETRY of the window (40 bytes, client position), then nothing.
        var ev = new byte[128];
        var needRc = NativeCalls.Poll(w, Span<byte>.Empty, out var need);
        s.Check(needRc == (int)Native.Result.BufferTooSmall && need == 20 + 28 + 40, $"poll: cap 0 -> buffer too small, required {need} ({needRc})");
        // The poll collects once: a change between the too-small call and its retry is not collected into the retry,
        // so the retry returns exactly the required size and the change arrives with the next poll.
        s.Check(Apply(b.Clear().Size(1, 123, 45)) == 0, "poll: size change of the bound item between the two calls");
        s.Check(NativeCalls.Poll(w, ev) == 20 + 28 + 40, "poll: measured + scene bounds of the bound item + window geometry");
        var later = NativeCalls.Poll(w, ev);
        s.Check(later > 0 && later <= 20 + 28, $"poll: the change made between the two calls is delivered by the next poll ({later})");
        s.Check(NativeCalls.Poll(w, ev) == 0, "poll: 0 bytes (nothing changed)");
        s.Check(NativeCalls.Poll(SceneHandle.Null, ev) == (int)Native.Result.NullHandle, "poll: null window -> NullHandle");

        // Perf: 10 000 SETs (SIZE on 100 items) per apply.
        b.Clear();
        for (uint i = 0; i < 100; i++) b.Create(1000 + i, Protocol.ElementType.Rectangle).Reparent(1000 + i, 0, 1000);
        s.Check(Apply(b) == 0, "apply: create 100 perf items");
        var times = new double[20];
        // best of up to 3 rounds (see PerfLimits.Best); the INFO line shows the last round's spread.
        var median = PerfLimits.Best(2.0, () =>
        {
            for (int run = 0; run < times.Length; run++)
            {
                b.Clear();
                for (int i = 0; i < 10_000; i++)
                    b.Size(1000 + (uint)(i % 100), 10 + run + i % 7, 10);
                var sw = Stopwatch.StartNew();
                rc = Apply(b);
                times[run] = sw.Elapsed.TotalMilliseconds;
                if (rc != 0) { s.Check(false, "apply perf " + Native.LastError()); break; }
            }
            Array.Sort(times);
            return times[times.Length / 2];
        }, cpuBound: true);
        s.Info($"apply 10000 SET (size, {b.Length} bytes): median {median:F3} ms, min {times[0]:F3}, max {times[^1]:F3}");
        s.Perf("apply.apply_10000_set_size", median);
        PerfLimits.Below(s, median, 2.0, $"apply: 10000 SET < 2 ms (median {median:F3} ms)", cpuBound: true);

        b.Clear();
        for (int i = 0; i < 10_000; i++)
            b.Color(1000 + (uint)(i % 100), (i % 256) / 255f, 0.5f, 0.5f);
        var sw2 = Stopwatch.StartNew();
        rc = Apply(b);
        s.Info($"apply 10000 SET (color via QML property): {sw2.Elapsed.TotalMilliseconds:F3} ms (rc {rc})");

        // RESET: every object goes at once (nested and unparented ones too), ids are free again, the clear color stays.
        b.Clear().ClearColor(0.5f, 0, 0).Create(2000, Protocol.ElementType.Group).Reparent(2000, 0, 0).Create(2001, Protocol.ElementType.Rectangle)
            .Reparent(2001, 2000, 0).Place(2001, 0, 0, 800, 600).Color(2001, 0, 1, 0).Create(2002, Protocol.ElementType.Rectangle);
        s.Check(Apply(b) == 0, "reset: build nested + unparented objects");
        rc = Apply(b.Clear().Raw(Protocol.Op.Reset, 0, 0));
        s.Check(rc == 0, $"reset: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
        foreach (uint id in new uint[] { 1, 1000, 2000, 2001, 2002 })
            s.Check(Apply(b.Clear().Destroy(id)) == Native.Result.UnknownObject, $"reset: id {id} gone");
        s.Check(Apply(b.Clear().Create(2001, Protocol.ElementType.Group).Create(1, Protocol.ElementType.Group)) == 0, "reset: ids reusable");
        Err("reset with payload", b.Clear().Raw(Protocol.Op.Reset, 4, 4), Native.Result.BadLength, "expected 0");
        s.Pump(3);
        using (var a = s.Grab(w, "apply_reset.png"))
            s.Check(Near(Px(a, 400, 300), 128, 0, 0), $"reset: scene empty, clear color kept ({Px(a, 400, 300)})");

        RejectCases(s, w, b);

        s.Pump(2);
        Native.vlqt_scene_destroy(w); // releases all remaining ids
    }
}
