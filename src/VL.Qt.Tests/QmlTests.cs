using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Protocol.Prop;

namespace VL.Qt.Tests
{
    /// <summary>Wire helpers shared by the QmlElement tests (#104): values and events encoded like protocol.h "QmlElement".</summary>
    internal static class QmlWire
    {
        public static byte[] F(params float[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), v[i]);
            return b;
        }

        public static byte[] I32(int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            return b;
        }

        public static byte[] Str(string s)
        {
            var utf8 = Encoding.UTF8.GetBytes(s);
            var b = new byte[4 + utf8.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)utf8.Length);
            utf8.CopyTo(b, 4);
            return b;
        }

        public static byte[] Bool(bool v) => new[] { (byte)(v ? 1 : 0) };

        public static byte[] Pad(byte[] b) => b.Concat(new byte[(4 - b.Length % 4) % 4]).ToArray();

        static byte[] U16(int v) => new[] { (byte)(v & 0xFF), (byte)(v >> 8) };

        /// <summary>EV_QML_PROPERTY payload: u16 name length, u16 value type, name, padding to 4, value.</summary>
        public static byte[] Property(string name, ushort type, byte[] value)
        {
            var n = Encoding.UTF8.GetBytes(name);
            return U16(n.Length).Concat(U16(type)).Concat(Pad(n)).Concat(value).ToArray();
        }

        /// <summary>EV_QML_SIGNAL payload: u16 name length, u16 argument count, name, padding to 4, per argument u16 type, u16 0, value, padding.</summary>
        public static byte[] Signal(string name, params (ushort Type, byte[] Value)[] args)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var p = U16(n.Length).Concat(U16(args.Length)).Concat(Pad(n));
            foreach (var (type, value) in args)
                p = p.Concat(U16(type)).Concat(U16(0)).Concat(Pad(value));
            return p.ToArray();
        }
    }

    /// <summary>QmlValue (#104): conversion of patch values to wire values, exact equality, the lines for values that are refused.</summary>
    public class QmlValueTests
    {
        static IEnumerable<TestCaseData> Supported()
        {
            yield return new TestCaseData(1.5f, Protocol.Vt.F32);
            yield return new TestCaseData(2.25d, Protocol.Vt.F32);
            yield return new TestCaseData(-7, Protocol.Vt.I32);
            yield return new TestCaseData(true, Protocol.Vt.Bool);
            yield return new TestCaseData("text", Protocol.Vt.String);
            yield return new TestCaseData("", Protocol.Vt.String);
            yield return new TestCaseData(new Color4(1, 0.5f, 0.25f, 0.75f), Protocol.Vt.Color);
            yield return new TestCaseData(new Vector2(1, 2), Protocol.Vt.Vec2);
            yield return new TestCaseData(new Vector3(1, 2, 3), Protocol.Vt.Vec3);
            yield return new TestCaseData(new Vector4(1, 2, 3, 4), Protocol.Vt.Vec4);
            yield return new TestCaseData(new RectangleF(1, 2, 3, 4), Protocol.Vt.Rectf);
        }

        [TestCaseSource(nameof(Supported))]
        public void TryConvert_EachSupportedType(object value, ushort wireType)
        {
            Assert.That(QmlValue.TryConvert(value, out var v), Is.True);
            Assert.That(v.ValueType, Is.EqualTo(wireType));
        }

        [Test]
        public void TryConvert_Fields()
        {
            QmlValue.TryConvert(new Color4(0.1f, 0.2f, 0.3f, 0.4f), out var c);
            Assert.That((c.A, c.B, c.C, c.D), Is.EqualTo((0.1f, 0.2f, 0.3f, 0.4f)));
            QmlValue.TryConvert(new RectangleF(10, 20, 30, 40), out var r);
            Assert.That((r.A, r.B, r.C, r.D), Is.EqualTo((10f, 20f, 30f, 40f)));
            QmlValue.TryConvert(new Vector3(1, 2, 3), out var v3);
            Assert.That((v3.A, v3.B, v3.C), Is.EqualTo((1f, 2f, 3f)));
            QmlValue.TryConvert(2.5d, out var d);
            Assert.That(d.A, Is.EqualTo(2.5f));
            QmlValue.TryConvert(true, out var b);
            Assert.That(b.Integer, Is.EqualTo(1));
            QmlValue.TryConvert(-5, out var i);
            Assert.That(i.Integer, Is.EqualTo(-5));
            QmlValue.TryConvert("hi", out var s);
            Assert.That(s.Text, Is.EqualTo("hi"));
        }

        static IEnumerable<TestCaseData> Unsupported()
        {
            yield return new TestCaseData(null);
            yield return new TestCaseData(5L);
            yield return new TestCaseData((byte)1);
            yield return new TestCaseData(new object());
            yield return new TestCaseData(DateTime.Now);
            yield return new TestCaseData(new Int2(1, 2));
            yield return new TestCaseData(new string('x', QmlValue.MaxStringChars + 1));
        }

        [TestCaseSource(nameof(Unsupported))]
        public void TryConvert_Unsupported(object? value) => Assert.That(QmlValue.TryConvert(value, out _), Is.False);

        [Test]
        public void TryConvert_LongestString_IsStillSupported() =>
            Assert.That(QmlValue.TryConvert(new string('x', QmlValue.MaxStringChars), out _), Is.True);

        [Test]
        public void Equality_IsExact_NotNearEqual()
        {
            QmlValue.TryConvert(new Vector2(1, 2), out var a);
            QmlValue.TryConvert(new Vector2(1, MathF.BitIncrement(2)), out var b);
            QmlValue.TryConvert(new Vector2(1, 2), out var c);
            Assert.That(a, Is.Not.EqualTo(b), "Stride's Vector2 equality is a near-equal compare; the wire value must not swallow tiny changes");
            Assert.That(a, Is.EqualTo(c));
        }

        [Test]
        public void Equality_SameNumber_DifferentWireType()
        {
            QmlValue.TryConvert(3f, out var f);
            QmlValue.TryConvert(3, out var i);
            Assert.That(f, Is.Not.EqualTo(i));
        }

        [Test]
        public void Describe_Null() =>
            Assert.That(QmlValue.Describe("x", null), Is.EqualTo("Property 'x': the value is not set, ignored"));

        [Test]
        public void Describe_LongString() =>
            Assert.That(QmlValue.Describe("s", new string('x', 30000)),
                Is.EqualTo($"Property 's': the string is longer than {QmlValue.MaxStringChars} characters, ignored"));

        [Test]
        public void Describe_UnsupportedType() =>
            Assert.That(QmlValue.Describe("t", 5L),
                Is.EqualTo("Property 't': type Int64 is not supported, ignored (use float, int, bool, string, Color4, Vector2/3/4 or RectangleF)"));
    }

    /// <summary>QmlProperties (#104): the converted snapshot of the Properties dictionary, change detection by content, problem lines.</summary>
    public class QmlPropertiesTests
    {
        [Test]
        public void NullAndEmpty_AreTheEmptyInstance()
        {
            Assert.That(QmlProperties.From(null), Is.SameAs(QmlProperties.Empty));
            Assert.That(QmlProperties.From(new Dictionary<string, object>()), Is.SameAs(QmlProperties.Empty));
            Assert.That(QmlProperties.Update(QmlProperties.Empty, null), Is.SameAs(QmlProperties.Empty));
            Assert.That(QmlProperties.Update(QmlProperties.From(new() { ["a"] = 1 }), new Dictionary<string, object>()), Is.SameAs(QmlProperties.Empty));
        }

        [Test]
        public void From_ConvertsEntries_AndListsProblems()
        {
            var p = QmlProperties.From(new()
            {
                ["ok"] = 1f,
                ["long"] = 5L,
                [""] = 1,
                ["null"] = null!,
            });
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(p.TryGetValue("ok", out var v) && v.ValueType == Protocol.Vt.F32, Is.True);
            Assert.That(p.TryGetValue("long", out _), Is.False);
            var lines = p.Problems.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.That(lines, Has.Length.EqualTo(3));
            Assert.That(lines.Any(l => l.StartsWith("Property 'long': type Int64")), Is.True);
            Assert.That(lines.Any(l => l.StartsWith("Property name '' is empty")), Is.True);
            Assert.That(lines.Any(l => l == "Property 'null': the value is not set, ignored"), Is.True);
        }

        [Test]
        public void NameLimit_InUtf8Bytes()
        {
            Assert.That(QmlProperties.IsValidName(new string('a', Protocol.UniformNameMax)), Is.True);
            Assert.That(QmlProperties.IsValidName(new string('a', Protocol.UniformNameMax + 1)), Is.False);
            Assert.That(QmlProperties.IsValidName(new string('ä', Protocol.UniformNameMax / 2 + 1)), Is.False, "2 bytes per char");
            Assert.That(QmlProperties.IsValidName(null), Is.False);
            Assert.That(QmlProperties.IsValidName(""), Is.False);
            Assert.That(QmlProperties.From(new() { [new string('a', 300)] = 1 }).Problems, Does.Contain("longer than 255 bytes"));
        }

        [Test]
        public void Update_SameContent_KeepsTheInstance()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["b"] = "x" };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            Assert.That(QmlProperties.Update(p, d), Is.SameAs(p));
            Assert.That(QmlProperties.Update(p, new Dictionary<string, object> { ["b"] = "x", ["a"] = 1f }), Is.SameAs(p), "another dictionary, same content, other order");
        }

        [Test]
        public void Update_MutatedInPlace_IsSeen()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            d["a"] = 2f;
            var q = QmlProperties.Update(p, d);
            Assert.That(q, Is.Not.SameAs(p));
            Assert.That(q.TryGetValue("a", out var v) && v.A == 2f, Is.True);
            d["b"] = true;
            Assert.That(QmlProperties.Update(q, d), Is.Not.SameAs(q), "added key");
            d.Remove("b");
            Assert.That(QmlProperties.Update(QmlProperties.Update(q, new() { ["a"] = 2f, ["b"] = true }), d).Count, Is.EqualTo(1), "removed key");
        }

        [Test]
        public void Update_TypeChange_SameNumber_IsAChange()
        {
            var p = QmlProperties.Update(QmlProperties.Empty, new() { ["a"] = 3f });
            Assert.That(QmlProperties.Update(p, new() { ["a"] = 3 }), Is.Not.SameAs(p));
        }

        [Test]
        public void Update_InvalidEntries_DoNotCauseRebuilds()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["bad"] = 5L };
            var p = QmlProperties.Update(QmlProperties.Empty, d);
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(QmlProperties.Update(p, d), Is.SameAs(p));
            d["bad2"] = 6L;
            Assert.That(QmlProperties.Update(p, d), Is.Not.SameAs(p), "one more invalid entry changes the problems");
            d.Remove("bad");
            d.Remove("bad2");
            d["a"] = 1f;
            Assert.That(QmlProperties.Update(p, d).Problems, Is.Empty);
        }
    }

    /// <summary>QmlPropertyWriter (#104): the diff by name and the encoding of every value type into SET_QML_PROPERTIES.</summary>
    public class QmlPropertyWriterTests
    {
        static QmlProperties Props(params (string Name, object Value)[] entries)
        {
            var d = new Dictionary<string, object>();
            foreach (var (n, v) in entries)
                d[n] = v;
            return QmlProperties.From(d);
        }

        static string[] Write(QmlProperties next, QmlProperties? previous, out int sent)
        {
            var sink = new RecordingSink();
            sent = QmlPropertyWriter.Write(sink, 7, next, previous);
            return sink.Commands.ToArray();
        }

        [Test]
        public void Without_Previous_SendsEverything_InOrder()
        {
            var cmds = Write(Props(("b", 2), ("a", 1.5f), ("c", "x")), null, out int sent);
            Assert.That(sent, Is.EqualTo(3));
            Assert.That(cmds, Is.EqualTo(new[] { "QML 7 0 b=i32(2) a=f32(1.5) c=string(x)" }));
        }

        [Test]
        public void Unchanged_SendsNothing()
        {
            var p = Props(("a", 1f), ("s", "x"));
            Assert.That(Write(Props(("a", 1f), ("s", "x")), p, out int sent), Is.Empty);
            Assert.That(sent, Is.EqualTo(0));
        }

        [Test]
        public void OnlyChangedEntries_AreSent()
        {
            var cmds = Write(Props(("a", 1f), ("b", 3), ("c", "z")), Props(("a", 1f), ("b", 2), ("c", "z")), out int sent);
            Assert.That(cmds, Is.EqualTo(new[] { "QML 7 0 b=i32(3)" }));
            Assert.That(sent, Is.EqualTo(1));
        }

        [Test]
        public void NewKey_IsSent()
        {
            var cmds = Write(Props(("a", 1f), ("n", true)), Props(("a", 1f)), out _);
            Assert.That(cmds, Is.EqualTo(new[] { "QML 7 0 n=bool(1)" }));
        }

        [Test]
        public void RemovedKey_IsNotReset()
        {
            Assert.That(Write(Props(("a", 1f)), Props(("a", 1f), ("gone", 5)), out _), Is.Empty);
            Assert.That(Write(QmlProperties.Empty, Props(("a", 1f)), out _), Is.Empty);
        }

        [Test]
        public void SameNumber_OtherWireType_IsSent()
        {
            Assert.That(Write(Props(("a", 3)), Props(("a", 3f)), out _), Is.EqualTo(new[] { "QML 7 0 a=i32(3)" }));
        }

        [Test]
        public void TinyVectorChange_IsSent()
        {
            var cmds = Write(Props(("v", new Vector2(1, MathF.BitIncrement(2)))), Props(("v", new Vector2(1, 2))), out int sent);
            Assert.That(sent, Is.EqualTo(1));
            Assert.That(cmds, Has.Length.EqualTo(1));
        }

        [Test]
        public void EveryValueType_IsWrittenToTheSink()
        {
            var cmds = Write(Props(
                ("f", 0.5f), ("d", 0.25d), ("i", 3), ("b", false), ("s", "hä"),
                ("c", new Color4(1, 0.5f, 0.25f, 0.75f)),
                ("v2", new Vector2(1, 2)), ("v3", new Vector3(1, 2, 3)), ("v4", new Vector4(1, 2, 3, 4)),
                ("r", new RectangleF(5, 6, 7, 8))), null, out int sent);
            Assert.That(sent, Is.EqualTo(10));
            Assert.That(cmds, Is.EqualTo(new[]
            {
                "QML 7 0 f=f32(0.5) d=f32(0.25) i=i32(3) b=bool(0) s=string(hä) c=color(1,0.5,0.25,0.75) " +
                "v2=vec2(1,2) v3=vec3(1,2,3) v4=vec4(1,2,3,4) r=rect(5,6,7,8)"
            }));
        }

        [Test]
        public void LargeTable_IsSplitIntoSeveralCommands_EveryEntryOnce()
        {
            var entries = Enumerable.Range(0, 8).Select(i => ($"p{i}", (object)new string((char)('a' + i), 15000))).ToArray();
            var cmds = Write(Props(entries), null, out int sent);
            Assert.That(sent, Is.EqualTo(8));
            Assert.That(cmds.Length, Is.GreaterThan(2));
            Assert.That(cmds.All(c => c.StartsWith("QML 7 0 ")), Is.True);
            for (int i = 0; i < 8; i++)
                Assert.That(cmds.Count(c => c.Contains($" p{i}=")), Is.EqualTo(1), $"p{i}");
        }

        [Test]
        public void LargeTable_FitsTheCommandLimit_OnTheRealWriter()
        {
            var entries = Enumerable.Range(0, 8).Select(i => ($"p{i}", (object)new string('€', 6000))).ToArray(); // 3 bytes per char
            var w = new CommandWriter();
            Assert.DoesNotThrow(() => QmlPropertyWriter.Write(w, 1, Props(entries), null));
            Assert.That(w.Count, Is.GreaterThan(1));
        }

        // --- bytes ---

        static string Hex(byte[] b) => Convert.ToHexString(b);
        static string U16(int v) => $"{v & 0xFF:X2}{(v >> 8) & 0xFF:X2}";
        static string U32(uint v) => Hex(BitConverter.GetBytes(v));
        static string F32(float v) => Hex(BitConverter.GetBytes(v));

        /// <summary>One entry: u16 value type, u16 name length, name padded so that the block is a multiple of 4, value padded to 4.</summary>
        static string Entry(ushort type, string name, string valueHex)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var block = Hex(n) + new string('0', 2 * ((4 - (4 + n.Length) % 4) % 4));
            var value = valueHex + new string('0', 2 * ((4 - valueHex.Length / 2 % 4) % 4));
            return U16(type) + U16(n.Length) + block + value;
        }

        static string Command(uint id, int count, string entries) =>
            U16(Protocol.Op.SetQmlProperties) + U16(8 + entries.Length / 2) + U32(id) + U16(count) + U16(0) + entries;

        static string Encode(params (string Name, object Value)[] entries)
        {
            var w = new CommandWriter();
            QmlPropertyWriter.Write(w, 9, Props(entries), null);
            return Hex(w.Written.ToArray());
        }

        [Test]
        public void Bytes_F32_AndDouble() =>
            Assert.That(Encode(("a", 1.5f), ("bb", 2d)), Is.EqualTo(Command(9, 2,
                Entry(Protocol.Vt.F32, "a", F32(1.5f)) + Entry(Protocol.Vt.F32, "bb", F32(2f)))));

        [Test]
        public void Bytes_I32() =>
            Assert.That(Encode(("count", -2)), Is.EqualTo(Command(9, 1, Entry(Protocol.Vt.I32, "count", "FEFFFFFF"))));

        [Test]
        public void Bytes_Bool() =>
            Assert.That(Encode(("on", true), ("off", false)), Is.EqualTo(Command(9, 2,
                Entry(Protocol.Vt.Bool, "on", "01") + Entry(Protocol.Vt.Bool, "off", "00"))));

        [Test]
        public void Bytes_String_Utf8_LengthInBytes()
        {
            const string text = "äö€😀";
            var utf8 = Encoding.UTF8.GetBytes(text);
            Assert.That(utf8.Length, Is.EqualTo(2 + 2 + 3 + 4));
            Assert.That(Encode(("label", text)), Is.EqualTo(Command(9, 1,
                Entry(Protocol.Vt.String, "label", U32((uint)utf8.Length) + Hex(utf8)))));
        }

        [Test]
        public void Bytes_String_Empty() =>
            Assert.That(Encode(("s", "")), Is.EqualTo(Command(9, 1, Entry(Protocol.Vt.String, "s", U32(0)))));

        [Test]
        public void Bytes_Color() =>
            Assert.That(Encode(("fill", new Color4(1f, 0.5f, 0.25f, 0.75f))), Is.EqualTo(Command(9, 1,
                Entry(Protocol.Vt.Color, "fill", F32(1f) + F32(0.5f) + F32(0.25f) + F32(0.75f)))));

        [Test]
        public void Bytes_Vectors() =>
            Assert.That(Encode(("v2", new Vector2(1, 2)), ("v3", new Vector3(1, 2, 3)), ("v4", new Vector4(1, 2, 3, 4))), Is.EqualTo(Command(9, 3,
                Entry(Protocol.Vt.Vec2, "v2", F32(1) + F32(2))
                + Entry(Protocol.Vt.Vec3, "v3", F32(1) + F32(2) + F32(3))
                + Entry(Protocol.Vt.Vec4, "v4", F32(1) + F32(2) + F32(3) + F32(4)))));

        [Test]
        public void Bytes_Rect() =>
            Assert.That(Encode(("box", new RectangleF(10, 20, 300, 400))), Is.EqualTo(Command(9, 1,
                Entry(Protocol.Vt.Rectf, "box", F32(10) + F32(20) + F32(300) + F32(400)))));

        [Test]
        public void Bytes_NonAsciiName_PaddedByBytes() =>
            Assert.That(Encode(("größe", 1f)), Is.EqualTo(Command(9, 1, Entry(Protocol.Vt.F32, "größe", F32(1f)))));

        [Test]
        public void Bytes_NothingToSend_WritesNoCommand()
        {
            var w = new CommandWriter();
            QmlPropertyWriter.Write(w, 9, Props(("a", 1f)), Props(("a", 1f)));
            Assert.That(w.Count, Is.EqualTo(0));
        }
    }

    /// <summary>QmlEvents (#104): decoding of EV_QML_PROPERTY and EV_QML_SIGNAL, for every value type and for broken payloads.</summary>
    public class QmlEventsTests
    {
        static IEnumerable<TestCaseData> Values()
        {
            yield return new TestCaseData(Protocol.Vt.F32, QmlWire.F(1.5f), 1.5f);
            yield return new TestCaseData(Protocol.Vt.I32, QmlWire.I32(-42), -42);
            yield return new TestCaseData(Protocol.Vt.Bool, QmlWire.Bool(true), true);
            yield return new TestCaseData(Protocol.Vt.Bool, QmlWire.Bool(false), false);
            yield return new TestCaseData(Protocol.Vt.String, QmlWire.Str("hä€"), "hä€");
            yield return new TestCaseData(Protocol.Vt.String, QmlWire.Str(""), "");
            yield return new TestCaseData(Protocol.Vt.Color, QmlWire.F(1, 0.5f, 0.25f, 0.75f), new Color4(1, 0.5f, 0.25f, 0.75f));
            yield return new TestCaseData(Protocol.Vt.Vec2, QmlWire.F(1, 2), new Vector2(1, 2));
            yield return new TestCaseData(Protocol.Vt.Vec3, QmlWire.F(1, 2, 3), new Vector3(1, 2, 3));
            yield return new TestCaseData(Protocol.Vt.Vec4, QmlWire.F(1, 2, 3, 4), new Vector4(1, 2, 3, 4));
            yield return new TestCaseData(Protocol.Vt.Rectf, QmlWire.F(10, 20, 30, 40), new RectangleF(10, 20, 30, 40));
        }

        [TestCaseSource(nameof(Values))]
        public void Property_EachValueType(ushort type, byte[] value, object expected)
        {
            Assert.That(QmlEvents.TryDecodeProperty(QmlWire.Property("prop", type, value), out var name, out var v), Is.True);
            Assert.That(name, Is.EqualTo("prop"));
            Assert.That(v, Is.EqualTo(expected));
            Assert.That(v.GetType(), Is.EqualTo(expected.GetType()));
        }

        [Test]
        public void Property_NamePadding_AllLengths()
        {
            foreach (var name in new[] { "a", "ab", "abc", "abcd", "abcde", "größe", "x€" })
            {
                Assert.That(QmlEvents.TryDecodeProperty(QmlWire.Property(name, Protocol.Vt.I32, QmlWire.I32(9)), out var n, out var v), Is.True, name);
                Assert.That((n, v), Is.EqualTo((name, (object)9)), name);
            }
        }

        [Test]
        public void Property_Malformed_IsRejected()
        {
            var good = QmlWire.Property("abc", Protocol.Vt.F32, QmlWire.F(1));
            Assert.That(QmlEvents.TryDecodeProperty(good, out _, out _), Is.True);
            Assert.That(QmlEvents.TryDecodeProperty(good.AsSpan(0, 3), out _, out _), Is.False, "shorter than the header");
            Assert.That(QmlEvents.TryDecodeProperty(good.AsSpan(0, good.Length - 1), out _, out _), Is.False, "truncated value");
            Assert.That(QmlEvents.TryDecodeProperty(good.AsSpan(0, 6), out _, out _), Is.False, "name cut off");
            Assert.That(QmlEvents.TryDecodeProperty(QmlWire.Property("", Protocol.Vt.F32, QmlWire.F(1)), out _, out _), Is.False, "empty name");
            Assert.That(QmlEvents.TryDecodeProperty(QmlWire.Property("a", Protocol.Vt.Mat4, new byte[64]), out _, out _), Is.False, "unsupported value type");
            Assert.That(QmlEvents.TryDecodeProperty(QmlWire.Property("a", 0, new byte[4]), out _, out _), Is.False, "value type 0");
            // string whose length field points beyond the payload
            var bad = QmlWire.Property("a", Protocol.Vt.String, QmlWire.Str("hello"));
            Assert.That(QmlEvents.TryDecodeProperty(bad.AsSpan(0, bad.Length - 3), out _, out _), Is.False, "string cut off");
        }

        [Test]
        public void Signal_NoArguments()
        {
            Assert.That(QmlEvents.TryDecodeSignal(QmlWire.Signal("done"), out var s), Is.True);
            Assert.That(s.Name, Is.EqualTo("done"));
            Assert.That(s.Arguments.Count, Is.EqualTo(0));
        }

        [TestCaseSource(nameof(Values))]
        public void Signal_OneArgument_EachValueType(ushort type, byte[] value, object expected)
        {
            Assert.That(QmlEvents.TryDecodeSignal(QmlWire.Signal("sig", (type, value)), out var s), Is.True);
            Assert.That(s.Arguments.Count, Is.EqualTo(1));
            Assert.That(s.Arguments[0], Is.EqualTo(expected));
            Assert.That(s.Arguments[0].GetType(), Is.EqualTo(expected.GetType()));
        }

        [Test]
        public void Signal_MixedArguments_AreAlignedIndividually()
        {
            var payload = QmlWire.Signal("finished",
                (Protocol.Vt.String, QmlWire.Str("done")),
                (Protocol.Vt.Bool, QmlWire.Bool(true)),
                (Protocol.Vt.F32, QmlWire.F(1)),
                (Protocol.Vt.Color, QmlWire.F(1, 0, 0, 1)),
                (Protocol.Vt.I32, QmlWire.I32(7)));
            Assert.That(QmlEvents.TryDecodeSignal(payload, out var s), Is.True);
            Assert.That(s.Name, Is.EqualTo("finished"));
            Assert.That(s.Arguments.ToArray(), Is.EqualTo(new object[] { "done", true, 1f, new Color4(1, 0, 0, 1), 7 }));
        }

        [Test]
        public void Signal_Malformed_IsRejected()
        {
            var good = QmlWire.Signal("clicked", (Protocol.Vt.I32, QmlWire.I32(1)), (Protocol.Vt.F32, QmlWire.F(2)));
            Assert.That(QmlEvents.TryDecodeSignal(good, out _), Is.True);
            Assert.That(QmlEvents.TryDecodeSignal(good.AsSpan(0, 3), out _), Is.False, "shorter than the header");
            Assert.That(QmlEvents.TryDecodeSignal(good.AsSpan(0, good.Length - 4), out _), Is.False, "second argument missing");
            Assert.That(QmlEvents.TryDecodeSignal(good.AsSpan(0, good.Length - 1), out _), Is.False, "second argument truncated");
            Assert.That(QmlEvents.TryDecodeSignal(QmlWire.Signal("", (Protocol.Vt.I32, QmlWire.I32(1))), out _), Is.False, "empty name");
            Assert.That(QmlEvents.TryDecodeSignal(QmlWire.Signal("s", (Protocol.Vt.Mat4, new byte[64])), out _), Is.False, "unknown argument type");
            var tooMany = QmlWire.Signal("s", Enumerable.Repeat((Protocol.Vt.I32, QmlWire.I32(1)), 65).ToArray());
            Assert.That(QmlEvents.TryDecodeSignal(tooMany, out _), Is.False, "more than 64 arguments");
            var many = QmlWire.Signal("s", Enumerable.Repeat((Protocol.Vt.I32, QmlWire.I32(1)), 64).ToArray());
            Assert.That(QmlEvents.TryDecodeSignal(many, out var s) && s.Arguments.Count == 64, Is.True);
        }

        // --- through the registry into the slot ---

        static (SinkRegistry Registry, ElementSink Sink, uint Handle) Bound()
        {
            var registry = new SinkRegistry();
            var sink = new ElementSink();
            return (registry, sink, registry.Acquire(new SinkSlot(sink, 0)));
        }

        [Test]
        public void Dispatch_Property_LastValuePerName()
        {
            var (reg, sink, h) = Bound();
            var res = reg.Dispatch(new SinkRegistryTests.Poll()
                .Add(Protocol.Ev.QmlProperty, h, QmlWire.Property("a", Protocol.Vt.F32, QmlWire.F(1)))
                .Add(Protocol.Ev.QmlProperty, h, QmlWire.Property("b", Protocol.Vt.String, QmlWire.Str("x")))
                .Add(Protocol.Ev.QmlProperty, h, QmlWire.Property("a", Protocol.Vt.F32, QmlWire.F(2))).Bytes);
            Assert.That(res, Is.EqualTo(new DispatchResult(3, 0, null)));
            var values = sink.QmlPropertyValues()!;
            Assert.That(values, Has.Count.EqualTo(2));
            Assert.That(values["a"], Is.EqualTo(2f));
            Assert.That(values["b"], Is.EqualTo("x"));
            Assert.That(sink.QmlPropertyUpdates(), Is.EqualTo(3));
        }

        [Test]
        public void Dispatch_Signals_InOrder_TakenOnce()
        {
            var (reg, sink, h) = Bound();
            reg.Dispatch(new SinkRegistryTests.Poll()
                .Add(Protocol.Ev.QmlSignal, h, QmlWire.Signal("clicked", (Protocol.Vt.I32, QmlWire.I32(1))))
                .Add(Protocol.Ev.QmlSignal, h, QmlWire.Signal("clicked", (Protocol.Vt.I32, QmlWire.I32(2))))
                .Add(Protocol.Ev.QmlSignal, h, QmlWire.Signal("reset")).Bytes);
            var taken = sink.TakeQmlSignals();
            Assert.That(taken.Select(s => s.Name), Is.EqualTo(new[] { "clicked", "clicked", "reset" }));
            Assert.That(taken[1].Arguments[0], Is.EqualTo(2));
            Assert.That(sink.TakeQmlSignals().Count, Is.EqualTo(0));
        }

        [Test]
        public void Dispatch_MalformedPayload_IsIgnored_NotApplied()
        {
            var (reg, sink, h) = Bound();
            var res = reg.Dispatch(new SinkRegistryTests.Poll()
                .Add(Protocol.Ev.QmlProperty, h, QmlWire.Property("", Protocol.Vt.F32, QmlWire.F(1)))
                .Add(Protocol.Ev.QmlSignal, h, new byte[] { 1, 0 }).Bytes);
            Assert.That(res.Applied, Is.EqualTo(0));
            Assert.That(res.Ignored, Is.EqualTo(2));
            Assert.That(sink.QmlPropertyValues(), Is.Null);
            Assert.That(sink.TakeQmlSignals().Count, Is.EqualTo(0));
        }

        [Test]
        public void ResetQml_ForgetsValuesAndSignals_AndCountsAsUpdate()
        {
            var (reg, sink, h) = Bound();
            reg.Dispatch(new SinkRegistryTests.Poll()
                .Add(Protocol.Ev.QmlProperty, h, QmlWire.Property("a", Protocol.Vt.F32, QmlWire.F(1)))
                .Add(Protocol.Ev.QmlSignal, h, QmlWire.Signal("s")).Bytes);
            int before = sink.QmlPropertyUpdates();
            sink.ResetQml();
            Assert.That(sink.QmlPropertyValues(), Is.Empty);
            Assert.That(sink.TakeQmlSignals().Count, Is.EqualTo(0));
            Assert.That(sink.QmlPropertyUpdates(), Is.Not.EqualTo(before));
        }

        [Test]
        public void Signals_BeyondTheCap_AreDropped()
        {
            var (reg, sink, h) = Bound();
            var poll = new SinkRegistryTests.Poll();
            for (int i = 0; i < ElementSink.MaxPendingQmlSignals + 10; i++)
                poll.Add(Protocol.Ev.QmlSignal, h, QmlWire.Signal("s"));
            reg.Dispatch(poll.Bytes);
            Assert.That(sink.TakeQmlSignals().Count, Is.EqualTo(ElementSink.MaxPendingQmlSignals));
        }
    }

    /// <summary>QmlFileWatcher (#104): which changes count, folder only, change flag taken once.</summary>
    public class QmlFileWatcherTests
    {
        string _dir = null!;
        readonly Subject<FileSystemEventArgs> _events = new();
        int _subscriptions;
        string? _watched;

        [SetUp]
        public void SetUp()
        {
            _dir = Directory.CreateTempSubdirectory("vlqt-qmlwatch").FullName;
            _subscriptions = 0;
            _watched = null;
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_dir, true);

        QmlFileWatcher Watcher() => new(dir =>
        {
            _watched = dir;
            return System.Reactive.Linq.Observable.Create<FileSystemEventArgs>(o =>
            {
                _subscriptions++;
                var d = _events.Subscribe(o);
                return () => { _subscriptions--; d.Dispose(); };
            });
        });

        FileSystemEventArgs Changed(string name) => new(WatcherChangeTypes.Changed, _dir, name);

        [TestCase("a.qml", true)]
        [TestCase("Other.QML", true)]
        [TestCase("qmldir", true)]
        [TestCase("QMLDIR", true)]
        [TestCase("sub/qmldir", true)]
        [TestCase("a.js", false)]
        [TestCase("a.qml.bak", false)]
        [TestCase("a.qmlc", false)]
        [TestCase("notes.txt", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void IsRelevantName(string? name, bool expected) => Assert.That(QmlFileWatcher.IsRelevantName(name), Is.EqualTo(expected));

        [Test]
        public void Rename_CountsForTheOldOrTheNewName()
        {
            Assert.That(QmlFileWatcher.IsRelevant(new RenamedEventArgs(WatcherChangeTypes.Renamed, _dir, "a.qml~", "a.qml")), Is.True, "temp file renamed to a.qml");
            Assert.That(QmlFileWatcher.IsRelevant(new RenamedEventArgs(WatcherChangeTypes.Renamed, _dir, "a.bak", "a.qml")), Is.True, "a.qml renamed away");
            Assert.That(QmlFileWatcher.IsRelevant(new RenamedEventArgs(WatcherChangeTypes.Renamed, _dir, "a.txt", "b.txt")), Is.False);
        }

        [Test]
        public void WatchesTheFolderOfTheFile_ChangeIsTakenOnce()
        {
            using var w = Watcher();
            w.Update(System.IO.Path.Combine(_dir, "a.qml"));
            Assert.That(_watched, Is.EqualTo(_dir));
            Assert.That(w.IsWatching, Is.True);
            Assert.That(w.TakeChanged(), Is.False);
            _events.OnNext(Changed("Other.qml")); // a component next to the file counts
            Assert.That(w.TakeChanged(), Is.True);
            Assert.That(w.TakeChanged(), Is.False);
            _events.OnNext(Changed("readme.txt"));
            _events.OnNext(Changed("helper.js")); // .js files are not watched
            Assert.That(w.TakeChanged(), Is.False);
            _events.OnNext(Changed("qmldir"));
            Assert.That(w.TakeChanged(), Is.True);
        }

        [Test]
        public void SamePath_DoesNotResubscribe_NewPathForgetsTheChange()
        {
            using var w = Watcher();
            var a = System.IO.Path.Combine(_dir, "a.qml");
            w.Update(a);
            w.Update(a);
            Assert.That(_subscriptions, Is.EqualTo(1));
            _events.OnNext(Changed("a.qml"));
            w.Update(System.IO.Path.Combine(_dir, "b.qml"));
            Assert.That(w.TakeChanged(), Is.False, "a change reported for the old path is forgotten");
            Assert.That(_subscriptions, Is.EqualTo(1), "the old subscription is gone");
        }

        [Test]
        public void EmptyPath_Stops()
        {
            using var w = Watcher();
            w.Update(System.IO.Path.Combine(_dir, "a.qml"));
            w.Update("");
            Assert.That(w.IsWatching, Is.False);
            Assert.That(_subscriptions, Is.EqualTo(0));
        }

        [Test]
        public void MissingFolder_IsNotWatched_UntilThePathIsSetAgain()
        {
            using var w = Watcher();
            var missing = System.IO.Path.Combine(_dir, "later", "a.qml");
            w.Update(missing);
            Assert.That(w.IsWatching, Is.False);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(missing)!);
            w.Update(missing); // same path: nothing happens
            Assert.That(w.IsWatching, Is.False);
            w.Update("");
            w.Update(missing);
            Assert.That(w.IsWatching, Is.True);
        }

        [Test]
        public void Dispose_EndsTheSubscription()
        {
            var w = Watcher();
            w.Update(System.IO.Path.Combine(_dir, "a.qml"));
            w.Dispose();
            Assert.That(_subscriptions, Is.EqualTo(0));
            _events.OnNext(Changed("a.qml"));
            Assert.That(w.TakeChanged(), Is.False);
        }
    }

    /// <summary>QmlElement node (#104): description, reconciler commands, reload, outputs, errors.</summary>
    public class QmlElementNodeTests
    {
        string _dir = null!;
        string _file = null!;
        readonly Subject<FileSystemEventArgs> _events = new();
        QmlElementNode _node = null!;
        SinkRegistry _registry = null!;
        uint _handle;
        RecordingSink _sink = null!;
        Reconciler _reconciler = null!;
        readonly List<string> _warnings = new();

        sealed class Nop : IDisposable { public void Dispose() { } }

        [SetUp]
        public void SetUp()
        {
            _dir = Directory.CreateTempSubdirectory("vlqt-qmlnode").FullName;
            _file = System.IO.Path.Combine(_dir, "a.qml");
            _warnings.Clear();
            _node = new QmlElementNode(w => { _warnings.Add(w); return new Nop(); }, _ => _events);
            _registry = new SinkRegistry();
            _handle = _registry.Acquire(new SinkSlot(_node.Core.Sink, 0));
            _sink = new RecordingSink();
            _reconciler = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
        }

        [TearDown]
        public void TearDown()
        {
            _node.Dispose();
            Directory.Delete(_dir, true);
        }

        QmlElement Update(Dictionary<string, object>? properties = null, string? file = "", bool autoSize = true)
            => Update(out _, out _, out _, properties, file, autoSize);

        QmlElement Update(out Dictionary<string, object> changed, out Spread<QmlSignal> signals, out string error,
            Dictionary<string, object>? properties = null, string? file = "", bool autoSize = true)
            => (QmlElement)_node.Update(file is null ? null : new Path(file == "" ? _file : file), properties, new Vector2(1, 1), autoSize,
                Matrix.Identity, RectangleAnchor.TopLeft, 1, false, false, null, null,
                out changed, out signals, out _, out _, out error);

        string[] Frame(Dictionary<string, object>? properties = null, string? file = "")
        {
            _sink.Clear();
            _reconciler.Update(Update(properties, file), Space.DIPTopLeft);
            return _sink.Commands.ToArray();
        }

        SinkRegistryTests.Poll NewPoll() => new();

        [Test]
        public void Defaults()
        {
            var e = Update(null, null);
            Assert.That(e.Type, Is.EqualTo(ElementType.Qml));
            Assert.That((ushort)ElementType.Qml, Is.EqualTo(Protocol.Type.Qml));
            Assert.That((e.AutoSize, e.Source, e.Reload), Is.EqualTo((true, string.Empty, 0)));
            Assert.That(e.Properties, Is.SameAs(QmlProperties.Empty));
            Assert.That(e.Sink.Sink, Is.SameAs(_node.Core.Sink));
        }

        [Test]
        public void Source_IsTheFullPath()
        {
            Assert.That(Update(null, _file).Source, Is.EqualTo(_file));
            Assert.That(Update(null, System.IO.Path.Combine("rel", "x.qml")).Source, Is.EqualTo(System.IO.Path.GetFullPath(System.IO.Path.Combine("rel", "x.qml"))));
            Assert.That(Update(null, "  ").Source, Is.Empty);
        }

        [Test]
        public void SameInputs_SameDescription()
        {
            var props = new Dictionary<string, object> { ["a"] = 1f };
            var first = Update(props);
            Assert.That(Update(props), Is.SameAs(first));
            Assert.That(Update(new Dictionary<string, object> { ["a"] = 1f }), Is.SameAs(first), "a new dictionary with the same content");
            Assert.That(Update(new Dictionary<string, object> { ["a"] = 2f }), Is.Not.SameAs(first));
            Assert.That(Update(props, autoSize: false), Is.Not.SameAs(first));
        }

        [Test]
        public void Reconciler_FirstFrame_CreatesSetsSourceAndAllProperties()
        {
            var cmds = Frame(new() { ["fill"] = new Color4(1, 0, 0, 1), ["progress"] = 0.5f });
            Assert.That(cmds[0], Is.EqualTo($"CREATE 1 {Protocol.Type.Qml}"));
            Assert.That(cmds, Does.Contain($"SET 1 {P.QmlSource} string({_file})"));
            Assert.That(cmds, Does.Not.Contain($"SET 1 {P.QmlReload} i32(0)"), "reload counter 0 is the default");
            Assert.That(cmds, Does.Contain("QML 1 0 fill=color(1,0,0,1) progress=f32(0.5)"));
        }

        [Test]
        public void Reconciler_OnlyChangedProperties_AreSent()
        {
            Frame(new() { ["a"] = 1f, ["b"] = "x", ["c"] = true });
            Assert.That(Frame(new() { ["a"] = 1f, ["b"] = "x", ["c"] = true }), Is.Empty);
            Assert.That(Frame(new() { ["a"] = 2f, ["b"] = "x", ["c"] = true }), Is.EqualTo(new[] { "QML 1 0 a=f32(2)" }));
            Assert.That(Frame(new() { ["a"] = 2f, ["b"] = "y", ["c"] = false, ["d"] = 4 }), Is.EqualTo(new[] { "QML 1 0 b=string(y) c=bool(0) d=i32(4)" }));
        }

        [Test]
        public void Reconciler_RemovedKey_IsNotReset_AndNotSentAgainWhenItComesBack()
        {
            Frame(new() { ["a"] = 1f, ["b"] = 2f });
            Assert.That(Frame(new() { ["a"] = 1f }), Is.Empty, "removed: nothing is sent");
            Assert.That(Frame(new() { ["a"] = 1f, ["b"] = 2f }), Is.EqualTo(new[] { "QML 1 0 b=f32(2)" }),
                "back again: sent again, the diff is against the last description");
        }

        [Test]
        public void Reconciler_MutatedInPlace_IsSent()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f };
            Frame(d);
            d["a"] = 5f;
            Assert.That(Frame(d), Is.EqualTo(new[] { "QML 1 0 a=f32(5)" }));
        }

        [Test]
        public void Reconciler_NewFile_SendsSourceAndAllPropertiesAgain()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f };
            Frame(d);
            var other = System.IO.Path.Combine(_dir, "b.qml");
            var cmds = Frame(d, other);
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 {P.QmlSource} string({other})", "QML 1 0 a=f32(1)" }));
        }

        [Test]
        public void Reconciler_AutoSizeOff_IsSent()
        {
            Update();
            _sink.Clear();
            _reconciler.Update(Update(autoSize: false), Space.DIPTopLeft);
            Assert.That(_sink.Commands, Does.Contain($"SET 1 {P.AutoSize} bool(0)"));
        }

        [Test]
        public void HotReload_ChangeOfAFileNextToIt_BumpsTheCounter_AndResendsEverything()
        {
            var d = new Dictionary<string, object> { ["a"] = 1f, ["b"] = "x" };
            Frame(d);
            Assert.That(Frame(d), Is.Empty);
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "Helper.qml"));
            var cmds = Frame(d);
            Assert.That(cmds, Is.EqualTo(new[] { $"SET 1 {P.QmlReload} i32(1)", "QML 1 0 a=f32(1) b=string(x)" }));
            Assert.That(Frame(d), Is.Empty, "once per change");
        }

        [Test]
        public void HotReload_OtherFiles_DoNotReload()
        {
            Frame();
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "readme.txt"));
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "lib.js"));
            Assert.That(Frame(), Is.Empty);
        }

        [Test]
        public void HotReload_ResetsTheOutputs()
        {
            Frame();
            _registry.Dispatch(NewPoll()
                .Add(Protocol.Ev.QmlProperty, _handle, QmlWire.Property("steps", Protocol.Vt.I32, QmlWire.I32(3)))
                .Add(Protocol.Ev.QmlSignal, _handle, QmlWire.Signal("clicked", (Protocol.Vt.I32, QmlWire.I32(1)))).Bytes);
            Update(out var changed, out var signals, out _);
            Assert.That(changed["steps"], Is.EqualTo(3));
            Assert.That(signals.Count, Is.EqualTo(1));

            _registry.Dispatch(NewPoll().Add(Protocol.Ev.QmlSignal, _handle, QmlWire.Signal("clicked")).Bytes);
            _events.OnNext(new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "a.qml"));
            Update(out changed, out signals, out _);
            Assert.That(changed, Is.Empty, "the properties belonged to the old instance");
            Assert.That(signals.Count, Is.EqualTo(0), "so did the signal that was waiting");
        }

        [Test]
        public void NewFile_ResetsTheOutputs()
        {
            Update();
            _registry.Dispatch(NewPoll().Add(Protocol.Ev.QmlProperty, _handle, QmlWire.Property("a", Protocol.Vt.F32, QmlWire.F(1))).Bytes);
            Update(out var changed, out _, out _);
            Assert.That(changed, Has.Count.EqualTo(1));
            Update(out changed, out _, out _, file: System.IO.Path.Combine(_dir, "b.qml"));
            Assert.That(changed, Is.Empty);
        }

        [Test]
        public void Outputs_Properties_NewInstanceOnlyWhenSomethingChanged()
        {
            Update(out var first, out _, out _);
            Assert.That(first, Is.Empty);
            _registry.Dispatch(NewPoll().Add(Protocol.Ev.QmlProperty, _handle, QmlWire.Property("progress", Protocol.Vt.F32, QmlWire.F(0.5f))).Bytes);
            Update(out var second, out _, out _);
            Assert.That(second, Is.EqualTo(new Dictionary<string, object> { ["progress"] = 0.5f }));
            Update(out var third, out _, out _);
            Assert.That(third, Is.SameAs(second), "nothing new: the same dictionary");
            _registry.Dispatch(NewPoll().Add(Protocol.Ev.QmlProperty, _handle, QmlWire.Property("progress", Protocol.Vt.F32, QmlWire.F(0.75f))).Bytes);
            Update(out var fourth, out _, out _);
            Assert.That(fourth, Is.Not.SameAs(second));
            Assert.That(fourth["progress"], Is.EqualTo(0.75f));
            Assert.That(second["progress"], Is.EqualTo(0.5f), "an earlier output is not changed afterwards");
        }

        [Test]
        public void Outputs_Signals_OnceThenEmpty()
        {
            Update();
            _registry.Dispatch(NewPoll()
                .Add(Protocol.Ev.QmlSignal, _handle, QmlWire.Signal("clicked", (Protocol.Vt.I32, QmlWire.I32(1))))
                .Add(Protocol.Ev.QmlSignal, _handle, QmlWire.Signal("clicked", (Protocol.Vt.I32, QmlWire.I32(2)))).Bytes);
            Update(out _, out var signals, out _);
            Assert.That(signals.Select(s => (s.Name, s.Arguments[0])), Is.EqualTo(new[] { ("clicked", (object)1), ("clicked", 2) }));
            Update(out _, out signals, out _);
            Assert.That(signals.Count, Is.EqualTo(0));
        }

        [Test]
        public void Error_UnsupportedValues_AreListed_AndIgnored()
        {
            var d = new Dictionary<string, object> { ["good"] = 1f, ["bad"] = 5L };
            var e = Update(out _, out _, out var error, d);
            Assert.That(e.Properties.Count, Is.EqualTo(1));
            Assert.That(error, Does.Contain("Property 'bad': type Int64 is not supported"));
            Assert.That(Update(out _, out _, out var again, d), Is.Not.Null);
            Assert.That(again, Is.EqualTo(error));
            Update(out _, out _, out error, new() { ["good"] = 1f });
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void Error_FromQt_IsShown_AndClears()
        {
            Update();
            _registry.Dispatch(NewPoll().Error(_handle, "a.qml:3:5: Unexpected token").Bytes);
            Update(out _, out _, out var error);
            Assert.That(error, Is.EqualTo("a.qml:3:5: Unexpected token"));
            _registry.Dispatch(NewPoll().Error(_handle, "").Bytes);
            Update(out _, out _, out error);
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void Error_ValueProblemsAndQtError_AreJoined()
        {
            Update();
            _registry.Dispatch(NewPoll().Error(_handle, "progress: the root has no such property").Bytes);
            Update(out _, out _, out var error, new() { ["x"] = 5L });
            var lines = error.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.That(lines, Has.Length.EqualTo(2));
            Assert.That(lines[0], Does.StartWith("Property 'x': type Int64"));
            Assert.That(lines[1], Is.EqualTo("progress: the root has no such property"));
        }
    }
}
