using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Snapshot node on a fake resource API: grab without renderer, ready frame, failure, release.</summary>
    public class SnapshotNodeTests
    {
        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;
        private SceneRegistry _scene = null!;
        private Reconciler _reconciler = null!;
        private SnapshotNode _node = null!;
        private Rectangle _element = null!;

        [SetUp]
        public void SetUp()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
            _client.SetPumping(true);
            _scene = new SceneRegistry();
            _reconciler = new Reconciler(new RecordingSink(), new IdAllocator(), _scene);
            _node = new SnapshotNode(_client, _scene);
            _element = new Rectangle { Size = new Vector2(10, 10) };
        }

        [TearDown]
        public void TearDown() => _node.Dispose();

        private void Pump()
        {
            _client.BeforeEvents();
            _client.AfterEvents();
        }

        private void Show()
        {
            _reconciler.Window = new IntPtr(1);
            _reconciler.Update(_element, Space.DIPTopLeft);
        }

        private IImage? Update(out bool isReady, out string error, bool grab = false, float scale = 1)
            => _node.Update(_element, out isReady, out error, scale, grab);

        private static ulong IdOf(FakeResourceApi api)
            => api.Calls.Where(c => c.StartsWith("snapshot")).Select(c => Convert.ToUInt64(c.Split(' ')[1], 16)).Last();

        [Test]
        public void Grab_NotShown_ErrorWithoutThrow_NoSnapshot()
        {
            var image = Update(out var ready, out var error, grab: true);
            Assert.That(image, Is.Null);
            Assert.That(ready, Is.False);
            Assert.That(error, Is.EqualTo(SnapshotNode.NotShownError));
            Assert.That(_api.Calls, Is.Empty, "no resource created");

            _ = _node.Update(null, out _, out error, 1, true);
            Assert.That(error, Is.EqualTo(SnapshotNode.NotShownError), "no element: same error");
        }

        [Test]
        public void Grab_NewInstanceBeforeRendererUpdate_FoundByItsSlot()
        {
            // Like a Group with an animated input: the node outputs a new instance every frame with the same feedback
            // slot, and Snapshot runs before the renderer of that frame.
            var slot = new FeedbackSlot(new ElementFeedback(), 0);
            _reconciler.Window = new IntPtr(1);
            _reconciler.Update(new Group { Feedback = slot }, Space.DIPTopLeft);
            for (var frame = 0; frame < 3; frame++)
            {
                var next = new Group { Feedback = slot, Opacity = 0.5f + frame * 0.1f };
                _node.Update(next, out _, out var error, 1, grab: true);
                Assert.That(error, Is.Empty, $"frame {frame}");
                _reconciler.Update(next, Space.DIPTopLeft);
            }
            Assert.That(_api.Calls.Where(c => c.StartsWith("snapshot")).All(c => c.EndsWith($" {_reconciler.Root!.Id} 1")), Is.True);
        }

        [Test]
        public void Grab_UnknownElementWithoutSlot_StaysNotShown()
        {
            Show();
            var other = new Rectangle { Size = new Vector2(10, 10) };
            _node.Update(other, out _, out var error, 1, grab: true);
            Assert.That(error, Is.EqualTo(SnapshotNode.NotShownError), "no slot: no place to fall back to");

            var foreign = new Rectangle { Feedback = new FeedbackSlot(new ElementFeedback(), 0) };
            _node.Update(foreign, out _, out error, 1, grab: true);
            Assert.That(error, Is.EqualTo(SnapshotNode.NotShownError), "a slot no renderer has shown");
        }

        [Test]
        public void Grab_ErrorOfAnEarlierAttempt_ClearedByTheNextOne()
        {
            Update(out _, out var error, grab: true);
            Assert.That(error, Is.EqualTo(SnapshotNode.NotShownError), "not shown yet at the start");
            Show();
            Update(out _, out error, grab: true);
            Assert.That(error, Is.Empty);
            Update(out _, out error);
            Assert.That(error, Is.Empty, "stays clear without Grab");
        }

        [Test]
        public void Grab_Ready_ImageHasSizeFormatAndOwnPixels()
        {
            Show();
            _api.SnapshotSize = (6, 3);
            Assert.That(Update(out var ready, out var error, grab: true, scale: 2), Is.Null, "nothing before the first one is done");
            Assert.That(ready, Is.False);
            Assert.That(error, Is.Empty);
            var call = _api.Calls.Single(c => c.StartsWith("snapshot"));
            Assert.That(call, Does.EndWith($" {_reconciler.Root!.Id} 2"));

            _api.Ready(IdOf(_api), 6, 3);
            Pump();
            var image = Update(out ready, out error);
            Assert.That(image, Is.Not.Null);
            Assert.That(ready, Is.True);
            Assert.That(error, Is.Empty);
            Assert.That(image!.Info.Width, Is.EqualTo(6));
            Assert.That(image.Info.Height, Is.EqualTo(3));
            Assert.That(image.Info.Format, Is.EqualTo(PixelFormat.B8G8R8A8));
            Assert.That(image.Info.IsPremultipliedAlpha, Is.True);
            Assert.That(image.IsVolatile, Is.False);
            using var data = image.GetData();
            Assert.That(data.ScanSize, Is.EqualTo(24));
            Assert.That(data.Bytes.Length, Is.EqualTo(72));
            Assert.That(data.Bytes.Span[0], Is.EqualTo(0x7F));
        }

        [Test]
        public void IsReady_OnlyOneFrame_ImageStaysUntilNextSnapshot()
        {
            Show();
            _api.SnapshotSize = (4, 4);
            Update(out _, out _, grab: true);
            _api.Ready(IdOf(_api), 4, 4);
            Pump();
            var first = Update(out var ready, out _)!;
            Assert.That(ready, Is.True);
            Assert.That(Update(out ready, out _), Is.SameAs(first));
            Assert.That(ready, Is.False);

            // Second grab: the old image stays until the replacement is done (INFO on a Ready resource).
            _api.SnapshotSize = (8, 2);
            Assert.That(Update(out ready, out _, grab: true), Is.SameAs(first));
            Assert.That(ready, Is.False);
            _api.Info(IdOf(_api), 8, 2);
            Pump();
            var second = Update(out ready, out _)!;
            Assert.That(ready, Is.True);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Info.Width, Is.EqualTo(8));
            Assert.That(first.Info.Width, Is.EqualTo(4), "own copy, the first image is untouched");
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1), "one resource per node");
        }

        [Test]
        public void Failed_ShowsResourceError_KeepsImage()
        {
            Show();
            _api.SnapshotSize = (4, 4);
            Update(out _, out _, grab: true);
            _api.Ready(IdOf(_api), 4, 4);
            Pump();
            var image = Update(out _, out _);

            Update(out _, out _, grab: true);
            _api.Failed(IdOf(_api), "grab failed");
            Pump();
            Assert.That(Update(out var ready, out var error), Is.SameAs(image));
            Assert.That(ready, Is.False);
            Assert.That(error, Is.EqualTo("grab failed"));
        }

        [Test]
        public void Refused_ShowsErrorAtOnce()
        {
            Show();
            _api.SnapshotRc = -7;
            var image = Update(out var ready, out var error, grab: true, scale: 0);
            Assert.That(image, Is.Null);
            Assert.That(ready, Is.False);
            Assert.That(error, Does.Contain("refused"));
        }

        [Test]
        public void Dispose_ReleasesResource()
        {
            Show();
            Update(out _, out _, grab: true);
            var id = IdOf(_api);
            _node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { id }));
        }
    }
}
