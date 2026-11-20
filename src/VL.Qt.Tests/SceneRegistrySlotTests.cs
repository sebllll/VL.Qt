using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Place fallback of <see cref="SceneRegistry.TryLocate"/>: a new instance is found by its feedback slot.</summary>
    public class SceneRegistrySlotTests
    {
        private SceneRegistry _scene = null!;
        private Reconciler _reconciler = null!;
        private FeedbackSlot _slot;

        [SetUp]
        public void SetUp()
        {
            _scene = new SceneRegistry();
            _reconciler = new Reconciler(new RecordingSink(), new IdAllocator(), _scene) { Window = new IntPtr(1) };
            _slot = new FeedbackSlot(new ElementFeedback(), 0);
        }

        private Group Child(float opacity) => new() { Feedback = _slot, Opacity = opacity };

        private Group Root(Element child) => new() { Children = [new Rectangle { Size = new Vector2(1, 1) }, child] };

        [Test]
        public void NewInstanceEveryFrame_LocatedBeforeTheReconcile_AtTheSamePlace()
        {
            _reconciler.Update(Root(Child(1)), Space.DIPTopLeft);
            var id = _reconciler.Root!.Children[1].Id;
            for (var frame = 0; frame < 50; frame++)
            {
                var next = Child(frame / 50f);
                Assert.That(_scene.TryLocate(next, out var window, out var located), Is.True, $"frame {frame}");
                Assert.That(window, Is.EqualTo(new IntPtr(1)));
                Assert.That(located, Is.EqualTo(id));
                _reconciler.Update(Root(next), Space.DIPTopLeft);
            }
            Assert.That(_scene.SlotCount, Is.EqualTo(1), "one entry per slot, not per instance");
        }

        [Test]
        public void ElementWithoutSlot_NotLocated()
        {
            _reconciler.Update(Root(Child(1)), Space.DIPTopLeft);
            Assert.That(_scene.TryLocate(new Rectangle(), out _, out _), Is.False);
            Assert.That(_scene.TryLocate(new Group { Feedback = new FeedbackSlot(new ElementFeedback(), 0) }, out _, out _), Is.False);
            Assert.That(_scene.TryLocate(new Group { Feedback = new FeedbackSlot(_slot.Feedback, 1) }, out _, out _), Is.False, "other index of the feedback");
        }

        [Test]
        public void Removed_SlotNotHeld()
        {
            _reconciler.Update(Root(Child(1)), Space.DIPTopLeft);
            Assert.That(_scene.SlotCount, Is.EqualTo(1));
            _reconciler.Update(new Group { Children = [new Rectangle()] }, Space.DIPTopLeft);
            Assert.That(_scene.SlotCount, Is.EqualTo(0));
            Assert.That(_scene.TryLocate(Child(0.5f), out _, out _), Is.False);
        }

        [Test]
        public void RendererDisposed_SlotNotHeld()
        {
            _reconciler.Update(Root(Child(1)), Space.DIPTopLeft);
            _reconciler.Clear();
            Assert.That(_scene.SlotCount, Is.EqualTo(0));
            Assert.That(_scene.Count, Is.EqualTo(0));
            Assert.That(_scene.TryLocate(Child(0.5f), out _, out _), Is.False);
        }

        [Test]
        public void Discarded_SlotNotHeld()
        {
            _reconciler.Update(Root(Child(1)), Space.DIPTopLeft);
            _reconciler.Discard();
            Assert.That(_scene.SlotCount, Is.EqualTo(0));
        }

        [Test]
        public void WindowGone_NotLocated()
        {
            _reconciler.Update(Root(Child(1)), Space.DIPTopLeft);
            _reconciler.Window = IntPtr.Zero;
            Assert.That(_scene.TryLocate(Child(0.5f), out _, out _), Is.False);
        }

        [Test]
        public void Fallback_NoAllocation()
        {
            _reconciler.Update(Root(Child(1)), Space.DIPTopLeft);
            var next = Child(0.5f);
            for (var i = 0; i < 100; i++)
                _scene.TryLocate(next, out _, out _);
            Assert.That(Allocations.Fewest(() => _scene.TryLocate(next, out _, out _)), Is.EqualTo(0));
        }
    }
}
