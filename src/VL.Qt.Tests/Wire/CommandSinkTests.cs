using System;
using System.Collections.Generic;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    public class CommandSinkTests
    {
        private static readonly Matrix s_mat = Matrix.RotationZ(0.5f) * Matrix.Translation(10, 20, 0);

        // One call per interface method, all through ICommandSink.
        private static void WriteAll(ICommandSink s)
        {
            s.Create(1, Protocol.ElementType.Rectangle);
            s.Reparent(1, 0, 0);
            s.SetF32(1, Protocol.PropertyId.Opacity, 0.5f);
            s.SetI32(1, 300, -7);
            s.SetBool(1, Protocol.PropertyId.Debug, true);
            s.SetColor(1, Protocol.PropertyId.Color, 1, 0, 0, 1);
            s.SetRect(1, Protocol.MultiEffectKey.PaddingRect, 1, 2, 3, 4);
            s.SetMat4(1, Protocol.PropertyId.Transform, in s_mat);
            s.SetString(1, 301, "Hällo".AsSpan());
            s.SetRef(1, 302, 2);
            s.Bind(1, 5);
            s.Destroy(1);
        }

        [Test]
        public void CommandWriterThroughInterfaceMatchesDirectCalls()
        {
            var viaInterface = new CommandWriter();
            WriteAll(viaInterface);

            var direct = new CommandWriter();
            direct.Create(1, Protocol.ElementType.Rectangle);
            direct.Reparent(1, 0, 0);
            direct.SetF32(1, Protocol.PropertyId.Opacity, 0.5f);
            direct.SetI32(1, 300, -7);
            direct.SetBool(1, Protocol.PropertyId.Debug, true);
            direct.SetColor(1, Protocol.PropertyId.Color, 1, 0, 0, 1);
            direct.SetRect(1, Protocol.MultiEffectKey.PaddingRect, 1, 2, 3, 4);
            direct.SetMat4(1, Protocol.PropertyId.Transform, in s_mat);
            direct.SetString(1, 301, "Hällo");
            direct.SetRef(1, 302, 2);
            direct.Bind(1, 5);
            direct.Destroy(1);

            Assert.That(viaInterface.Count, Is.EqualTo(12));
            Assert.That(viaInterface.Written.ToArray(), Is.EqualTo(direct.Written.ToArray()));
        }

        [Test]
        public void StrideExtensionsMatchFloatOverloads()
        {
            ICommandSink a = new CommandWriter(), b = new CommandWriter();
            a.SetColor(1, Protocol.PropertyId.Color, new Color4(0.1f, 0.2f, 0.3f, 0.4f));
            a.SetRect(1, Protocol.MultiEffectKey.PaddingRect, new RectangleF(1, 2, 3, 4));
            a.SetString(1, 301, (string?)null);
            b.SetColor(1, Protocol.PropertyId.Color, 0.1f, 0.2f, 0.3f, 0.4f);
            b.SetRect(1, Protocol.MultiEffectKey.PaddingRect, 1, 2, 3, 4);
            b.SetString(1, 301, ReadOnlySpan<char>.Empty);
            Assert.That(((CommandWriter)a).Written.ToArray(), Is.EqualTo(((CommandWriter)b).Written.ToArray()));
        }

        [Test]
        public void CommandWriterThroughInterfaceDoesNotAllocate()
        {
            var writer = new CommandWriter();
            ICommandSink sink = writer;
            WriteAll(sink); // warm-up: grow the buffer
            writer.Clear();

            Assert.That(Allocations.Fewest(() =>
            {
                writer.Clear();
                for (int i = 0; i < 10; i++)
                    WriteAll(sink);
            }), Is.EqualTo(0));
        }

        [Test]
        public void RecordingSinkRecordsReadableLines()
        {
            var rec = new RecordingSink();
            WriteAll(rec);
            Assert.That(rec.Count, Is.EqualTo(12));
            Assert.That(rec.Commands[0], Is.EqualTo($"CREATE 1 Rectangle"));
            Assert.That(rec.Commands[1], Is.EqualTo("REPARENT 1 0 0"));
            Assert.That(rec.Commands[2], Is.EqualTo($"SET 1 Opacity f32(0.5)"));
            Assert.That(rec.Commands[5], Is.EqualTo($"SET 1 Color color(1,0,0,1)"));
            Assert.That(rec.Commands[6], Is.EqualTo($"SET 1 PaddingRect rect(1,2,3,4)"));
            Assert.That(rec.Commands[8], Is.EqualTo("SET 1 301 string(Hällo)"));
            Assert.That(rec.Commands[10], Is.EqualTo("BIND 1 5"));
            Assert.That(rec.Commands[11], Is.EqualTo("DESTROY 1"));
            Assert.That(rec.OfOp("SET"), Has.Exactly(8).Items);
        }
    }
}
