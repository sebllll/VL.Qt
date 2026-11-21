using System;
using NUnit.Framework;
using VL.Lib.Basics.Imaging;
using VL.Qt.Images;
using VL.Qt.Nodes;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>QtImage tooltip preview: the Preview flag only for the editor, ReadPreview, and the node reading once per image.</summary>
    public class QtImageTooltipPreviewTests
    {
        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;

        [SetUp]
        public void SetUp()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null, previews: true);
            _client.SetPumping(true);
        }

        private void Pump()
        {
            _client.BeforeEvents();
            _client.AfterEvents();
        }

        private QtImage ReadyImage(int width = 640, int height = 480)
        {
            var r = _client.CreateImage();
            _client.Load(r, "C:/a.png", false);
            _api.Ready(r.Id, width, height);
            Pump();
            return new QtImage(r);
        }

        [Test]
        public void Load_InTheEditor_SetsThePreviewFlag()
        {
            var r = _client.CreateImage();
            _client.Load(r, "C:/a.png", true);
            Assert.That(_api.LoadFlags, Is.EqualTo(new[] { Protocol.ImageLoadFlags.Mipmaps | Protocol.ImageLoadFlags.Preview }));
        }

        [Test]
        public void Load_InAnExportedApp_DoesNotSetThePreviewFlag()
        {
            var client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null, previews: false);
            var r = client.CreateImage();
            client.Load(r, "C:/a.png", true);
            client.Load(r, "C:/b.png", false);
            Assert.That(_api.LoadFlags, Is.EqualTo(new[] { Protocol.ImageLoadFlags.Mipmaps, 0u }));
        }

        [Test]
        public void SetPixels_FlagFollowsPreviews([Values(true, false)] bool previews)
        {
            var client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null, previews);
            var r = client.CreateImage();
            Assert.That(client.SetPixels(r, 2, 2, 8, PixelFormat.B8G8R8A8, true, new byte[16]), Is.True);
            Assert.That(_api.PixelFlags, Is.EqualTo(new[] { previews ? (int)Protocol.ImageLoadFlags.Preview : 0 }));
        }

        [Test]
        public void ReadPreview_CopiesThePixels_WithTheSizeOfQtNative()
        {
            var image = ReadyImage(4000, 2000);
            _api.PreviewSize = (256, 128);
            var preview = _client.ReadPreview(image.Resource);

            Assert.That(preview, Is.Not.Null);
            Assert.That((preview!.Info.Width, preview.Info.Height, preview.Info.Format, preview.Info.IsPremultipliedAlpha),
                Is.EqualTo((256, 128, PixelFormat.B8G8R8A8, true)));
            Assert.That(preview.IsVolatile, Is.False);
            using var data = preview.GetData();
            Assert.That(data.Bytes.Length, Is.EqualTo(256 * 128 * 4));
            Assert.That(data.Bytes.Span[0], Is.EqualTo(_api.PreviewByte));
            Assert.That(_api.PreviewCalls, Is.EqualTo(1), "the estimate from the image size is enough");
        }

        [Test]
        public void ReadPreview_SmallImage_KeepsItsSize()
        {
            var image = ReadyImage(100, 80);
            _api.PreviewSize = (100, 80);
            var preview = _client.ReadPreview(image.Resource);
            Assert.That((preview!.Info.Width, preview.Info.Height), Is.EqualTo((100, 80)));
            Assert.That(_api.PreviewCalls, Is.EqualTo(1));
        }

        [Test]
        public void ReadPreview_TooSmallBuffer_RetriesWithTheRequiredSize()
        {
            var image = ReadyImage(640, 480);
            _api.PreviewSize = (256, 200); // the estimate would be 256 x 192
            var preview = _client.ReadPreview(image.Resource);
            Assert.That((preview!.Info.Width, preview.Info.Height), Is.EqualTo((256, 200)));
            Assert.That(_api.PreviewCalls, Is.EqualTo(2));
        }

        [Test]
        public void ReadPreview_NoPreview_ReturnsNull()
        {
            var image = ReadyImage();
            Assert.That(_client.ReadPreview(image.Resource), Is.Null); // -7
        }

        [Test]
        public void ReadPreview_ReleasedResource_DoesNotCallQtNative()
        {
            var image = ReadyImage();
            _client.Release(image.Resource);
            _api.PreviewSize = (10, 10);
            Assert.That(_client.ReadPreview(image.Resource), Is.Null);
            Assert.That(_api.PreviewCalls, Is.Zero);
        }

        [Test]
        public void Node_ReadsOncePerImage_AndCaches()
        {
            var node = new QtImageTooltipPreview(_client);
            var image = ReadyImage(640, 480);
            _api.PreviewSize = (256, 192);

            var first = node.Update(image, out _);
            var second = node.Update(image, out _);
            var third = node.Update(image, out _);

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.SameAs(first));
            Assert.That(third, Is.SameAs(first));
            Assert.That(_api.PreviewCalls, Is.EqualTo(1));
        }

        [Test]
        public void Node_NewInstance_ReadsAgain()
        {
            var node = new QtImageTooltipPreview(_client);
            _api.PreviewSize = (64, 32);
            var a = node.Update(ReadyImage(), out _);
            var b = node.Update(ReadyImage(), out _);
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(_api.PreviewCalls, Is.EqualTo(2));
        }

        [Test]
        public void Node_Loading_ReadsOnReady_NotBefore()
        {
            var node = new QtImageTooltipPreview(_client);
            var r = _client.CreateImage();
            _client.Load(r, "C:/a.png", false);
            var image = new QtImage(r);
            _api.PreviewSize = (64, 32);

            Assert.That(node.Update(image, out _), Is.Null);
            Assert.That(_api.PreviewCalls, Is.Zero);

            _api.Ready(r.Id, 640, 480);
            Pump();
            Assert.That(node.Update(image, out _), Is.Not.Null);
            Assert.That(node.Update(image, out _), Is.Not.Null);
            Assert.That(_api.PreviewCalls, Is.EqualTo(1));
        }

        [Test]
        public void Node_Reload_OfTheSameImage_ReadsAgain()
        {
            var node = new QtImageTooltipPreview(_client);
            var image = ReadyImage(640, 480);
            _api.PreviewSize = (256, 192);
            var first = node.Update(image, out _);

            // Replacement: the resource stays Ready, the INFO record ends the load.
            _client.Load(image.Resource, "C:/b.png", false);
            Assert.That(node.Update(image, out _), Is.SameAs(first), "not finished yet");
            _api.Info(image.Resource.Id, 320, 240);
            Pump();
            var second = node.Update(image, out _);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(_api.PreviewCalls, Is.EqualTo(2));
        }

        [Test]
        public void Node_NoPreview_ReadsOnlyOnce_ThenFallsBackToTheText()
        {
            var node = new QtImageTooltipPreview(_client);
            var image = ReadyImage(640, 480); // PreviewSize null: -7
            Assert.That(node.Update(image, out var text), Is.Null);
            Assert.That(text, Is.EqualTo("QtImage 640 x 480 (Ready)"));
            node.Update(image, out _);
            node.Update(image, out _);
            Assert.That(_api.PreviewCalls, Is.EqualTo(1));
        }

        [Test]
        public void Node_TextIsToString_AndEmptyForNull()
        {
            var node = new QtImageTooltipPreview(_client);
            var image = ReadyImage(640, 480);
            node.Update(image, out var text);
            Assert.That(text, Is.EqualTo(image.ToString()));
            Assert.That(node.Update(null, out text), Is.Null);
            Assert.That(text, Is.Empty);
        }

        [Test]
        public void Node_Failed_ReturnsNull_WithoutReading()
        {
            var node = new QtImageTooltipPreview(_client);
            var r = _client.CreateImage();
            _client.Load(r, "C:/a.png", false);
            _api.Failed(r.Id, "broken");
            Pump();
            _api.PreviewSize = (64, 32);
            Assert.That(node.Update(new QtImage(r), out var text), Is.Null);
            Assert.That(text, Does.Contain("Failed"));
            Assert.That(_api.PreviewCalls, Is.Zero);
        }
    }
}
