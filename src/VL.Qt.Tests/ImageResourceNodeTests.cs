using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using Image = VL.Qt.Elements.Image;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests
{
    /// <summary>LoadImage, LoadImage (Async), ToQtImage and the Image pin against the fake QtNative.</summary>
    public class ImageResourceNodeTests
    {
        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;

        [SetUp]
        public void SetUp()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
            _client.SetPumping(true);
        }

        private void Pump()
        {
            _client.BeforeEvents();
            _client.AfterEvents();
        }

        private static Path P(string path) => new(path);

        [Test]
        public void LoadImage_Synchronous_ReadyAfterUpdate()
        {
            _api.OnWait = id => _api.Ready(id, 100, 50);
            var messages = new RecordingMessages();
            using var node = new LoadImageNode(_client, messages.Errors);
            var image = node.Update(P("C:/a.png"), false, ImageUpload.Auto);
            Assert.That(image, Is.Not.Null);
            Assert.That((image!.State, image.Width, image.Height, messages.Shown), Is.EqualTo((QtImageState.Ready, 100, 50, (string?)null)));
            Assert.That(node.Update(P("C:/a.png"), false, ImageUpload.Auto), Is.SameAs(image), "unchanged path = same image, no reload");
            Assert.That(messages.Logged, Is.Empty);
            Assert.That(_api.Calls.Count(c => c.StartsWith("load")), Is.EqualTo(1));
        }

        [Test]
        public void LoadImage_PathChange_NewImage_OldReleasedInNextPump()
        {
            _api.OnWait = id => _api.Ready(id);
            using var node = new LoadImageNode(_client);
            var a = node.Update(P("C:/a.png"), false, ImageUpload.Auto)!;
            var b = node.Update(P("C:/b.png"), false, ImageUpload.Auto)!;
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(b.Id, Is.Not.EqualTo(a.Id));
            Assert.That(_api.Released, Is.Empty);
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id }));
            Assert.That(node.Update(P(""), false, ImageUpload.Auto), Is.Null, "empty path = no image");
            node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id, b.Id }));
        }

        [Test]
        public void LoadImage_Failed_MarksTheNodeAndLogsOnce()
        {
            _api.OnWait = id => _api.Failed(id, "cannot decode");
            var messages = new RecordingMessages();
            using var node = new LoadImageNode(_client, messages.Errors);
            var image = node.Update(P("C:/broken.png"), false, ImageUpload.Auto);
            Assert.That(image!.State, Is.EqualTo(QtImageState.Failed));
            Assert.That(messages.Shown, Is.EqualTo("cannot decode"));
            node.Update(P("C:/broken.png"), false, ImageUpload.Auto);
            Assert.That(messages.Logged, Is.EqualTo(new[] { "cannot decode" }), "logged once while it lasts");

            _api.OnWait = id => _api.Ready(id);
            node.Update(P("C:/a.png"), false, ImageUpload.Auto);
            Assert.That(messages.Shown, Is.Null, "gone with the next path that loads");
        }

        [Test]
        public void LoadImageAsync_KeepPrevious_SwitchesWhenReady()
        {
            var messages = new RecordingMessages();
            using var node = new LoadImageAsyncNode(_client, messages.Errors);
            var loaded = new List<QtImage>();
            QtImage? Update(string path, out bool isLoaded, bool keep = true)
            {
                var image = node.Update(P(path), false, keep, ImageUpload.Auto, out isLoaded, out var onLoaded);
                if (loaded.Count == 0 && onLoaded is not null)
                    onLoaded.Subscribe(loaded.Add);
                return image;
            }

            var first = Update("C:/a.png", out var isLoaded);
            Assert.That(first, Is.Not.Null, "nothing before: the loading image is the output");
            Assert.That(isLoaded, Is.False);
            var idA = _api.Calls.Count;
            _api.Ready(first!.Id);
            Pump();
            Assert.That(loaded, Is.EqualTo(new[] { first }));
            Assert.That(Update("C:/a.png", out isLoaded), Is.SameAs(first));
            Assert.That(isLoaded, Is.True);

            var stillA = Update("C:/b.png", out isLoaded);
            Assert.That(stillA, Is.SameAs(first), "previous stays while B loads");
            Assert.That(isLoaded, Is.False);
            var pendingId = first.Id + 1;
            _api.Ready(pendingId);
            Pump();
            var b = Update("C:/b.png", out isLoaded);
            Assert.That(b, Is.Not.SameAs(first));
            Assert.That(b!.Id, Is.EqualTo(pendingId));
            Assert.That(isLoaded, Is.True);
            Assert.That(loaded, Has.Count.EqualTo(2));
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { first.Id }), "A released after the switch");

            // C fails: B stays, the node shows why.
            Update("C:/c.png", out _);
            _api.Failed(pendingId + 1, "not found");
            Pump();
            var stillB = Update("C:/c.png", out isLoaded);
            Assert.That(stillB, Is.SameAs(b));
            Assert.That((isLoaded, messages.Shown), Is.EqualTo((false, "not found")));
            Assert.That(messages.Logged, Is.EqualTo(new[] { "not found" }));
        }

        [Test]
        public void LoadImageAsync_WithoutKeepPrevious_SwitchesAtOnce()
        {
            using var node = new LoadImageAsyncNode(_client);
            var a = node.Update(P("C:/a.png"), false, false, ImageUpload.Auto, out _, out _);
            var b = node.Update(P("C:/b.png"), false, false, ImageUpload.Auto, out var isLoaded, out _);
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(b!.State, Is.EqualTo(QtImageState.Loading));
            Assert.That(isLoaded, Is.False);
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a!.Id }));
        }

        [Test]
        public void LoadImageAsync_CreateFails_WithoutKeepPrevious_TheOldImageIsGone()
        {
            var messages = new RecordingMessages();
            using var node = new LoadImageAsyncNode(_client, messages.Errors);
            var a = node.Update(P("C:/a.png"), false, false, ImageUpload.Auto, out _, out _)!;
            _api.Ready(a.Id);
            Pump();

            _api.CreateRc = Native.Result.Internal; // QtNative refuses the new image
            var b = node.Update(P("C:/b.png"), false, false, ImageUpload.Auto, out var isLoaded, out _);
            Assert.That(b, Is.Null, "Keep Previous off: the output switches at once, to nothing when the new one cannot be created");
            Assert.That(isLoaded, Is.False);
            Assert.That(messages.Shown, Is.Not.Null.And.Not.Empty, "the node says why");
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id }), "the old image is released");
        }

        [Test]
        public void LoadImageAsync_CreateFails_WithKeepPrevious_TheOldImageStays()
        {
            var messages = new RecordingMessages();
            using var node = new LoadImageAsyncNode(_client, messages.Errors);
            var a = node.Update(P("C:/a.png"), false, true, ImageUpload.Auto, out _, out _)!;
            _api.Ready(a.Id);
            Pump();

            _api.CreateRc = Native.Result.Internal;
            var b = node.Update(P("C:/b.png"), false, true, ImageUpload.Auto, out _, out _);
            Assert.That(b, Is.SameAs(a));
            Assert.That(messages.Shown, Is.Not.Null.And.Not.Empty);
            Pump();
            Assert.That(_api.Released, Is.Empty);
        }

        [Test]
        public void LoadImageAsync_PathChangeWhileLoading_CancelsThePending()
        {
            using var node = new LoadImageAsyncNode(_client);
            var a = node.Update(P("C:/a.png"), false, true, ImageUpload.Auto, out _, out _)!;
            _api.Ready(a.Id);
            Pump();
            node.Update(P("C:/b.png"), false, true, ImageUpload.Auto, out _, out _);
            node.Update(P("C:/c.png"), false, true, ImageUpload.Auto, out _, out _);
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id + 1 }), "B released, A stays at the output");
        }

        private sealed class TestImage : IImage, IImageData
        {
            private readonly byte[] _bytes;
            public TestImage(int width, int height, PixelFormat format, bool volatileImage = false)
            {
                Info = new ImageInfo(width, height, format);
                _bytes = new byte[Info.ScanSize * height];
                IsVolatile = volatileImage;
            }
            public ImageInfo Info { get; }
            public bool IsVolatile { get; }
            public IImageData GetData() => this;
            public ReadOnlyMemory<byte> Bytes => _bytes;
            public int ScanSize => Info.ScanSize;
            public void Dispose() { }
        }

        [Test]
        public void ToQtImage_UploadsOnlyNewInstances_OneResource()
        {
            var messages = new RecordingMessages();
            using var node = new ToQtImageNode(_client, messages.Errors);
            var input = new TestImage(4, 2, PixelFormat.B8G8R8A8);
            var a = node.Update(input, ImageUpload.Auto)!;
            Assert.That(messages.Logged, Is.Empty);
            Assert.That(_api.Calls.Last(), Is.EqualTo($"pixels {a.Id:X} 4x2 row 16 fmt 9 pm 0 len 32"));
            Assert.That(node.Update(input, ImageUpload.Auto), Is.SameAs(a));
            Assert.That(_api.Calls.Count(c => c.StartsWith("pixels")), Is.EqualTo(1), "same instance: no upload");

            var b = node.Update(new TestImage(8, 8, PixelFormat.R8G8B8A8), ImageUpload.Auto)!;
            Assert.That(b, Is.SameAs(a), "a new input instance replaces the content, the QtImage stays (NODW-06)");
            Assert.That(_api.Calls.Count(c => c.StartsWith("pixels")), Is.EqualTo(2));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1));
            Assert.That(node.Update(null, ImageUpload.Auto), Is.Null);
            Pump();
            Assert.That(_api.Released, Is.Empty, "resource kept for the next input");
            node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id }));
        }

        [Test]
        public void ToQtImage_ANewInputEveryFrame_NoNewQtImage_NothingWithoutInput()
        {
            using var node = new ToQtImageNode(_client);
            var first = node.Update(new TestImage(4, 4, PixelFormat.R8G8B8A8), ImageUpload.Auto)!;
            for (int i = 0; i < 20; i++)
                Assert.That(node.Update(new TestImage(4, 4, PixelFormat.R8G8B8A8), ImageUpload.Auto), Is.SameAs(first));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1), "one resource");

            Assert.That(node.Update(null, ImageUpload.Auto), Is.Null);
            var again = node.Update(new TestImage(4, 4, PixelFormat.R8G8B8A8), ImageUpload.Auto)!;
            Assert.That(again.Id, Is.EqualTo(first.Id), "the resource is kept for the next input");
        }

        [Test]
        public void ToQtImage_Volatile_UploadsEveryFrame_SameImage()
        {
            using var node = new ToQtImageNode(_client);
            var input = new TestImage(4, 4, PixelFormat.R8G8B8A8, volatileImage: true);
            var a = node.Update(input, ImageUpload.Auto);
            Assert.That(node.Update(input, ImageUpload.Auto), Is.SameAs(a));
            Assert.That(_api.Calls.Count(c => c.StartsWith("pixels")), Is.EqualTo(2));
        }

        [Test]
        public void ToQtImage_UnsupportedFormat_MarksTheNode()
        {
            var messages = new RecordingMessages();
            using var node = new ToQtImageNode(_client, messages.Errors);
            var image = node.Update(new TestImage(2, 2, PixelFormat.Unknown), ImageUpload.Auto);
            Assert.That(image!.State, Is.EqualTo(QtImageState.Failed));
            Assert.That(messages.Shown, Does.Contain("not supported"));
            Assert.That(messages.Logged.Single(), Does.Contain("not supported"));
        }

        [Test]
        public void LoadImage_Upload_PassedToQtNative_ChangeReloads()
        {
            _api.OnWait = id => _api.Ready(id, 100, 50);
            using var node = new LoadImageNode(_client);
            var a = node.Update(P("C:/a.png"), false, ImageUpload.Auto);
            Assert.That(_api.Uploads, Is.EqualTo(new[] { Protocol.ImageUpload.Auto }), "default Auto");
            Assert.That(node.Update(P("C:/a.png"), false, ImageUpload.Worker), Is.Not.SameAs(a), "new mode = reload");
            Assert.That(node.Update(P("C:/a.png"), false, ImageUpload.Worker), Is.Not.Null);
            Assert.That(_api.Uploads, Is.EqualTo(new[] { Protocol.ImageUpload.Auto, Protocol.ImageUpload.Worker }), "unchanged mode: no reload");
        }

        [Test]
        public void LoadImageAsync_Upload_PassedToQtNative_ChangeReloads()
        {
            using var node = new LoadImageAsyncNode(_client);
            node.Update(P("C:/a.png"), false, true, ImageUpload.MainThread, out _, out _);
            node.Update(P("C:/a.png"), false, true, ImageUpload.MainThread, out _, out _);
            node.Update(P("C:/a.png"), false, true, ImageUpload.Auto, out _, out _);
            Assert.That(_api.Uploads, Is.EqualTo(new[] { Protocol.ImageUpload.MainThread, Protocol.ImageUpload.Auto }));
        }

        [Test]
        public void ToQtImage_Upload_PassedToQtNative_ChangeUploadsAgain()
        {
            using var node = new ToQtImageNode(_client);
            var input = new TestImage(4, 2, PixelFormat.B8G8R8A8);
            var a = node.Update(input, ImageUpload.Auto);
            node.Update(input, ImageUpload.Worker);
            Assert.That(node.Update(input, ImageUpload.Worker), Is.SameAs(a), "same input: same QtImage");
            Assert.That(_api.Uploads, Is.EqualTo(new[] { Protocol.ImageUpload.Auto, Protocol.ImageUpload.Worker }), "a new mode uploads the same input again, once");
        }

        [Test]
        public void ImagePin_SendsTheResourceId()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            var r = _client.CreateImage();
            var image = new QtImage(r);
            rec.Update(new Image { Source = image }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET"), Has.One.EqualTo($"SET 1 Image u64({r.Id})"));
            sink.Clear();
            rec.Update(new Image { Source = new QtImage(r) }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET"), Is.Empty, "same resource: nothing to send");
            rec.Update(new Image(), Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET"), Has.One.EqualTo($"SET 1 Image u64(0)"));
        }

        [Test]
        public void SharedImage_In100Elements_OneResource()
        {
            var r = _client.CreateImage();
            var image = new QtImage(r);
            var nodes = Enumerable.Range(0, 100).Select(_ => new ImageNode(_ => NoWarning.Instance)).ToArray();
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            var children = nodes.Select((n, i) => (Element)n.Update(new Vector2(1, 1), default, RectangleAnchor.Center, image,
                FillMode.Stretch, true, 1, false, false, null, null, true, out _, out _)).ToArray();
            rec.Update(new Group { Children = System.Collections.Immutable.ImmutableArray.Create(children) }, Space.DIPTopLeft);
            Assert.That(sink.OfOp("SET").Count(c => c.EndsWith($"u64({r.Id})")), Is.EqualTo(100));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1));
            foreach (var n in nodes)
                n.Dispose();
        }

        private sealed class NoWarning : IDisposable
        {
            public static readonly NoWarning Instance = new();
            public void Dispose() { }
        }
    }
}
