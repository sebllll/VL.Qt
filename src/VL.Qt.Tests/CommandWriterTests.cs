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
        public void SetF32Array_Limit()
        {
            var w = new CommandWriter();
            w.SetF32Array(1, Protocol.PropertyId.ShapePoints, new float[Protocol.MaxF32ArrayCount]);
            var before = w.Length;
            Assert.That(before, Is.GreaterThan(Protocol.CappedSetPayload), "the largest array fills the payload to the last 4-byte unit");
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetF32Array(1, Protocol.PropertyId.ShapePoints, new float[Protocol.MaxF32ArrayCount + 1]));
            Assert.That(w.Length, Is.EqualTo(before), "failed command must not leave partial bytes");
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
        public void SetPath_Limit()
        {
            // Grow the verb count until exactly one more would not fit.
            int points = 4000;
            int verbs = points;
            while (Protocol.PathFits(verbs + 1, points))
                verbs++;
            var w = new CommandWriter();
            w.SetPath(1, Protocol.PropertyId.ShapePath, new byte[verbs], new float[points * 2]);
            var before = w.Length;
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetPath(1, Protocol.PropertyId.ShapePath, new byte[verbs + 1], new float[points * 2]));
            Assert.That(w.Length, Is.EqualTo(before), "failed command must not leave partial bytes");
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
        public void EveryCommandIsAligned_AndResetKeepsCapacity()
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
            w.Reset();
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

        [Test]
        public void NoManagedAllocationPerFrame()
        {
            var w = new CommandWriter();
            var m = Matrix.Identity;
            const string text = "Hello Qt";

            void Frame(int f)
            {
                w.Reset();
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

    public class EventReaderTests
    {
        [Test]
        public void IteratesRecords_WithPaddingAndPayload()
        {
            var buf = Convert.FromHexString(
                  "0300" + "0000" + "04000000" + "2A000000"                       // HOVER, no payload
                + "0500" + "0000" + "08000000" + "07000000" + "03000000"          // CLICK, i32 3
                + "0100" + "0000" + "05000000" + "01000000" + "FF" + "000000");   // 1-byte payload + padding

            var r = new EventReader(buf);
            Assert.That(r.MoveNext(), Is.True);
            Assert.That((r.Event, r.FeedbackId, r.Payload.Length), Is.EqualTo((Protocol.EventId.Hover, 42u, 0)));
            Assert.That(r.MoveNext(), Is.True);
            Assert.That((r.Event, r.FeedbackId, r.ReadI32(0)), Is.EqualTo((Protocol.EventId.Click, 7u, 3)));
            Assert.That(r.MoveNext(), Is.True);
            Assert.That((r.Event, r.Payload.Length, r.Payload[0]), Is.EqualTo((Protocol.EventId.Measured, 1, (byte)0xFF)));
            Assert.That(r.MoveNext(), Is.False);
            Assert.That(r.Error, Is.Null);
        }

        [Test]
        public void RecordLongerThan64K()
        {
            // The length is a u32: a payload of 70000 bytes is one record.
            var payload = new byte[70_000];
            var buf = new byte[Protocol.HeaderSize + Protocol.EventFieldsSize + payload.Length];
            BitConverter.GetBytes((ushort)Protocol.EventId.Error).CopyTo(buf, 0);
            BitConverter.GetBytes((uint)(Protocol.EventFieldsSize + payload.Length)).CopyTo(buf, 4);
            BitConverter.GetBytes(9u).CopyTo(buf, Protocol.HeaderSize);

            var r = new EventReader(buf);
            Assert.That(r.MoveNext(), Is.True);
            Assert.That((r.Event, r.FeedbackId, r.Payload.Length), Is.EqualTo((Protocol.EventId.Error, 9u, 70_000)));
            Assert.That(r.MoveNext(), Is.False);
            Assert.That(r.Error, Is.Null);
        }

        [Test]
        public void EmptyBuffer()
        {
            var r = new EventReader(ReadOnlySpan<byte>.Empty);
            Assert.That(r.MoveNext(), Is.False);
            Assert.That(r.Error, Is.Null);
        }

        [TestCase("0300" + "0000" + "04000000")]                 // truncated header
        [TestCase("0300" + "0000" + "00000000" + "2A000000")]    // len < feedbackId
        [TestCase("0300" + "0000" + "10000000" + "2A000000")]    // len exceeds buffer
        public void MalformedRecord_StopsWithError(string hex)
        {
            var r = new EventReader(Convert.FromHexString(hex));
            Assert.That(r.MoveNext(), Is.False);
            Assert.That(r.Error, Is.Not.Null);
        }
    }

    public class CommandTraceTests
    {
        [Test]
        public void TraceFile_NameContainsPid() =>
            Assert.That(Path.GetFileName(CommandWriter.TraceFilePath), Is.EqualTo($"vlqt-trace-{Environment.ProcessId}.bin"));

        // Runs only with VLQT_TRACE_COMMANDS=1 set for the test process.
        [Test]
        public void TraceFile_ContainsLengthPrefixedFrames()
        {
            if (!CommandWriter.TraceEnabled)
                Assert.Ignore("set VLQT_TRACE_COMMANDS=1 to run");
            var w = new CommandWriter();
            w.Destroy(77);
            var frame = w.EndFrame().ToArray();
            w.Reset();
            w.EndFrame(); // empty frame is traced too

            byte[] file;
            using (var fs = new FileStream(CommandWriter.TraceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                file = new byte[fs.Length];
                fs.ReadExactly(file);
            }
            var tail = file.AsSpan(file.Length - (4 + frame.Length + 4));
            Assert.That(BitConverter.ToUInt32(tail), Is.EqualTo((uint)frame.Length));
            Assert.That(tail.Slice(4, frame.Length).ToArray(), Is.EqualTo(frame));
            Assert.That(BitConverter.ToUInt32(tail.Slice(4 + frame.Length)), Is.Zero);
        }
    }
}
