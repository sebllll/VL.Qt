using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Snapshot node on a fake resource API: grab without renderer, ready frame, failure, release.</summary>
    public class SnapshotNodeTests
    {
        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;
        private OccurrenceRegistry _scene = null!;
        private InteractionRegistry _interactions = null!;
        private Reconciler _reconciler = null!;
        private SnapshotNode _node = null!;
        private RecordingMessages _messages = null!;
        private Rectangle _element = null!;

        [SetUp]
        public void SetUp()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
            _client.SetPumping(true);
            _scene = new OccurrenceRegistry();
            _interactions = new InteractionRegistry();
            _reconciler = new Reconciler(new RecordingSink(), new IdAllocator(), _scene, _interactions);
            _messages = new RecordingMessages();
            _node = new SnapshotNode(_client, _scene, _messages.Errors);
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
            _reconciler.Window = new SceneHandle(1);
            _reconciler.Update(_element, Space.DIPTopLeft);
        }

        private IImage? Update(out bool isReady, bool grab = false, float scale = 1)
            => _node.Update(_element, scale, grab, out isReady);

        private int Snapshots => _api.Calls.Count(c => c.StartsWith("snapshot"));

        private static ulong IdOf(FakeResourceApi api)
            => api.Calls.Where(c => c.StartsWith("snapshot")).Select(c => Convert.ToUInt64(c.Split(' ')[1], 16)).Last();

        [Test]
        public void Grab_NotShown_LoggedWithoutThrow_NoSnapshot()
        {
            var image = Update(out var ready, grab: true);
            Assert.That(image, Is.Null);
            Assert.That(ready, Is.False);
            Assert.That(_messages.Logged, Is.EqualTo(new[] { SnapshotNode.NotShownError }));
            Assert.That(_messages.Shown, Is.Null, "a one-shot failure does not mark the node");
            Assert.That(_api.Calls, Is.Empty, "no resource created");

            _ = _node.Update(null, 1, true, out _);
            Assert.That(_messages.Logged, Is.EqualTo(new[] { SnapshotNode.NotShownError }), "no element: the same error, logged once");
            Assert.That(_api.Calls, Is.Empty);
        }

        [Test]
        public void Grab_NewInstanceBeforeRendererUpdate_FoundByItsSlot()
        {
            // Like a Group with an animated input: the node outputs a new instance every frame with the same feedback
            // slot, and Snapshot runs before the renderer of that frame.
            var slot = new FeedbackSlot(new ElementFeedback(), 0);
            _reconciler.Window = new SceneHandle(1);
            _reconciler.Update(new Group { Feedback = slot }, Space.DIPTopLeft);
            for (var frame = 0; frame < 3; frame++)
            {
                var next = new Group { Feedback = slot, Opacity = 0.5f + frame * 0.1f };
                _node.Update(next, 1, true, out _);
                Assert.That(_messages.Logged, Is.Empty, $"frame {frame}");
                _reconciler.Update(next, Space.DIPTopLeft);
            }
            Assert.That(_api.Calls.Where(c => c.StartsWith("snapshot")).All(c => c.EndsWith($" {_reconciler.Root!.Id} 1")), Is.True);
        }

        [Test]
        public void Grab_UnknownElementWithoutSlot_StaysNotShown()
        {
            Show();
            var other = new Rectangle { Size = new Vector2(10, 10) };
            _node.Update(other, 1, true, out _);
            Assert.That(_messages.Logged, Is.EqualTo(new[] { SnapshotNode.NotShownError }), "no slot: no place to fall back to");

            var foreign = new Rectangle { Feedback = new FeedbackSlot(new ElementFeedback(), 0) };
            _node.Update(foreign, 1, true, out _);
            Assert.That(Snapshots, Is.Zero, "a slot no renderer has shown");
            Assert.That(_messages.Logged, Is.EqualTo(new[] { SnapshotNode.NotShownError }), "the same error, logged once");
        }

        [Test]
        public void Grab_ErrorOfAnEarlierAttempt_ClearedByTheNextOne()
        {
            Update(out _, grab: true);
            Assert.That(_messages.Logged, Is.EqualTo(new[] { SnapshotNode.NotShownError }), "not shown yet at the start");
            Show();
            Update(out _, grab: true);
            Assert.That(Snapshots, Is.EqualTo(1));
            Update(out _);
            Assert.That(_messages.Logged, Has.Count.EqualTo(1), "nothing more logged");

            // Cleared: the same error later is logged again.
            _node.Update(null, 1, true, out _);
            Assert.That(_messages.Logged, Is.EqualTo(new[] { SnapshotNode.NotShownError, SnapshotNode.NotShownError }));
        }

        [Test]
        public void Grab_Ready_ImageHasSizeFormatAndOwnPixels()
        {
            Show();
            _api.SnapshotSize = (6, 3);
            Assert.That(Update(out var ready, grab: true, scale: 2), Is.Null, "nothing before the first one is done");
            Assert.That(ready, Is.False);
            var call = _api.Calls.Single(c => c.StartsWith("snapshot"));
            Assert.That(call, Does.EndWith($" {_reconciler.Root!.Id} 2"));

            _api.Ready(IdOf(_api), 6, 3);
            Pump();
            var image = Update(out ready);
            Assert.That(image, Is.Not.Null);
            Assert.That(ready, Is.True);
            Assert.That(_messages.Logged, Is.Empty);
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
            Update(out _, grab: true);
            _api.Ready(IdOf(_api), 4, 4);
            Pump();
            var first = Update(out var ready)!;
            Assert.That(ready, Is.True);
            Assert.That(Update(out ready), Is.SameAs(first));
            Assert.That(ready, Is.False);

            // Second grab: the old image stays until the replacement is done (INFO on a Ready resource).
            _api.SnapshotSize = (8, 2);
            Assert.That(Update(out ready, grab: true), Is.SameAs(first));
            Assert.That(ready, Is.False);
            _api.Info(IdOf(_api), 8, 2);
            Pump();
            var second = Update(out ready)!;
            Assert.That(ready, Is.True);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Info.Width, Is.EqualTo(8));
            Assert.That(first.Info.Width, Is.EqualTo(4), "own copy, the first image is untouched");
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1), "one resource per node");
        }

        [Test]
        public void Failed_LogsResourceError_KeepsImage()
        {
            Show();
            _api.SnapshotSize = (4, 4);
            Update(out _, grab: true);
            _api.Ready(IdOf(_api), 4, 4);
            Pump();
            var image = Update(out _);

            Update(out _, grab: true);
            _api.Failed(IdOf(_api), "grab failed");
            Pump();
            Assert.That(Update(out var ready), Is.SameAs(image));
            Assert.That(ready, Is.False);
            Assert.That(_messages.Logged, Is.EqualTo(new[] { "grab failed" }));
            Assert.That(_messages.Shown, Is.Null);
        }

        [Test]
        public void Refused_LogsErrorAtOnce()
        {
            Show();
            _api.SnapshotRc = Native.Result.BadArgument;
            var image = Update(out var ready, grab: true, scale: 0);
            Assert.That(image, Is.Null);
            Assert.That(ready, Is.False);
            Assert.That(_messages.Logged.Single(), Does.Contain("refused"));
        }

        [Test]
        public void Dispose_ReleasesResource()
        {
            Show();
            Update(out _, grab: true);
            var id = IdOf(_api);
            _node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { id }));
        }
    }
}
