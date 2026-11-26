using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;
using VL.Qt.Interop;
using VL.Qt.Reconciliation;
using VL.Qt.Tests.Support;

namespace VL.Qt.Tests.Reconciliation
{
    /// <summary>
    /// Key of <see cref="OccurrenceRegistry"/> (<see cref="OccurrenceKey"/>): an element with a feedback slot is its slot,
    /// whatever instance its node outputs; an element without a slot is its instance.
    /// </summary>
    public class OccurrenceRegistrySlotTests
    {
        private OccurrenceRegistry _scene = null!;
        private InteractionRegistry _interactions = null!;
        private Reconciler _reconciler = null!;
        private FeedbackSlot _slot;

        [SetUp]
        public void SetUp()
        {
            _scene = new OccurrenceRegistry();
            _interactions = new InteractionRegistry();
            _reconciler = new Reconciler(new RecordingSink(), new IdAllocator(), _scene, _interactions) { Window = new SceneHandle(1) };
            _slot = new FeedbackSlot(new ElementFeedback(), 0);
        }

        private Group Child(float opacity) => new() { Feedback = _slot, Opacity = opacity };

        private Group Root(Element child) => new() { Children = [new Rectangle { Size = new Vector2(1, 1) }, child] };

        [Test]
        public void NewInstanceEveryFrame_LocatedBeforeTheReconcile_AtTheSamePlace()
        {
            _reconciler.Update(Root(Child(1)), SpaceMapping.Default);
            var id = _reconciler.Root!.Children[1].Id;
            for (var frame = 0; frame < 50; frame++)
            {
                var next = Child(frame / 50f);
                Assert.That(_scene.TryLocate(next, out var window, out var located), Is.True, $"frame {frame}");
                Assert.That(window, Is.EqualTo(new SceneHandle(1)));
                Assert.That(located, Is.EqualTo(id));
                _reconciler.Update(Root(next), SpaceMapping.Default);
            }
        }

        [Test]
        public void NewInstanceEveryFrame_KeepsItsOccurrence()
        {
            _reconciler.Update(Root(Child(1)), SpaceMapping.Default);
            var item = _reconciler.Root!.Children[1];
            var count = _scene.Count;
            for (var frame = 0; frame < 10; frame++)
            {
                var next = Child(frame / 10f);
                _reconciler.Update(Root(next), SpaceMapping.Default);
                Assert.That(_scene.PrimaryOf(next), Is.SameAs(item), $"frame {frame}");
                Assert.That(_scene.OccurrencesOf(next), Is.EqualTo(1));
                Assert.That(item.IsPrimary, Is.True);
                Assert.That(_scene.Count, Is.EqualTo(count), "one entry per slot, not per instance");
            }
        }

        [Test]
        public void TwoInstancesOfOneNode_InOneTree_AreOccurrencesOfOneElement()
        {
            // Last frame's instance (e.g. through a FrameDelay) next to the current one: one element, two occurrences.
            var earlier = Child(0.2f);
            var current = Child(0.8f);
            _reconciler.Update(new Group { Children = [earlier, current] }, SpaceMapping.Default);
            var children = _reconciler.Root!.Children;
            Assert.That(_scene.OccurrencesOf(current), Is.EqualTo(2));
            Assert.That(_scene.PrimaryOf(current), Is.SameAs(children[0]), "the first in traversal order");
            Assert.That(children[1].IsPrimary, Is.False);
        }

        [Test]
        public void ElementWithoutSlot_KeyedByInstance()
        {
            var shown = new Rectangle { Size = new Vector2(1, 1) };
            _reconciler.Update(new Group { Children = [shown, Child(1)] }, SpaceMapping.Default);
            Assert.That(_scene.TryLocate(shown, out _, out _), Is.True);
            Assert.That(_scene.TryLocate(new Rectangle { Size = new Vector2(1, 1) }, out _, out _), Is.False, "another instance without a slot");
            Assert.That(_scene.TryLocate(new Group { Feedback = new FeedbackSlot(new ElementFeedback(), 0) }, out _, out _), Is.False, "another feedback");
            Assert.That(_scene.TryLocate(new Group { Feedback = new FeedbackSlot(_slot.Feedback, 1) }, out _, out _), Is.False, "other index of the feedback");
        }

        [Test]
        public void Removed_NotHeld()
        {
            _reconciler.Update(Root(Child(1)), SpaceMapping.Default);
            _reconciler.Update(new Group { Children = [new Rectangle()] }, SpaceMapping.Default);
            Assert.That(_scene.OccurrencesOf(Child(0.5f)), Is.Zero);
            Assert.That(_scene.TryLocate(Child(0.5f), out _, out _), Is.False);
        }

        [Test]
        public void RendererDisposed_NothingHeld()
        {
            _reconciler.Update(Root(Child(1)), SpaceMapping.Default);
            _reconciler.Clear();
            Assert.That(_scene.Count, Is.EqualTo(0));
            Assert.That(_scene.TryLocate(Child(0.5f), out _, out _), Is.False);
        }

        [Test]
        public void Discarded_NothingHeld()
        {
            _reconciler.Update(Root(Child(1)), SpaceMapping.Default);
            _reconciler.Discard();
            Assert.That(_scene.Count, Is.EqualTo(0));
        }

        [Test]
        public void WindowGone_NotLocated()
        {
            _reconciler.Update(Root(Child(1)), SpaceMapping.Default);
            _reconciler.Window = SceneHandle.Null;
            Assert.That(_scene.TryLocate(Child(0.5f), out _, out _), Is.False);
        }

        [Test]
        public void LocateNewInstance_NoAllocation()
        {
            _reconciler.Update(Root(Child(1)), SpaceMapping.Default);
            var next = Child(0.5f);
            for (var i = 0; i < 100; i++)
                _scene.TryLocate(next, out _, out _);
            Assert.That(Allocations.Fewest(() => _scene.TryLocate(next, out _, out _)), Is.EqualTo(0));
        }
    }
}
