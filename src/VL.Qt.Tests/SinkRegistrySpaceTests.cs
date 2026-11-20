using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Tests
{
    /// <summary>Events from QtNative come back in units of the scene's Space: Scene Bounds, size, pointer, drag, pinch, touch.</summary>
    public class SinkRegistrySpaceTests
    {
        // Normalized in a 600 DIP high scene: 300 DIP per unit.
        private static readonly SpaceMapping Normalized600 = new(Space.Normalized, new Vector2(800, 600), 1f);

        [Test]
        public void Events_AreConvertedWithTheMappingOfTheScene()
        {
            var reg = new SinkRegistry { Mapping = Normalized600 };
            var sink = new ElementSink();
            uint h = reg.Acquire(new SinkSlot(sink, 0));

            reg.Dispatch(new SinkRegistryTests.Poll()
                .Measured(h, 600, 300)
                .Bounds(h, 0, 150, 300, 150)
                .Pointer(h, 150, 75)
                .Drag(h, 30, -60)
                .Pinch(h, 2f, 90f, 300, 0)
                .TouchPoints(h, (7, 150, 300, 0.5f, 30, 60))
                .Wheel(h, 1, -2).Bytes);

            Assert.That(sink.MeasuredSize(), Is.EqualTo(new Vector2(2f, 1f)));
            Assert.That(sink.SceneBounds(), Is.EqualTo(new RectangleF(0f, 0.5f, 1f, 0.5f)));
            Assert.That(sink.PointerPosition(), Is.EqualTo(new Vector2(0.5f, 0.25f)));
            Assert.That(sink[0].DragDelta, Is.EqualTo(new Vector2(0.1f, -0.2f)));
            Assert.That(sink[0].PinchTranslation, Is.EqualTo(new Vector2(1f, 0f)));
            Assert.That(sink[0].PinchScale, Is.EqualTo(2f), "factor, not a length");
            Assert.That(sink[0].PinchRotation, Is.EqualTo(0.25f), "cycles");
            Assert.That(sink[0].PendingWheel, Is.EqualTo(new Vector2(1, -2)), "notches, not a length");
            var p = sink[0].TouchPoints[0];
            Assert.That(p.Position, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(p.Size, Is.EqualTo(new Vector2(0.1f, 0.2f)));
        }

        [Test]
        public void Mapping_FollowsTheReconcilerUpdate()
        {
            var reg = new Reconciler(new RecordingSink(), new IdAllocator(), new SceneRegistry());
            reg.Update(null, Normalized600);
            Assert.That(reg.Sinks.Mapping, Is.EqualTo(Normalized600));
            var resized = new SpaceMapping(Space.Normalized, new Vector2(400, 300), 1f);
            reg.Update(null, resized);
            Assert.That(reg.Sinks.Mapping, Is.EqualTo(resized));
        }
    }
}
