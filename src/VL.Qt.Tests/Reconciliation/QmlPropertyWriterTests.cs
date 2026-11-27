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
using VL.Qt.Reconciliation;
using VL.Qt.Tests.Support;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests.Reconciliation
{
    /// <summary>QmlPropertyWriter: the diff by name and the encoding of every value type into SET_QML_PROPERTIES.</summary>
    public class QmlPropertyWriterTests
    {
        private static QmlProperties Props(params (string Name, object Value)[] entries)
        {
            var d = new Dictionary<string, object>();
            foreach (var (n, v) in entries)
                d[n] = v;
            return QmlProperties.From(d);
        }

        private static string[] Write(QmlProperties next, QmlProperties? previous, out int sent)
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
        public void Lists_EachElementType()
        {
            var cmds = Write(Props(
                ("f", new[] { 1f, 2.5f }.ToSpread()),
                ("i", new[] { -1, 7 }.ToSpread()),
                ("b", new[] { true, false, true }.ToSpread()),
                ("s", new[] { "a", "hä" }.ToSpread()),
                ("c", new[] { new Color4(1, 0.5f, 0.25f, 0.75f) }.ToSpread()),
                ("v2", new[] { new Vector2(1, 2), new Vector2(3, 4) }.ToSpread()),
                ("v3", new[] { new Vector3(1, 2, 3) }.ToSpread()),
                ("v4", new[] { new Vector4(1, 2, 3, 4) }.ToSpread()),
                ("r", new[] { new RectangleF(10, 20, 30, 40) }.ToSpread()),
                ("e", Spread<int>.Empty)), null, out int sent);
            Assert.That(sent, Is.EqualTo(10));
            Assert.That(cmds, Is.EqualTo(new[]
            {
                "QML 7 0 f=list<" + Protocol.ValueType.F32 + ">(1,2.5) i=list<" + Protocol.ValueType.I32 + ">(-1,7) b=list<" + Protocol.ValueType.Bool + ">(1,0,1)"
                + " s=list<" + Protocol.ValueType.String + ">(a,hä) c=list<" + Protocol.ValueType.Color + ">(1,0.5,0.25,0.75)"
                + " v2=list<" + Protocol.ValueType.Vec2 + ">(1,2,3,4) v3=list<" + Protocol.ValueType.Vec3 + ">(1,2,3) v4=list<" + Protocol.ValueType.Vec4 + ">(1,2,3,4)"
                + " r=list<" + Protocol.ValueType.Rectf + ">(10,20,30,40) e=list<" + Protocol.ValueType.I32 + ">()",
            }));
        }

        [Test]
        public void List_SameContentOtherInstance_IsNotSentAgain()
        {
            var p = Props(("l", new[] { 1f, 2f }.ToSpread()), ("v", new[] { new Vector2(1, 2) }.ToSpread()));
            Assert.That(Write(Props(("l", new[] { 1f, 2f }.ToSpread()), ("v", new[] { new Vector2(1, 2) }.ToSpread())), p, out int sent), Is.Empty);
            Assert.That(sent, Is.EqualTo(0));
        }

        [Test]
        public void List_Changed_IsSent()
        {
            var p = Props(("l", new[] { 1f, 2f }.ToSpread()), ("o", 1));
            var cmds = Write(Props(("l", new[] { 1f, 2f, 3f }.ToSpread()), ("o", 1)), p, out int sent);
            Assert.That(cmds, Is.EqualTo(new[] { "QML 7 0 l=list<" + Protocol.ValueType.F32 + ">(1,2,3)" }));
            Assert.That(sent, Is.EqualTo(1));
            Assert.That(Write(Props(("l", new[] { 1f, 2f }.ToSpread()), ("o", 1)), Props(("l", new[] { 1, 2 }.ToSpread()), ("o", 1)), out _),
                Has.Length.EqualTo(1), "same numbers, other element type");
        }

        [Test]
        public void Paths_AreSentAsTheirText_AlsoInAList()
        {
            var cmds = Write(Props(("u", new Path("C:/a b.png")), ("ul", new[] { new Path("a.png"), new Path("qrc:/b.png") }.ToSpread())), null, out _);
            Assert.That(cmds, Is.EqualTo(new[] { "QML 7 0 u=string(C:/a b.png) ul=list<" + Protocol.ValueType.String + ">(a.png,qrc:/b.png)" }));
        }

        [Test]
        public void ListElementsOfNullString_AreEmpty()
        {
            var cmds = Write(Props(("s", new[] { "a", null!, "" }.ToSpread())), null, out _);
            Assert.That(cmds, Is.EqualTo(new[] { "QML 7 0 s=list<" + Protocol.ValueType.String + ">(a,,)" }));
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
        public void LargeTable_IsOneCommand_EveryEntryOnce()
        {
            var entries = Enumerable.Range(0, 8).Select(i => ($"p{i}", (object)new string((char)('a' + i), 15000))).ToArray();
            var cmds = Write(Props(entries), null, out int sent);
            Assert.That(sent, Is.EqualTo(8));
            Assert.That(cmds, Has.Length.EqualTo(1));
            Assert.That(cmds[0], Does.StartWith("QML 7 0 "));
            for (int i = 0; i < 8; i++)
                Assert.That(cmds[0], Does.Contain($" p{i}="), $"p{i}");
        }

        [Test]
        public void LargeTable_IsOneCommand_OnTheRealWriter()
        {
            var entries = Enumerable.Range(0, 8).Select(i => ($"p{i}", (object)new string('€', 6000))).ToArray(); // 3 bytes per char, 144 KB
            var w = new CommandWriter();
            Assert.DoesNotThrow(() => QmlPropertyWriter.Write(w, 1, Props(entries), null));
            Assert.That(w.Count, Is.EqualTo(1));
            Assert.That(w.Length, Is.GreaterThan(ushort.MaxValue));
        }

        // --- bytes ---

        private static string Hex(byte[] b) => Convert.ToHexString(b);
        private static string U16(int v) => $"{v & 0xFF:X2}{(v >> 8) & 0xFF:X2}";
        private static string U32(uint v) => Hex(BitConverter.GetBytes(v));
        private static string F32(float v) => Hex(BitConverter.GetBytes(v));

        /// <summary>One entry: u16 value type, u16 name length, name padded so that the block is a multiple of 4, value padded to 4.</summary>
        private static string Entry(ushort type, string name, string valueHex)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var block = Hex(n) + new string('0', 2 * ((4 - (4 + n.Length) % 4) % 4));
            var value = valueHex + new string('0', 2 * ((4 - valueHex.Length / 2 % 4) % 4));
            return U16(type) + U16(n.Length) + block + value;
        }

        private static string Command(uint id, int count, string entries) =>
            U16(Protocol.Op.SetQmlProperties) + U16(0) + U32((uint)(8 + entries.Length / 2)) + U32(id) + U16(count) + U16(0) + entries;

        private static string Encode(params (string Name, object Value)[] entries)
        {
            var w = new CommandWriter();
            QmlPropertyWriter.Write(w, 9, Props(entries), null);
            return Hex(w.Written.ToArray());
        }

        [Test]
        public void Bytes_F32_AndDouble() =>
            Assert.That(Encode(("a", 1.5f), ("bb", 2d)), Is.EqualTo(Command(9, 2,
                Entry(Protocol.ValueType.F32, "a", F32(1.5f)) + Entry(Protocol.ValueType.F32, "bb", F32(2f)))));

        [Test]
        public void Bytes_I32() =>
            Assert.That(Encode(("count", -2)), Is.EqualTo(Command(9, 1, Entry(Protocol.ValueType.I32, "count", "FEFFFFFF"))));

        [Test]
        public void Bytes_Bool() =>
            Assert.That(Encode(("on", true), ("off", false)), Is.EqualTo(Command(9, 2,
                Entry(Protocol.ValueType.Bool, "on", "01") + Entry(Protocol.ValueType.Bool, "off", "00"))));

        [Test]
        public void Bytes_String_Utf8_LengthInBytes()
        {
            const string text = "äö€😀";
            var utf8 = Encoding.UTF8.GetBytes(text);
            Assert.That(utf8.Length, Is.EqualTo(2 + 2 + 3 + 4));
            Assert.That(Encode(("label", text)), Is.EqualTo(Command(9, 1,
                Entry(Protocol.ValueType.String, "label", U32((uint)utf8.Length) + Hex(utf8)))));
        }

        [Test]
        public void Bytes_String_Empty() =>
            Assert.That(Encode(("s", "")), Is.EqualTo(Command(9, 1, Entry(Protocol.ValueType.String, "s", U32(0)))));

        [Test]
        public void Bytes_Color() =>
            Assert.That(Encode(("fill", new Color4(1f, 0.5f, 0.25f, 0.75f))), Is.EqualTo(Command(9, 1,
                Entry(Protocol.ValueType.Color, "fill", F32(1f) + F32(0.5f) + F32(0.25f) + F32(0.75f)))));

        [Test]
        public void Bytes_Vectors() =>
            Assert.That(Encode(("v2", new Vector2(1, 2)), ("v3", new Vector3(1, 2, 3)), ("v4", new Vector4(1, 2, 3, 4))), Is.EqualTo(Command(9, 3,
                Entry(Protocol.ValueType.Vec2, "v2", F32(1) + F32(2))
                + Entry(Protocol.ValueType.Vec3, "v3", F32(1) + F32(2) + F32(3))
                + Entry(Protocol.ValueType.Vec4, "v4", F32(1) + F32(2) + F32(3) + F32(4)))));

        [Test]
        public void Bytes_Rect() =>
            Assert.That(Encode(("box", new RectangleF(10, 20, 300, 400))), Is.EqualTo(Command(9, 1,
                Entry(Protocol.ValueType.Rectf, "box", F32(10) + F32(20) + F32(300) + F32(400)))));

        [Test]
        public void Bytes_NonAsciiName_PaddedByBytes() =>
            Assert.That(Encode(("größe", 1f)), Is.EqualTo(Command(9, 1, Entry(Protocol.ValueType.F32, "größe", F32(1f)))));

        [Test]
        public void Bytes_NothingToSend_WritesNoCommand()
        {
            var w = new CommandWriter();
            QmlPropertyWriter.Write(w, 9, Props(("a", 1f)), Props(("a", 1f)));
            Assert.That(w.Count, Is.EqualTo(0));
        }
    }
}
