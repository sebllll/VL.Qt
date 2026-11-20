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
    /// <summary>Tooltip preview of an element on a fake resource API: throttle, change test, one grab at a time, text, release.</summary>
    public class ElementTooltipPreviewTests
    {
        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;
        private SceneRegistry _scene = null!;
        private Reconciler _reconciler = null!;
        private ElementTooltipPreview _node = null!;
        private Rectangle _element = null!;
        private long _changes;
        private double _clock;

        [SetUp]
        public void SetUp()
        {
            _api = new FakeResourceApi { SnapshotSize = (4, 4) };
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
            _client.SetPumping(true);
            _scene = new SceneRegistry();
            _reconciler = new Reconciler(new RecordingSink(), new IdAllocator(), _scene);
            _changes = 0;
            _clock = 100;
            _node = new ElementTooltipPreview(_client, _scene, _ => _changes, () => _clock);
            _element = new Rectangle { Size = new Vector2(10, 10) };
        }

        [TearDown]
        public void TearDown() => _node.Dispose();

        private void Pump()
        {
            _client.BeforeEvents();
            _client.AfterEvents();
        }

        private void Show(Element? element = null)
        {
            _reconciler.Window = new IntPtr(1);
            _reconciler.Update(element ?? _element, Space.DIPTopLeft);
        }

        private IImage? Update(out string text, Element? element = null) => _node.Update(element ?? _element, out text);

        private int Grabs => _api.Calls.Count(c => c.StartsWith("snapshot_fit"));

        private ulong Id => _api.Calls.Where(c => c.StartsWith("snapshot_fit")).Select(c => Convert.ToUInt64(c.Split(' ')[1], 16)).Last();

        private void Finish()
        {
            _api.Ready(Id, 4, 4);
            Pump();
        }

        [Test]
        public void NullElement_NoImage_EmptyText_NoGrab()
        {
            var image = _node.Update(null, out var text);
            Assert.That(image, Is.Null);
            Assert.That(text, Is.Empty);
            Assert.That(_api.Calls, Is.Empty);
        }

        [Test]
        public void NotShown_TextSaysSo_NoGrab()
        {
            var image = Update(out var text);
            Assert.That(image, Is.Null);
            Assert.That(text, Is.EqualTo("Rectangle 10 x 10 – not shown in a renderer"));
            Assert.That(_api.Calls, Is.Empty, "no resource created");
        }

        [Test]
        public void NewInstanceEveryFrame_BeforeRendererUpdate_FoundByItsSlot()
        {
            // an animated Group is a new instance every frame (same sink slot); the tooltip runs before the renderer.
            var slot = new SinkSlot(new ElementSink(), 0);
            Show(new Group { Sink = slot, Size = new Vector2(10, 10) });
            for (var frame = 0; frame < 3; frame++)
            {
                var next = new Group { Sink = slot, Size = new Vector2(10, 10), Opacity = 0.5f + frame * 0.1f };
                Update(out var text, next);
                Assert.That(text, Is.EqualTo("Item 10 x 10"), $"frame {frame}");
                if (frame == 0)
                    Finish();
                Show(next);
            }
            Assert.That(Update(out _, _reconciler.Root!.Last), Is.Not.Null, "the place keeps its image");
            Assert.That(Grabs, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void NoLongerShown_DropsTheImage()
        {
            Show();
            Update(out _);
            Finish();
            Assert.That(Update(out _), Is.Not.Null);

            _reconciler.Window = IntPtr.Zero;
            var image = Update(out var text);
            Assert.That(image, Is.Null);
            Assert.That(text, Does.EndWith("not shown in a renderer"));
        }

        [Test]
        public void FirstUpdate_GrabsWithMaxEdge_ImageAfterSettle()
        {
            Show();
            Assert.That(Update(out var text), Is.Null, "nothing before the first one is done");
            Assert.That(text, Is.EqualTo("Rectangle 10 x 10"));
            Assert.That(_api.Calls.Single(c => c.StartsWith("snapshot_fit")), Does.EndWith($" {_reconciler.Root!.Id} 256"));

            Finish();
            var image = Update(out text);
            Assert.That(image, Is.Not.Null);
            Assert.That(image!.Info.Width, Is.EqualTo(4));
            Assert.That(image.Info.Format, Is.EqualTo(PixelFormat.B8G8R8A8));
            Assert.That(text, Is.EqualTo("Rectangle 10 x 10"));
        }

        [Test]
        public void SameElementSameCounter_NoSecondGrab()
        {
            Show();
            Update(out _);
            Finish();
            var first = Update(out _);

            for (var i = 0; i < 5; i++)
            {
                _clock += 1;
                Assert.That(Update(out _), Is.SameAs(first));
            }
            Assert.That(Grabs, Is.EqualTo(1));
        }

        [Test]
        public void ChangedCounter_GrabsAgain_ButNotBefore200ms()
        {
            Show();
            Update(out _);
            Finish();
            var first = Update(out _);

            _changes++;
            _clock += 0.125;
            Assert.That(Update(out _), Is.SameAs(first), "the old image stays");
            Assert.That(Grabs, Is.EqualTo(1), "too early");

            _clock += 0.125;
            Update(out _);
            Assert.That(Grabs, Is.EqualTo(2));

            _api.SnapshotSize = (8, 2);
            _api.Info(Id, 8, 2);
            Pump();
            var second = Update(out _);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second!.Info.Width, Is.EqualTo(8));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1), "one resource per node");
        }

        [Test]
        public void NewInstanceAtSamePlace_CountsAsChange_KeepsImage_NotBefore200ms()
        {
            var other = new Rectangle { Size = new Vector2(20, 5) };
            Show();
            Update(out _);
            Finish();
            var first = Update(out _);
            Assert.That(first, Is.Not.Null);

            _reconciler.Update(other, Space.DIPTopLeft);
            _clock += 0.05;
            var image = Update(out var text, other);
            Assert.That(Grabs, Is.EqualTo(1), "too early");
            Assert.That(image, Is.SameAs(first), "same place: the last image stays for the new instance");
            Assert.That(text, Is.EqualTo("Rectangle 20 x 5"), "the text comes from the current instance");

            _clock += 0.2;
            Assert.That(Update(out _, other), Is.SameAs(first));
            Assert.That(Grabs, Is.EqualTo(2), "the new instance is a change");

            Finish();
            var second = Update(out _, other);
            Assert.That(second, Is.Not.Null.And.Not.SameAs(first));
            _clock += 1;
            Assert.That(Update(out _, other), Is.SameAs(second));
            Assert.That(Grabs, Is.EqualTo(2), "the same instance, no change: no further grab");
        }

        [Test]
        public void NewInstanceEveryFrame_KeepsTheImage_AtMost5GrabsPerSecond_NeverTwoAtOnce()
        {
            const int frames = 180; // 3 s at 60 Hz of simulated time
            const int settleFrames = 4;
            var finished = 0;
            var startedAt = -1;
            var grabsSeen = 0;
            var haveImage = false;
            for (var frame = 0; frame < frames; frame++)
            {
                _clock += 1.0 / 60;
                var animated = new Rectangle { Size = new Vector2(10 + frame % 7, 10), Fill = new Color4(frame % 10 / 10f, 0, 0, 1) };
                Show(animated);
                var image = Update(out _, animated);

                if (haveImage)
                    Assert.That(image, Is.Not.Null, $"frame {frame}: the image never flickers to null");
                haveImage |= image is not null;

                if (Grabs > grabsSeen)
                {
                    grabsSeen = Grabs;
                    startedAt = frame;
                }
                Assert.That(Grabs - finished, Is.LessThanOrEqualTo(1), $"frame {frame}: never two grabs at once");
                if (startedAt >= 0 && frame - startedAt >= settleFrames && Grabs > finished)
                {
                    Finish();
                    finished++;
                }
            }
            Assert.That(haveImage, Is.True);
            Assert.That(Grabs, Is.InRange(8, 16), "about 5 per second over 3 s");
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1), "one resource per node");
        }

        [Test]
        public void PlaceChange_DropsTheImage_AndAResultOfTheOldPlace()
        {
            var a = new Rectangle { Size = new Vector2(10, 10) };
            var b = new Rectangle { Size = new Vector2(10, 10) };
            var tree = new Group { Children = [a, b] };
            Show(tree);

            Update(out _, a);
            Finish();
            Assert.That(Update(out _, a), Is.Not.Null);

            _clock += 1;
            Assert.That(Update(out _, b), Is.Null, "another place: the image of a is not shown for b");
            Assert.That(Grabs, Is.EqualTo(2));

            // The grab of b is running; going back to a drops the image of b and the grab result belongs to b.
            Assert.That(Update(out _, a), Is.Null);
            Finish();
            _clock += 1;
            Assert.That(Update(out _, a), Is.Null, "the result of b is dropped for a");
            Assert.That(Grabs, Is.EqualTo(3), "a gets its own grab");
            Finish();
            Assert.That(Update(out _, a), Is.Not.Null);
        }

        [Test]
        public void ShortlyNotLocatable_DropsTheImage_ThenGrabsAgain()
        {
            Show();
            Update(out _);
            Finish();
            Assert.That(Update(out _), Is.Not.Null);

            var next = new Rectangle { Size = new Vector2(10, 10), Fill = new Color4(1, 0, 0, 1) };
            _clock += 1;
            var image = Update(out var text, next); // not reconciled yet
            Assert.That(image, Is.Null);
            Assert.That(text, Does.EndWith(ElementTooltipPreview.NotShownSuffix));
            Assert.That(Grabs, Is.EqualTo(1));

            Show(next);
            Update(out _, next);
            Assert.That(Grabs, Is.EqualTo(2));
            Finish();
            Assert.That(Update(out _, next), Is.Not.Null);
        }

        [Test]
        public void NeverTwoGrabsAtOnce()
        {
            Show();
            Update(out _);
            for (var i = 0; i < 5; i++)
            {
                _changes++;
                _clock += 1;
                Update(out _);
            }
            Assert.That(Grabs, Is.EqualTo(1), "the first one is still running");

            Finish();
            Update(out _);
            Assert.That(Grabs, Is.EqualTo(2), "now the pending change is grabbed");
        }

        [Test]
        public void ChangeDuringGrab_CountsForTheNextOne()
        {
            Show();
            Update(out _);
            _changes++;
            Finish();
            _clock += 1;
            Update(out _);
            Assert.That(Grabs, Is.EqualTo(2));
        }

        [Test]
        public void GrabFailed_ShownInText_NoRetryWithoutChange()
        {
            Show();
            Update(out _);
            _api.Failed(Id, "grab failed");
            Pump();
            Assert.That(Update(out var text), Is.Null);
            Assert.That(text, Is.EqualTo("Rectangle 10 x 10 – grab failed"));

            _clock += 1;
            Update(out _);
            Assert.That(Grabs, Is.EqualTo(1));
        }

        [Test]
        public void Refused_ShownInText_NoException()
        {
            Show();
            _api.SnapshotRc = -7;
            var image = Update(out var text);
            Assert.That(image, Is.Null);
            Assert.That(text, Does.Contain("refused"));

            _api.SnapshotRc = 0;
            _changes++;
            _clock += 1;
            Update(out _);
            Assert.That(Grabs, Is.EqualTo(2), "a change retries; the refusal did not block the node");
        }

        private static Group VideoTree(bool playing = false)
            => new Group { Children = [new Rectangle { Size = new Vector2(10, 10) }, new Group { Children = [new Video { Size = new Vector2(4, 3), Playing = playing }] }] };

        [Test]
        public void TreeWithVideo_NoGrab_TextOnly([Values(false, true)] bool playing)
        {
            var tree = VideoTree(playing);
            Show(tree);
            for (var i = 0; i < 5; i++)
            {
                _clock += 1;
                _changes++;
                var image = Update(out var text, tree);
                Assert.That(image, Is.Null);
                Assert.That(text, Does.EndWith(ElementTooltipPreview.ContainsVideoSuffix));
                Assert.That(text, Does.Contain("contains video, no preview"));
            }
            Assert.That(_api.Calls, Is.Empty, "no resource, no grab");
        }

        [Test]
        public void VideoAsDirectElement_NoGrab()
        {
            var video = new Video { Size = new Vector2(4, 3) };
            Show(video);
            Assert.That(Update(out var text, video), Is.Null);
            Assert.That(text, Is.EqualTo("Video 4 x 3 – contains video, no preview"));
            Assert.That(Grabs, Is.EqualTo(0));
        }

        [Test]
        public void SwitchFromVideoTree_ToPlainElement_GrabsAgain()
        {
            var tree = VideoTree();
            Show(tree);
            Update(out _, tree);
            Assert.That(Grabs, Is.EqualTo(0));

            Show();
            Update(out var text);
            Assert.That(Grabs, Is.EqualTo(1));
            Assert.That(text, Is.EqualTo("Rectangle 10 x 10"));
            Finish();
            Assert.That(Update(out _), Is.Not.Null);
        }

        [Test]
        public void SwitchFromPlainElement_ToVideoTree_DropsImage_NoNewGrab()
        {
            Show();
            Update(out _);
            Finish();
            Assert.That(Update(out _), Is.Not.Null);

            var tree = VideoTree();
            Show(tree);
            _clock += 1;
            _changes++;
            var image = Update(out var text, tree);
            Assert.That(image, Is.Null);
            Assert.That(text, Does.EndWith(ElementTooltipPreview.ContainsVideoSuffix));
            Assert.That(Grabs, Is.EqualTo(1), "only the first grab");
        }

        [Test]
        public void NotShownVideoTree_SaysNotShown()
        {
            var tree = VideoTree();
            Assert.That(Update(out var text, tree), Is.Null);
            Assert.That(text, Does.EndWith(ElementTooltipPreview.NotShownSuffix));
        }

        [Test]
        public void Dispose_ReleasesResource()
        {
            Show();
            Update(out _);
            var id = Id;
            _node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { id }));
        }
    }
}
