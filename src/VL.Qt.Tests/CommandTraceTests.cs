using System;
using System.IO;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
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
            w.Clear();
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
