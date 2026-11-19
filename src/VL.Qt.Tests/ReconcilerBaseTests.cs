using System;
using System.Collections.Generic;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Tests
{
    public class IdAllocatorTests
    {
        [Test]
        public void StartsAtOne_NeverZero()
        {
            var ids = new IdAllocator();
            var seen = new HashSet<uint>();
            for (int i = 0; i < 10_000; i++)
            {
                var id = ids.Allocate();
                Assert.That(id, Is.Not.EqualTo(IdAllocator.Root));
                Assert.That(seen.Add(id), Is.True, "id handed out twice while live");
            }
            Assert.That(ids.Count, Is.EqualTo(10_000));
            Assert.That(ids.IsLive(0), Is.False);
        }

        [Test]
        public void FreedIdsAreReused()
        {
            var ids = new IdAllocator();
            var a = ids.Allocate();
            var b = ids.Allocate();
            var c = ids.Allocate();
            Assert.That((a, b, c), Is.EqualTo((1u, 2u, 3u)));

            ids.Free(b);
            ids.Free(a);
            Assert.That(ids.Count, Is.EqualTo(1));
            Assert.That(ids.IsLive(a), Is.False);
            Assert.That(ids.Allocate(), Is.EqualTo(a)); // LIFO
            Assert.That(ids.Allocate(), Is.EqualTo(b));
            Assert.That(ids.Allocate(), Is.EqualTo(4u)); // freelist empty → fresh id
        }

        [Test]
        public void ChurnStaysDense()
        {
            var ids = new IdAllocator();
            var live = new List<uint>();
            for (int i = 0; i < 100; i++)
                live.Add(ids.Allocate());
            for (int frame = 0; frame < 1000; frame++)
            {
                ids.Free(live[frame % 100]);
                live[frame % 100] = ids.Allocate();
            }
            foreach (var id in live)
                Assert.That(id, Is.InRange(1u, 100u));
        }

        [Test]
        public void FreeInvalidThrows()
        {
            var ids = new IdAllocator();
            Assert.Throws<ArgumentException>(() => ids.Free(0));
            Assert.Throws<ArgumentException>(() => ids.Free(5)); // never allocated
            var a = ids.Allocate();
            ids.Free(a);
            Assert.Throws<ArgumentException>(() => ids.Free(a)); // double free
            Assert.That(ids.Count, Is.EqualTo(0));
        }

        [Test]
        public void SteadyStateDoesNotAllocate()
        {
            var ids = new IdAllocator();
            var live = new uint[1000];
            for (int i = 0; i < live.Length; i++)
                live[i] = ids.Allocate();
            for (int i = 0; i < live.Length; i++)
                ids.Free(live[i]);

            Assert.That(Allocations.Fewest(() =>
            {
                for (int i = 0; i < live.Length; i++)
                    live[i] = ids.Allocate();
                for (int i = 0; i < live.Length; i++)
                    ids.Free(live[i]);
            }, frames: 100), Is.EqualTo(0));
        }
    }

    public class CommandSinkTests
    {
        static readonly Matrix s_mat = Matrix.RotationZ(0.5f) * Matrix.Translation(10, 20, 0);

        // One call per interface method, all through ICommandSink.
        static void WriteAll(ICommandSink s)
        {
            s.Create(1, Protocol.Type.Rectangle);
            s.Reparent(1, 0, 0);
            s.SetF32(1, Protocol.Prop.Opacity, 0.5f);
            s.SetI32(1, 300, -7);
            s.SetBool(1, Protocol.Prop.Debug, true);
            s.SetColor(1, Protocol.Prop.Color, 1, 0, 0, 1);
            s.SetRect(1, Protocol.Prop.EffectPaddingRect, 1, 2, 3, 4);
            s.SetMat4(1, Protocol.Prop.Transform, in s_mat);
            s.SetString(1, 301, "Hällo".AsSpan());
            s.SetRef(1, 302, 2);
            s.Bind(1, 5, Protocol.Bind.CancelPress);
            s.Destroy(1);
        }

        [Test]
        public void CommandWriterThroughInterfaceMatchesDirectCalls()
        {
            var viaInterface = new CommandWriter();
            WriteAll(viaInterface);

            var direct = new CommandWriter();
            direct.Create(1, Protocol.Type.Rectangle);
            direct.Reparent(1, 0, 0);
            direct.SetF32(1, Protocol.Prop.Opacity, 0.5f);
            direct.SetI32(1, 300, -7);
            direct.SetBool(1, Protocol.Prop.Debug, true);
            direct.SetColor(1, Protocol.Prop.Color, 1, 0, 0, 1);
            direct.SetRect(1, Protocol.Prop.EffectPaddingRect, 1, 2, 3, 4);
            direct.SetMat4(1, Protocol.Prop.Transform, in s_mat);
            direct.SetString(1, 301, "Hällo");
            direct.SetRef(1, 302, 2);
            direct.Bind(1, 5, Protocol.Bind.CancelPress);
            direct.Destroy(1);

            Assert.That(viaInterface.Count, Is.EqualTo(12));
            Assert.That(viaInterface.Written.ToArray(), Is.EqualTo(direct.Written.ToArray()));
        }

        [Test]
        public void StrideExtensionsMatchFloatOverloads()
        {
            ICommandSink a = new CommandWriter(), b = new CommandWriter();
            a.SetColor(1, Protocol.Prop.Color, new Color4(0.1f, 0.2f, 0.3f, 0.4f));
            a.SetRect(1, Protocol.Prop.EffectPaddingRect, new RectangleF(1, 2, 3, 4));
            a.SetString(1, 301, (string?)null);
            b.SetColor(1, Protocol.Prop.Color, 0.1f, 0.2f, 0.3f, 0.4f);
            b.SetRect(1, Protocol.Prop.EffectPaddingRect, 1, 2, 3, 4);
            b.SetString(1, 301, ReadOnlySpan<char>.Empty);
            Assert.That(((CommandWriter)a).Written.ToArray(), Is.EqualTo(((CommandWriter)b).Written.ToArray()));
        }

        [Test]
        public void CommandWriterThroughInterfaceDoesNotAllocate()
        {
            var writer = new CommandWriter();
            ICommandSink sink = writer;
            WriteAll(sink); // warm-up: grow the buffer
            writer.Reset();

            Assert.That(Allocations.Fewest(() =>
            {
                writer.Reset();
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
            Assert.That(rec.Commands[0], Is.EqualTo($"CREATE 1 {Protocol.Type.Rectangle}"));
            Assert.That(rec.Commands[1], Is.EqualTo("REPARENT 1 0 0"));
            Assert.That(rec.Commands[2], Is.EqualTo($"SET 1 {Protocol.Prop.Opacity} f32(0.5)"));
            Assert.That(rec.Commands[5], Is.EqualTo($"SET 1 {Protocol.Prop.Color} color(1,0,0,1)"));
            Assert.That(rec.Commands[6], Is.EqualTo($"SET 1 {Protocol.Prop.EffectPaddingRect} rect(1,2,3,4)"));
            Assert.That(rec.Commands[8], Is.EqualTo("SET 1 301 string(Hällo)"));
            Assert.That(rec.Commands[10], Is.EqualTo("BIND 1 5 2"));
            Assert.That(rec.Commands[11], Is.EqualTo("DESTROY 1"));
            Assert.That(rec.OfOp("SET"), Has.Exactly(8).Items);
        }
    }

    public class NodeTests
    {
        [Test]
        public void NewNodeIsEmptyAndUnbound()
        {
            var n = new Node(3, ElementType.Rectangle);
            Assert.That(n.Id, Is.EqualTo(3u));
            Assert.That(n.Type, Is.EqualTo(ElementType.Rectangle));
            Assert.That(n.Last, Is.Null);
            Assert.That(n.Children, Is.Empty);
            Assert.That(n.BoundSink.IsNone, Is.True);
        }
    }
}
