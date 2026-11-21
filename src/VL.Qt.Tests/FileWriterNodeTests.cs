using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using Path = VL.Lib.IO.Path;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>FileWriter (IImage) and FileWriter (Element) on fake writes and a fake resource API.</summary>
    public class FileWriterNodeTests
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

        // NUnit shares one fixture instance between the tests: all state is made in SetUp.
        private List<(string Path, int Quality, int Width, int Height, byte FirstByte)> _writes = null!;
        private string _writeError = null!;
        private ImageFileWriter _writer = null!;
        private FileWriterNode _node = null!;

        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;
        private SceneRegistry _scene = null!;
        private Reconciler _reconciler = null!;
        private ImageFileWriter _elementWriter = null!;
        private FileWriterElementNode _elementNode = null!;
        private Rectangle _element = null!;

        private ImageFileWriter NewWriter() => new((image, path, quality) =>
        {
            using var data = image.GetData();
            lock (_writes)
                _writes.Add((path, quality, image.Info.Width, image.Info.Height, data.Bytes.Span[0]));
            return _writeError;
        });

        [SetUp]
        public void SetUp()
        {
            _writes = new();
            _writeError = string.Empty;
            _writer = NewWriter();
            _node = new FileWriterNode(_writer);

            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
            _client.SetPumping(true);
            _scene = new SceneRegistry();
            _reconciler = new Reconciler(new RecordingSink(), new IdAllocator(), _scene);
            _elementWriter = NewWriter();
            _elementNode = new FileWriterElementNode(_client, _scene, _elementWriter);
            _element = new Rectangle { Size = new Vector2(10, 10) };
        }

        [TearDown]
        public void TearDown()
        {
            _node.Dispose();
            _elementNode.Dispose();
        }

        private static void Finish(ImageFileWriter writer) => Assert.That(writer.WaitIdle(timeout), Is.True, "the write did not finish");

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

        private void Update(IImage? image, string? path, out bool isDone, out string error, bool write = false, int quality = 90)
            => _node.Update(image, path is null ? null! : new Path(path), out isDone, out error, quality, write);

        private void UpdateElement(string? path, out bool isDone, out string error, bool write = false, float scale = 1, int quality = 90)
            => _elementNode.Update(_element, path is null ? null! : new Path(path), out isDone, out error, scale, quality, write);

        private ulong SnapshotId() => Convert.ToUInt64(_api.Calls.Where(c => c.StartsWith("snapshot")).Last().Split(' ')[1], 16);

        // ---- FileWriter (IImage)

        [Test]
        public void Write_StartsAWrite_IsDoneInALaterFrame_OnlyOnce()
        {
            var image = new TestImage(4, 3, 0x33);
            Update(image, "out/a.png", out var done, out var error, write: true, quality: 70);
            Assert.That(done, Is.False, "never in the frame of Write");
            Assert.That(error, Is.Empty);
            Finish(_writer);
            Update(image, "out/a.png", out done, out error);
            Assert.That(done, Is.True);
            Assert.That(error, Is.Empty);
            Assert.That(_writes.Single(), Is.EqualTo(("out/a.png", 70, 4, 3, (byte)0x33)));
            Update(image, "out/a.png", out done, out _);
            Assert.That(done, Is.False, "one frame only");
            Assert.That(_writes, Has.Count.EqualTo(1), "no write without Write");
        }

        [Test]
        public void NoWrite_NothingStarts()
        {
            Update(new TestImage(2, 2, 1), "a.png", out var done, out var error);
            Finish(_writer);
            Update(new TestImage(2, 2, 1), "a.png", out done, out error);
            Assert.That(done, Is.False);
            Assert.That(error, Is.Empty);
            Assert.That(_writes, Is.Empty);
        }

        [Test]
        public void Write_Fails_ErrorStaysUntilTheNextWriteStarts()
        {
            var image = new TestImage(2, 2, 1);
            _writeError = "disk full";
            Update(image, "a.png", out _, out _, write: true);
            Finish(_writer);
            Update(image, "a.png", out var done, out var error);
            Assert.That(done, Is.True);
            Assert.That(error, Is.EqualTo("disk full"));
            Update(image, "a.png", out done, out error);
            Assert.That(done, Is.False);
            Assert.That(error, Is.EqualTo("disk full"), "stays");

            _writeError = string.Empty;
            Update(image, "a.png", out done, out error, write: true);
            Assert.That(error, Is.Empty, "cleared when the next write starts");
            Finish(_writer);
            Update(image, "a.png", out done, out error);
            Assert.That(done, Is.True);
            Assert.That(error, Is.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        public void Write_WithoutPath_ErrorAndDone_NoWrite(string? path)
        {
            Update(new TestImage(2, 2, 1), path, out var done, out var error, write: true);
            Assert.That(done, Is.True);
            Assert.That(error, Is.EqualTo(FileWriterNode.NoPathError));
            Assert.That(_writes, Is.Empty);
        }

        [Test]
        public void Write_WithoutImage_ErrorAndDone_NoWrite()
        {
            Update(null, "a.png", out var done, out var error, write: true);
            Assert.That(done, Is.True);
            Assert.That(error, Is.EqualTo(FileWriterNode.NoImageError));
            Assert.That(_writes, Is.Empty);
        }

        // ---- FileWriter (Element)

        [Test]
        public void Element_NotShown_ErrorAndDone_NoSnapshot()
        {
            UpdateElement("a.png", out var done, out var error, write: true);
            Assert.That(done, Is.True);
            Assert.That(error, Is.EqualTo(SnapshotNode.NotShownError));
            Assert.That(_api.Calls, Is.Empty, "no resource created");
            Assert.That(_writes, Is.Empty);
        }

        [Test]
        public void Element_WithoutPath_ErrorAndDone_NoSnapshot()
        {
            Show();
            UpdateElement("", out var done, out var error, write: true);
            Assert.That(done, Is.True);
            Assert.That(error, Is.EqualTo(FileWriterNode.NoPathError));
            Assert.That(_api.Calls, Is.Empty);
        }

        [Test]
        public void Element_Write_GrabsThenWritesTheSnapshot_DoneWhenWritten()
        {
            Show();
            _api.SnapshotSize = (6, 3);
            UpdateElement("shot.png", out var done, out var error, write: true, scale: 2, quality: 55);
            Assert.That(done, Is.False);
            Assert.That(error, Is.Empty);
            Assert.That(_api.Calls.Single(c => c.StartsWith("snapshot")), Does.EndWith($" {_reconciler.Root!.Id} 2"));
            Assert.That(_writes, Is.Empty, "still grabbing");

            _api.Ready(SnapshotId(), 6, 3);
            Pump();
            UpdateElement("shot.png", out done, out error);
            Assert.That(done, Is.False, "the file write has just started");
            Finish(_elementWriter);
            UpdateElement("shot.png", out done, out error);
            Assert.That(done, Is.True);
            Assert.That(error, Is.Empty);
            Assert.That(_writes.Single(), Is.EqualTo(("shot.png", 55, 6, 3, (byte)0x7F)));
            UpdateElement("shot.png", out done, out _);
            Assert.That(done, Is.False);
        }

        [Test]
        public void Element_GrabFails_ErrorAndDone()
        {
            Show();
            UpdateElement("a.png", out _, out _, write: true);
            _api.Failed(SnapshotId(), "grab failed");
            Pump();
            UpdateElement("a.png", out var done, out var error);
            Assert.That(done, Is.True);
            Assert.That(error, Is.EqualTo("grab failed"));
            Assert.That(_writes, Is.Empty);
        }

        [Test]
        public void Element_GrabRefused_ErrorAndDone()
        {
            Show();
            _api.SnapshotRc = Native.Result.BadArgument;
            UpdateElement("a.png", out var done, out var error, write: true, scale: 0);
            if (!done)
                UpdateElement("a.png", out done, out error);
            Assert.That(done, Is.True);
            Assert.That(error, Does.Contain("refused"));
            Assert.That(_writes, Is.Empty);
        }

        [Test]
        public void Element_FileWriteFails_ErrorAndDone()
        {
            Show();
            _api.SnapshotSize = (4, 4);
            _writeError = "no permission";
            UpdateElement("a.png", out _, out _, write: true);
            _api.Ready(SnapshotId(), 4, 4);
            Pump();
            UpdateElement("a.png", out _, out _);
            Finish(_elementWriter);
            UpdateElement("a.png", out var done, out var error);
            Assert.That(done, Is.True);
            Assert.That(error, Is.EqualTo("no permission"));
        }

        [Test, Ignore("NODW-03: the first grab to settle is written to the path of the second Write (A's pixels in b.png)")]
        public void Element_SecondWriteBeforeTheFirstGrabSettled_WritesTheSecondGrabToTheSecondPath()
        {
            Show();
            UpdateElement("a.png", out _, out _, write: true);
            UpdateElement("b.png", out _, out _, write: true);

            // The grab of the first Write settles first (QtNative does not cancel it): 6 wide.
            _api.SnapshotSize = (6, 3);
            _api.Ready(SnapshotId(), 6, 3);
            Pump();
            UpdateElement("b.png", out _, out _);
            Finish(_elementWriter);
            Assert.That(_writes, Is.Empty, "the first grab is not written to the path of the second Write");

            // The grab of the second Write settles: 7 wide, written to b.png.
            _api.SnapshotSize = (7, 3);
            _api.Ready(SnapshotId(), 7, 3);
            Pump();
            UpdateElement("b.png", out _, out _);
            Finish(_elementWriter);
            Assert.That(_writes.Single().Path, Is.EqualTo("b.png"));
            Assert.That(_writes.Single().Width, Is.EqualTo(7));
        }

        [Test]
        public void Element_Dispose_ReleasesTheSnapshotResource()
        {
            Show();
            UpdateElement("a.png", out _, out _, write: true);
            var id = SnapshotId();
            _elementNode.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { id }));
        }
    }
}
