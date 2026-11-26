using System;
using System.IO;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    public class CommandWriterTests
    {
        private static byte[] Write(Action<CommandWriter> write)
        {
            var w = new CommandWriter();
            write(w);
            return w.Written.ToArray();
        }

        private static string Hex(byte[] b) => Convert.ToHexString(b);

        // Little-endian helpers for expected byte strings.
        private static string U16(int v) => $"{v & 0xFF:X2}{(v >> 8) & 0xFF:X2}";
        private static string U32(uint v) => Hex(BitConverter.GetBytes(v));
        private static string F32(float v) => Hex(BitConverter.GetBytes(v));
        private static string Hdr(ushort op, int len) => U16(op) + U16(0) + U32((uint)len);
        private static string SetHdr(int valueLen, uint id, ushort prop, ushort vt) => Hdr(Protocol.Op.Set, 8 + valueLen) + U32(id) + U16(prop) + U16(vt);

        [Test]
        public void Create() =>
            Assert.That(Hex(Write(w => w.Create(7, Protocol.ElementType.Rectangle))),
                Is.EqualTo(Hdr(Protocol.Op.Create, 6) + U32(7) + U16(Protocol.ElementType.Rectangle) + "0000"));

        [Test]
        public void Destroy() =>
            Assert.That(Hex(Write(w => w.Destroy(0x01020304))), Is.EqualTo(Hdr(Protocol.Op.Destroy, 4) + "04030201"));

        [Test]
        public void Reparent() =>
            Assert.That(Hex(Write(w => w.Reparent(5, Protocol.RootId, 3))),
                Is.EqualTo(Hdr(Protocol.Op.Reparent, 12) + U32(5) + U32(0) + U32(3)));

        [Test]
        public void Bind() =>
            Assert.That(Hex(Write(w => w.Bind(9, 42))),
                Is.EqualTo(Hdr(Protocol.Op.Bind, 8) + U32(9) + U32(42)));

        [Test]
        public void ClearColor() =>
            Assert.That(Hex(Write(w => w.ClearColor(new Color4(0.1f, 0.2f, 0.3f, 1f)))),
                Is.EqualTo(Hdr(Protocol.Op.SetClearColor, 16) + F32(0.1f) + F32(0.2f) + F32(0.3f) + F32(1f)));

        [Test]
        public void SetF32() =>
            Assert.That(Hex(Write(w => w.SetF32(1, Protocol.PropertyId.Opacity, 0.5f))),
                Is.EqualTo(SetHdr(4, 1, Protocol.PropertyId.Opacity, Protocol.ValueType.F32) + "0000003F"));

        [Test]
        public void SetI32() =>
            Assert.That(Hex(Write(w => w.SetI32(1, 300, -2))),
                Is.EqualTo(SetHdr(4, 1, 300, Protocol.ValueType.I32) + "FEFFFFFF"));

        [Test]
        public void SetBool() =>
            Assert.That(Hex(Write(w => { w.SetBool(1, Protocol.PropertyId.Debug, true); w.SetBool(2, Protocol.PropertyId.Clip, false); })),
                Is.EqualTo(SetHdr(1, 1, Protocol.PropertyId.Debug, Protocol.ValueType.Bool) + "01000000"
                         + SetHdr(1, 2, Protocol.PropertyId.Clip, Protocol.ValueType.Bool) + "00000000"));

        [Test]
        public void SetColor() =>
            Assert.That(Hex(Write(w => w.SetColor(3, Protocol.PropertyId.Color, new Color4(1f, 0.5f, 0.25f, 0.75f)))),
                Is.EqualTo(SetHdr(16, 3, Protocol.PropertyId.Color, Protocol.ValueType.Color) + F32(1f) + F32(0.5f) + F32(0.25f) + F32(0.75f)));

        [Test]
        public void SetRect() =>
            Assert.That(Hex(Write(w => w.SetRect(3, Protocol.MultiEffectKey.PaddingRect, new RectangleF(10, 20, 300, 400)))),
                Is.EqualTo(SetHdr(16, 3, Protocol.MultiEffectKey.PaddingRect, Protocol.ValueType.Rectf) + F32(10) + F32(20) + F32(300) + F32(400)));

        [Test]
        public void SetVec2_Size() =>
            Assert.That(Hex(Write(w => w.SetVec2(3, Protocol.PropertyId.Size, 300, 400))),
                Is.EqualTo(SetHdr(8, 3, Protocol.PropertyId.Size, Protocol.ValueType.Vec2) + F32(300) + F32(400)));

        [Test]
        public void SetMat4_RowMajorLikeStride()
        {
            var m = Matrix.Translation(1, 2, 3); // row-major: translation in M41..M43
            var expected = SetHdr(64, 4, Protocol.PropertyId.Transform, Protocol.ValueType.Mat4);
            foreach (var f in m.ToArray()) expected += F32(f);
            Assert.That(Hex(Write(w => w.SetMat4(4, Protocol.PropertyId.Transform, m))), Is.EqualTo(expected));
            Assert.That(m.M41, Is.EqualTo(1f)); // sanity: ToArray order M11..M44
            Assert.That(Hex(Write(w => w.SetMat4(4, Protocol.PropertyId.Transform, m.ToArray()))), Is.EqualTo(expected));
        }

        [Test]
        public void SetF32Array_CountAndFloats()
        {
            Assert.That(Hex(Write(w => w.SetF32Array(3, Protocol.PropertyId.ShapePoints, new[] { 1f, 2f, 3f }))),
                Is.EqualTo(SetHdr(16, 3, Protocol.PropertyId.ShapePoints, Protocol.ValueType.F32Array) + U32(3) + F32(1f) + F32(2f) + F32(3f)));
            Assert.That(Hex(Write(w => w.SetF32Array(3, Protocol.PropertyId.ShapeDash, ReadOnlySpan<float>.Empty))),
                Is.EqualTo(SetHdr(4, 3, Protocol.PropertyId.ShapeDash, Protocol.ValueType.F32Array) + U32(0)));
        }

        [Test]
        public void SetF32Array_AboveTheFormerU16Limit_IsOneCommand()
        {
            var bytes = Write(w => w.SetF32Array(1, Protocol.PropertyId.ShapePoints, new float[100_000]));
            Assert.That(bytes.Length, Is.EqualTo(Protocol.HeaderSize + Protocol.SetFieldsSize + 4 + 4 * 100_000));
        }

        [TestCase(0, "")]
        [TestCase(1, "000000")]
        [TestCase(2, "0000")]
        [TestCase(3, "00")]
        [TestCase(4, "")]
        public void SetPath_PadsVerbsToFourBytes(int verbCount, string padding)
        {
            // Move/Line verbs take one point each.
            var verbs = new byte[verbCount];
            for (int i = 0; i < verbCount; i++)
                verbs[i] = i == 0 ? Protocol.Verb.Move : Protocol.Verb.Line;
            var points = new float[verbCount * 2];
            for (int i = 0; i < points.Length; i++)
                points[i] = i + 0.5f;
            int paddedVerbs = (verbCount + 3) & ~3;
            var expected = SetHdr(8 + paddedVerbs + 8 * verbCount, 5, Protocol.PropertyId.ShapePath, Protocol.ValueType.Path)
                + U32((uint)verbCount) + U32((uint)verbCount) + Hex(verbs) + padding;
            foreach (var f in points)
                expected += F32(f);
            Assert.That(Hex(Write(w => w.SetPath(5, Protocol.PropertyId.ShapePath, verbs, points))), Is.EqualTo(expected));
        }

        [Test]
        public void SetPath_AboveTheFormerU16Limit_IsOneCommand()
        {
            int points = 50_000;
            var bytes = Write(w => w.SetPath(1, Protocol.PropertyId.ShapePath, new byte[points], new float[points * 2]));
            Assert.That(bytes.Length, Is.EqualTo(Protocol.HeaderSize + Protocol.SetFieldsSize + 8 + points + 8 * points));
            var w = new CommandWriter();
            Assert.Throws<ArgumentException>(() => w.SetPath(1, Protocol.PropertyId.ShapePath, new byte[1], new float[3]));
        }

        [Test]
        public void SetString_Utf8WithPadding()
        {
            // "Aä" = 41 C3 A4 → 3 bytes: payload 8 + 4 + 3 = 15, padded to 20 total.
            Assert.That(Hex(Write(w => w.SetString(2, 257, "Aä"))),
                Is.EqualTo(SetHdr(7, 2, 257, Protocol.ValueType.String) + U32(3) + "41C3A4" + "00"));
            Assert.That(Hex(Write(w => w.SetString(2, 257, (string?)null))),
                Is.EqualTo(SetHdr(4, 2, 257, Protocol.ValueType.String) + U32(0)));
        }

        // SET_STYLE: header, u32 id, u16 count, u16 flags, entries (u16 field, u8 state, u8 vt, value), 4-byte aligned.
        private static string Entry(ushort field, byte state, ushort vt, string value) => U16(field) + state.ToString("X2") + vt.ToString("X2") + value;

        [Test]
        public void SetStyle_ColorWithClear() =>
            Assert.That(Hex(Write(w =>
            {
                w.BeginStyle(5, Protocol.StyleSet.Clear);
                w.StyleColor(Protocol.StyleField.FrameColor, Protocol.StyleState.Hovered, 0.1f, 0.2f, 0.3f, 1f);
                w.EndStyle();
            })),
            Is.EqualTo(Hdr(Protocol.Op.SetStyle, 8 + 20) + U32(5) + U16(1) + U16(Protocol.StyleSet.Clear)
                + Entry(Protocol.StyleField.FrameColor, 1, Protocol.ValueType.Color, F32(0.1f) + F32(0.2f) + F32(0.3f) + F32(1f))));

        [Test]
        public void SetStyle_MultipleEntries_AllTypes_AreAligned()
        {
            var w = new CommandWriter();
            w.BeginStyle(9, 0);
            w.StyleF32(Protocol.StyleField.FrameRadius, Protocol.StyleState.Normal, 2f);
            w.StyleI32(Protocol.StyleField.TextFontWeight, Protocol.StyleState.Disabled, 700);
            w.StyleString(Protocol.StyleField.TextFontFamily, Protocol.StyleState.Normal, "Aä"); // 3 bytes -> 4 + 4 + 3 padded to 12
            w.StyleString(Protocol.StyleField.TextFontFamily, Protocol.StyleState.Checked, "");
            w.StyleColor(Protocol.StyleField.TextColor, Protocol.StyleState.Pressed, 1f, 0f, 0f, 0.5f);
            w.EndStyle();
            w.Create(1, Protocol.ElementType.Button); // the next command starts right after the style

            var expectedEntries =
                Entry(Protocol.StyleField.FrameRadius, 0, Protocol.ValueType.F32, F32(2f))
                + Entry(Protocol.StyleField.TextFontWeight, 3, Protocol.ValueType.I32, "BC020000")
                + Entry(Protocol.StyleField.TextFontFamily, 0, Protocol.ValueType.String, U32(3) + "41C3A4" + "00")
                + Entry(Protocol.StyleField.TextFontFamily, 4, Protocol.ValueType.String, U32(0))
                + Entry(Protocol.StyleField.TextColor, 2, Protocol.ValueType.Color, F32(1f) + F32(0f) + F32(0f) + F32(0.5f));
            int payload = 8 + expectedEntries.Length / 2;
            Assert.That(payload % 4, Is.EqualTo(0));
            Assert.That(Hex(w.Written.ToArray()), Is.EqualTo(
                Hdr(Protocol.Op.SetStyle, payload) + U32(9) + U16(5) + U16(0) + expectedEntries
                + Hdr(Protocol.Op.Create, 6) + U32(1) + U16(Protocol.ElementType.Button) + "0000"));
            Assert.That(w.Count, Is.EqualTo(2));
        }

        [Test]
        public void SetStyle_EmptyAndMisuse()
        {
            Assert.That(Hex(Write(w => { w.BeginStyle(3, 0); w.EndStyle(); })),
                Is.EqualTo(Hdr(Protocol.Op.SetStyle, 8) + U32(3) + U16(0) + U16(0)));

            var w2 = new CommandWriter();
            Assert.Throws<InvalidOperationException>(() => w2.StyleF32(1, 0, 1f));
            Assert.Throws<InvalidOperationException>(() => w2.EndStyle());
            w2.BeginStyle(1, 0);
            Assert.Throws<InvalidOperationException>(() => w2.BeginStyle(2, 0));
        }

        [Test]
        public void SetRef() =>
            Assert.That(Hex(Write(w => w.SetRef(6, 300, 11))),
                Is.EqualTo(SetHdr(4, 6, 300, Protocol.ValueType.Ref) + U32(11)));

        [Test]
        public void EveryCommandIsAligned_AndClearKeepsCapacity()
        {
            var w = new CommandWriter(64);
            w.Create(1, Protocol.ElementType.Group);
            w.SetBool(1, Protocol.PropertyId.Debug, true);
            w.SetString(1, 257, "abcde");
            w.Bind(1, 2);
            Assert.That(w.Length % Protocol.CommandAlign, Is.Zero);
            Assert.That(w.Count, Is.EqualTo(4));
            for (int i = 0; i < 100; i++) w.SetF32(1, 1, i); // forces growth
            int cap = w.Capacity;
            w.Clear();
            Assert.That(w.Length, Is.Zero);
            Assert.That(w.Capacity, Is.EqualTo(cap));
        }

        [Test]
        public void LongString_IsOneCommand()
        {
            // 100 KB does not fit a u16 payload length; the u32 length of the header carries it.
            var text = new string('x', 100_000);
            var bytes = Write(w => w.SetString(2, 257, text));
            Assert.That(bytes.Length, Is.EqualTo(Protocol.HeaderSize + 8 + 4 + 100_000));
            Assert.That(Hex(bytes.AsSpan(0, Protocol.HeaderSize + 8 + 4).ToArray()),
                Is.EqualTo(SetHdr(4 + 100_000, 2, 257, Protocol.ValueType.String) + U32(100_000)));
        }

        [Test]
        public void PayloadOverSanityCap_Throws()
        {
            var w = new CommandWriter();
            // 8 bytes of SET fields + 4 of length + the string: one byte over the cap.
            var text = new string('x', Protocol.MaxCommandPayload - 12 + 1);
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetString(1, 257, text));
            Assert.That(w.Length, Is.Zero, "failed command must not leave partial bytes");
        }

        // --- compound commands: what the writer lets through while one is open ---

        /// <summary>The compound commands (and open links) a writer can be in, each with the call that closes it.</summary>
        private static readonly (string Name, Action<CommandWriter> Begin, Action<CommandWriter> End)[] s_open =
        {
            ("style", w => w.BeginStyle(1, 0), w => w.EndStyle()),
            ("uniforms", w => w.BeginUniforms(1, 0), w => w.EndUniforms()),
            ("qml", w => w.BeginQmlProperties(1, 0), w => w.EndQmlProperties()),
            ("chain", w => w.BeginEffectChain(1), w => w.EndEffectChain()),
            ("multi", w => { w.BeginEffectChain(1); w.BeginMultiLink(); }, w => { w.EndLink(); w.EndEffectChain(); }),
            ("shader", w => { w.BeginEffectChain(1); w.BeginEffectChainLink(Protocol.EffectTemplate.Single, new ulong[] { 5 }, 0, 0, 0, 0, 0); }, w => { w.EndLink(); w.EndEffectChain(); }),
        };

        public static readonly string[] OpenNames = Array.ConvertAll(s_open, o => o.Name);

        private static (Action<CommandWriter> Begin, Action<CommandWriter> End) OpenOf(string name)
        {
            foreach (var o in s_open)
                if (o.Name == name)
                    return (o.Begin, o.End);
            throw new ArgumentException(name);
        }

        [TestCaseSource(nameof(OpenNames))]
        public void PlainCommandInsideOpenCommand_ThrowsAndChangesNothing(string open)
        {
            var (begin, end) = OpenOf(open);
            var w = new CommandWriter();
            begin(w);
            int length = w.Length, count = w.Count;

            Assert.Throws<InvalidOperationException>(() => w.Create(2, Protocol.ElementType.Rectangle));
            Assert.Throws<InvalidOperationException>(() => w.Destroy(2));
            Assert.Throws<InvalidOperationException>(() => w.SetF32(2, Protocol.PropertyId.Opacity, 1));
            Assert.Throws<InvalidOperationException>(() => w.SetString(2, 257, "x"));
            Assert.Throws<InvalidOperationException>(() => w.ClearColor(0, 0, 0, 1));
            Assert.Throws<InvalidOperationException>(() => w.ResetScene());
            Assert.That(w.Length, Is.EqualTo(length));
            Assert.That(w.Count, Is.EqualTo(count));

            end(w);
            w.SetF32(2, Protocol.PropertyId.Opacity, 1); // after the End* it is a plain writer again
            Assert.That(w.Count, Is.EqualTo(count + 1));
        }

        [TestCaseSource(nameof(OpenNames))]
        public void BeginInsideOpenCommand_Throws(string open)
        {
            var (begin, end) = OpenOf(open);
            var w = new CommandWriter();
            begin(w);
            int length = w.Length;

            Assert.Throws<InvalidOperationException>(() => w.BeginStyle(2, 0));
            Assert.Throws<InvalidOperationException>(() => w.BeginUniforms(2, 0));
            Assert.Throws<InvalidOperationException>(() => w.BeginQmlProperties(2, 0));
            Assert.Throws<InvalidOperationException>(() => w.BeginEffectChain(2));
            Assert.That(w.Length, Is.EqualTo(length));

            end(w);
        }

        [Test]
        public void LinkInsideALink_Throws()
        {
            var w = new CommandWriter();
            w.BeginEffectChain(1);
            w.BeginMultiLink();
            Assert.Throws<InvalidOperationException>(() => w.BeginMultiLink());
            Assert.Throws<InvalidOperationException>(() => w.BeginEffectChainLink(Protocol.EffectTemplate.Single, new ulong[] { 5 }, 0, 0, 0, 0, 0));
            w.EndLink();
            Assert.Throws<InvalidOperationException>(() => w.EndLink());
            w.EndEffectChain();
        }

        [Test]
        public void EntriesOfAnotherCommand_Throw()
        {
            var w = new CommandWriter();

            w.BeginStyle(1, 0);
            Assert.Throws<InvalidOperationException>(() => w.UniformF32("a", 1));
            Assert.Throws<InvalidOperationException>(() => w.QmlPropertyF32("a", 1));
            Assert.Throws<InvalidOperationException>(() => w.MultiEffectF32(Protocol.MultiEffectKey.Blur, 1));
            w.EndStyle();

            w.BeginUniforms(1, 0);
            Assert.Throws<InvalidOperationException>(() => w.StyleF32(1, 0, 1));
            Assert.Throws<InvalidOperationException>(() => w.QmlPropertyF32("a", 1));
            Assert.Throws<InvalidOperationException>(() => w.QmlPropertyString("a", "x"));
            Assert.Throws<InvalidOperationException>(() => w.MultiEffectF32(Protocol.MultiEffectKey.Blur, 1));
            w.EndUniforms();

            w.BeginQmlProperties(1, 0);
            Assert.Throws<InvalidOperationException>(() => w.StyleF32(1, 0, 1));
            Assert.Throws<InvalidOperationException>(() => w.UniformF32("a", 1));
            Assert.Throws<InvalidOperationException>(() => w.UniformMat4("a", Matrix.Identity));
            Assert.Throws<InvalidOperationException>(() => w.MultiEffectF32(Protocol.MultiEffectKey.Blur, 1));
            w.EndQmlProperties();

            w.BeginEffectChain(1);
            w.BeginEffectChainLink(Protocol.EffectTemplate.Single, new ulong[] { 5 }, 0, 0, 0, 0, 0);
            Assert.Throws<InvalidOperationException>(() => w.QmlPropertyString("a", "x"));
            Assert.Throws<InvalidOperationException>(() => w.QmlPropertyRect("a", 0, 0, 1, 1));
            Assert.Throws<InvalidOperationException>(() => w.MultiEffectF32(Protocol.MultiEffectKey.Blur, 1));
            Assert.Throws<InvalidOperationException>(() => w.StyleF32(1, 0, 1));
            w.UniformF32("a", 1); // the one entry kind a SHADER link takes
            w.EndLink();
            w.EndEffectChain();
        }

        [Test]
        public void EndOfAnotherCommand_Throws()
        {
            var w = new CommandWriter();
            Assert.Throws<InvalidOperationException>(() => w.EndUniforms());
            Assert.Throws<InvalidOperationException>(() => w.EndQmlProperties());
            Assert.Throws<InvalidOperationException>(() => w.EndLink());

            w.BeginUniforms(1, 0);
            Assert.Throws<InvalidOperationException>(() => w.EndQmlProperties()); // no alias of EndUniforms
            Assert.Throws<InvalidOperationException>(() => w.EndStyle());
            Assert.Throws<InvalidOperationException>(() => w.EndEffectChain());
            w.EndUniforms();

            w.BeginQmlProperties(1, 0);
            Assert.Throws<InvalidOperationException>(() => w.EndUniforms());
            w.EndQmlProperties();
        }

        [Test]
        public void QmlPropertyEntries_AreEncodedLikeUniformEntries()
        {
            void Entries(CommandWriter w, bool qml)
            {
                if (qml)
                {
                    w.QmlPropertyF32("f", 1.5f);
                    w.QmlPropertyI32("i", -3);
                    w.QmlPropertyBool("b", true);
                    w.QmlPropertyColor("c", 1, 0.5f, 0.25f, 0.75f);
                    w.QmlPropertyVec2("v2", 1, 2);
                    w.QmlPropertyVec3("v3", 1, 2, 3);
                    w.QmlPropertyVec4("v4", 1, 2, 3, 4);
                }
                else
                {
                    w.UniformF32("f", 1.5f);
                    w.UniformI32("i", -3);
                    w.UniformBool("b", true);
                    w.UniformColor("c", 1, 0.5f, 0.25f, 0.75f);
                    w.UniformVec2("v2", 1, 2);
                    w.UniformVec3("v3", 1, 2, 3);
                    w.UniformVec4("v4", 1, 2, 3, 4);
                }
            }

            var uniforms = Write(w => { w.BeginUniforms(7, 0); Entries(w, false); w.EndUniforms(); });
            var qml = Write(w => { w.BeginQmlProperties(7, 0); Entries(w, true); w.EndQmlProperties(); });
            Assert.That(uniforms[0] | uniforms[1] << 8, Is.EqualTo((int)Protocol.Op.SetUniforms));
            Assert.That(qml[0] | qml[1] << 8, Is.EqualTo((int)Protocol.Op.SetQmlProperties));
            Assert.That(qml[2..], Is.EqualTo(uniforms[2..]), "everything but the op is the same");
        }

        [Test]
        public void QmlPropertyRectAndString()
        {
            var hex = Write(w =>
            {
                w.BeginQmlProperties(7, 0);
                w.QmlPropertyRect("r", 1, 2, 3, 4);
                w.QmlPropertyString("s", "hä");
                w.EndQmlProperties();
            });
            var rect = U16(Protocol.ValueType.Rectf) + U16(1) + "72" + "000000" + F32(1) + F32(2) + F32(3) + F32(4);
            var text = U16(Protocol.ValueType.String) + U16(1) + "73" + "000000" + U32(3) + "68C3A4" + "00";
            Assert.That(Hex(hex), Is.EqualTo(Hdr(Protocol.Op.SetQmlProperties, 8 + 24 + 16) + U32(7) + U16(2) + U16(0) + rect + text));
        }

        [Test]
        public void ThrowingEntry_LeavesTheOpenCommandAsItWas()
        {
            var expected = Write(w => { w.BeginUniforms(1, 0); w.UniformF32("a", 1); w.UniformF32("b", 2); w.EndUniforms(); });
            var actual = Write(w =>
            {
                w.BeginUniforms(1, 0);
                w.UniformF32("a", 1);
                Assert.Throws<ArgumentException>(() => w.UniformF32("", 9));
                Assert.Throws<ArgumentException>(() => w.UniformF32(new string('x', Protocol.UniformNameMax + 1), 9));
                w.UniformF32("b", 2);
                w.EndUniforms();
            });
            Assert.That(Hex(actual), Is.EqualTo(Hex(expected)));
        }

        [Test]
        public void Clear_AfterAThrownEntry_AllowsANewCommand()
        {
            var w = new CommandWriter();
            w.BeginUniforms(1, 0);
            Assert.Throws<ArgumentException>(() => w.UniformF32("", 1));
            Assert.Throws<InvalidOperationException>(() => w.BeginUniforms(2, 0)); // still open

            w.Clear();
            Assert.That(w.Length, Is.Zero);
            Assert.That(w.Count, Is.Zero);
            w.BeginUniforms(2, 0);
            w.UniformF32("a", 1);
            w.EndUniforms();
            Assert.That(Hex(w.Written.ToArray()), Is.EqualTo(Hex(Write(x => { x.BeginUniforms(2, 0); x.UniformF32("a", 1); x.EndUniforms(); }))));
        }

        [TestCaseSource(nameof(OpenNames))]
        public void Abort_DropsTheOpenCommand(string open)
        {
            var (begin, _) = OpenOf(open);
            var w = new CommandWriter();
            w.Create(1, Protocol.ElementType.Rectangle);
            var created = w.Written.ToArray();

            begin(w);
            w.Abort();
            Assert.That(w.Written.ToArray(), Is.EqualTo(created));
            Assert.That(w.Count, Is.EqualTo(1));

            w.Abort(); // nothing open: no-op
            Assert.That(w.Written.ToArray(), Is.EqualTo(created));
            Assert.That(w.Count, Is.EqualTo(1));

            w.BeginStyle(2, 0); // and the writer is usable again
            w.EndStyle();
            Assert.That(w.Count, Is.EqualTo(2));
        }

        [Test]
        public void EntryCountOverflow_DropsTheCommandAndClosesIt()
        {
            var w = new CommandWriter();
            w.BeginStyle(1, 0);
            for (int i = 0; i <= ushort.MaxValue; i++)
                w.StyleF32(1, 0, i);
            Assert.Throws<InvalidOperationException>(() => w.EndStyle());
            Assert.That(w.Length, Is.Zero);
            Assert.That(w.Count, Is.Zero);
            w.Create(1, Protocol.ElementType.Rectangle); // not open any more
            Assert.That(w.Count, Is.EqualTo(1));
        }

        [Test]
        public void NoManagedAllocationPerFrame()
        {
            var w = new CommandWriter();
            var m = Matrix.Identity;
            const string text = "Hello Qt";

            void Frame(int f)
            {
                w.Clear();
                for (uint i = 1; i <= 10; i++)
                {
                    w.SetVec2(i, Protocol.PropertyId.Size, 100, 50 + f);
                    w.SetColor(i, Protocol.PropertyId.Color, 1, 0, 0, 1);
                    w.SetF32(i, Protocol.PropertyId.Opacity, 0.5f);
                    w.SetBool(i, Protocol.PropertyId.Debug, true);
                    w.SetI32(i, 300, f);
                    w.SetMat4(i, Protocol.PropertyId.Transform, m);
                    w.SetString(i, 257, text);
                    w.SetRef(i, 258, i + 1);
                    w.Reparent(i, 0, i);
                    w.Bind(i, i);
                }
                w.EndFrame();
            }

            Frame(0); // warm-up: buffer reaches working size, JIT
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 1000; f++) Frame(f);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestContext.Out.WriteLine($"1000 frames x {w.Count} commands ({w.Length} bytes/frame): {allocated} bytes allocated");
            Assert.That(w.Count, Is.EqualTo(100));
            Assert.That(allocated, Is.LessThan(1024));
        }
    }
}
