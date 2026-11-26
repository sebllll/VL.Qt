using System;
using System.IO;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
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
}
