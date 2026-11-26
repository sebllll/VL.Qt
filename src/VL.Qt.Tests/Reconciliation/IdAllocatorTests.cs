using System;
using System.Collections.Generic;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Tests.Support;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Reconciliation
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
}
