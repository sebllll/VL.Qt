using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using VL.Lib.Basics.Imaging;

namespace VL.Qt.Tests
{
    /// <summary>ImageFileWriter on a fake write function: worker thread, newest wins, one report per finished write.</summary>
    public class ImageFileWriterTests
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

        // NUnit shares one fixture instance between the tests: all state is made in SetUp.
        private ConcurrentQueue<(string Path, int Quality, byte FirstByte, int ThreadId)> _writes = null!;
        private ManualResetEventSlim _started = null!, _gate = null!;
        private Func<string, string> _result = null!;
        private ImageFileWriter _writer = null!;

        [SetUp]
        public void SetUp()
        {
            _writes = new();
            _started = new(false);
            // Open: the fake write returns at once; closed: it blocks until the test opens it.
            _gate = new(true);
            _result = _ => string.Empty;
            _writer = new ImageFileWriter((image, path, quality) =>
            {
                using var data = image.GetData();
                _writes.Enqueue((path, quality, data.Bytes.Span[0], Environment.CurrentManagedThreadId));
                _started.Set();
                Assert.That(_gate.Wait(timeout), Is.True, "the test did not release the write");
                return _result(path);
            });
        }

        [TearDown]
        public void TearDown()
        {
            _gate.Set();
            _writer.WaitIdle(timeout);
            _writer.Dispose();
        }

        private void Finish() => Assert.That(_writer.WaitIdle(timeout), Is.True, "the writes did not finish");

        [Test]
        public void Poll_Idle_NothingToReport()
        {
            Assert.That(_writer.Poll(out var error), Is.False);
            Assert.That(error, Is.Empty);
            Assert.That(_writer.IsBusy, Is.False);
        }

        [Test]
        public void Start_WritesOnWorkerThread_PollReportsOnce()
        {
            _gate.Reset();
            _writer.Start(new TestImage(2, 2, 0x11), "a.png", 80);
            Assert.That(_started.Wait(timeout), Is.True);
            Assert.That(_writer.IsBusy, Is.True);
            Assert.That(_writer.Poll(out _), Is.False, "still running");

            _gate.Set();
            Finish();
            Assert.That(_writer.IsBusy, Is.False);
            var write = _writes.Single();
            Assert.That(write.Path, Is.EqualTo("a.png"));
            Assert.That(write.Quality, Is.EqualTo(80));
            Assert.That(write.FirstByte, Is.EqualTo(0x11));
            Assert.That(write.ThreadId, Is.Not.EqualTo(Environment.CurrentManagedThreadId));
            Assert.That(_writer.Poll(out var error), Is.True);
            Assert.That(error, Is.Empty);
            Assert.That(_writer.Poll(out _), Is.False, "reported once");
        }

        [Test]
        public void Start_WriteFails_ErrorIsReported()
        {
            _result = _ => "disk full";
            _writer.Start(new TestImage(2, 2, 1), "a.png", 90);
            Finish();
            Assert.That(_writer.Poll(out var error), Is.True);
            Assert.That(error, Is.EqualTo("disk full"));
        }

        [Test]
        public void Start_WriteThrows_BecomesTheError()
        {
            _result = _ => throw new InvalidOperationException("boom");
            _writer.Start(new TestImage(2, 2, 1), "a.png", 90);
            Finish();
            Assert.That(_writer.Poll(out var error), Is.True);
            Assert.That(error, Is.EqualTo("boom"));

            _result = _ => string.Empty;
            _writer.Start(new TestImage(2, 2, 1), "b.png", 90);
            Finish();
            Assert.That(_writer.Poll(out error), Is.True, "the writer keeps working");
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void Start_WhileRunning_NewestWaitingWins_OnlyItReports()
        {
            _gate.Reset();
            _writer.Start(new TestImage(2, 2, 1), "first.png", 90);
            Assert.That(_started.Wait(timeout), Is.True);
            _writer.Start(new TestImage(2, 2, 2), "second.png", 90);
            _writer.Start(new TestImage(2, 2, 3), "third.png", 90);

            _gate.Set();
            Finish();
            Assert.That(_writes.Select(w => w.Path), Is.EqualTo(new[] { "first.png", "third.png" }), "the second was replaced");
            Assert.That(_writer.Poll(out _), Is.True);
            Assert.That(_writer.Poll(out _), Is.False, "one report for the newest write");
        }

        [Test]
        public void Start_VolatileImage_IsCopiedBeforeStartReturns()
        {
            _gate.Reset();
            var image = new TestImage(2, 2, 0x22, isVolatile: true);
            _writer.Start(image, "a.png", 90);
            Array.Fill(image.Buffer, (byte)0x99); // the source reuses its buffer
            _gate.Set();
            Finish();
            Assert.That(_writes.Single().FirstByte, Is.EqualTo(0x22));
        }

        [Test]
        public void Dispose_DropsWaitingWrite_ReportsNothing()
        {
            _gate.Reset();
            _writer.Start(new TestImage(2, 2, 1), "first.png", 90);
            Assert.That(_started.Wait(timeout), Is.True);
            _writer.Start(new TestImage(2, 2, 2), "second.png", 90);
            _writer.Dispose();
            _gate.Set();
            Finish();
            Assert.That(_writes.Select(w => w.Path), Is.EqualTo(new[] { "first.png" }), "the running write finishes, the waiting one is dropped");
            Assert.That(_writer.Poll(out _), Is.False);
            _writer.Start(new TestImage(2, 2, 3), "third.png", 90);
            Finish();
            Assert.That(_writes, Has.Count.EqualTo(1), "nothing starts after Dispose");
        }

        [Test]
        public void Native_UnsupportedPixelFormat_IsAnErrorWithoutThrowing()
        {
            // Refused before QtNative is called.
            using var native = ImageFileWriter.CreateNative();
            native.Start(new TestImage(2, 2, 1, format: PixelFormat.Unknown), "a.png", 90);
            Assert.That(native.WaitIdle(timeout), Is.True);
            Assert.That(native.Poll(out var error), Is.True);
            Assert.That(error, Does.Contain("not supported"));
        }
    }
}
