using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using VL.Lib.Animation;
using VL.Lib.Basics.Imaging;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;
using VL.Qt.Nodes;

namespace VL.Qt.Tests
{
    /// <summary>VideoSourceToQtImage (#114, CPU path) with a fake video source and the fake resource API, no Qt.</summary>
    public class VideoSourceToQtImageTests
    {
        private sealed class Clock : IFrameClock
        {
            private readonly Subject<FrameFinishedMessage> _finished = new();
            private readonly Subject<FrameTimeMessage> _ticks = new();
            public Time Time => new(0);
            public double TimeDifference => 1.0 / 60;
            public IObservable<FrameTimeMessage> GetTicks() => _ticks;
            public IObservable<FrameFinishedMessage> GetFrameFinished() => _finished;
        }

        private sealed record MemoryFrame(int W, int H, bool InMemory = true) : VideoFrame
        {
            private readonly byte[] _bytes = new byte[W * H * 4];
            public override int Width => W;
            public override int Height => H;
            public override PixelFormat PixelFormat => PixelFormat.B8G8R8A8;
            public override int PixelSizeInBytes => 4;
            public override bool TryGetMemory(out ReadOnlyMemory<byte> memory)
            {
                memory = InMemory ? _bytes : default;
                return InMemory;
            }
        }

        /// <summary>IVideoSource2: every Start creates a player that hands out a new refcounted frame per grab.</summary>
        private sealed class FakeSource : IVideoSource2
        {
            public int Created, Released, Started, Stopped;
            public int Ticket;
            public bool InMemory = true;
            public Exception? GrabError;
            public int ChangedTicket => Ticket;

            public IVideoPlayer? Start(VideoPlaybackContext ctx)
            {
                Interlocked.Increment(ref Started);
                return new Player(this);
            }

            private sealed class Player : IVideoPlayer
            {
                private readonly FakeSource _source;
                public Player(FakeSource source) => _source = source;

                public IResourceProvider<VideoFrame>? GrabVideoFrame()
                {
                    if (_source.GrabError is { } e)
                        throw e;
                    Interlocked.Increment(ref _source.Created);
                    return ResourceProvider.Return<VideoFrame>(new MemoryFrame(4, 2, _source.InMemory), _ => Interlocked.Increment(ref _source.Released));
                }

                public void Dispose() => Interlocked.Increment(ref _source.Stopped);
            }
        }

        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;
        private VideoPlaybackContext _ctx = null!;

        [SetUp]
        public void SetUp()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
            _client.SetPumping(true);
            _ctx = new VideoPlaybackContext(new Clock(), NullLogger.Instance);
        }

        private void Pump()
        {
            _client.BeforeEvents();
            _client.AfterEvents();
        }

        private static QtImage? UpdateUntilFrame(VideoSourceToQtImageNode node, IVideoSource source, QtImage? fallback = null)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 5000)
            {
                var image = node.Update(source, fallback, out _, preferPush: true);
                if (image is not null && !ReferenceEquals(image, fallback))
                    return image;
                Thread.Sleep(5);
            }
            return null;
        }

        [Test]
        public void Pull_UploadsEveryFrame_SameImage_ReleasesFrames()
        {
            var source = new FakeSource();
            using (var node = new VideoSourceToQtImageNode(_client, _ctx))
            {
                var a = node.Update(source, null, out var error, preferPush: false)!;
                Assert.That(error, Is.Empty);
                Assert.That(_api.Calls.Last(), Is.EqualTo($"pixels {a.Id:X} 4x2 row 16 fmt 9 pm 0 len 32"));
                Assert.That(node.Update(source, null, out _, preferPush: false), Is.SameAs(a), "content replaced, same QtImage");
                Assert.That(_api.Calls.Count(c => c.StartsWith("pixels")), Is.EqualTo(2));
                Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1));
                Assert.That(source.Started, Is.EqualTo(1));
                Assert.That(source.Released, Is.EqualTo(source.Created), "frames released right after the upload");
                node.Dispose();
                Pump();
                Assert.That(_api.Released, Is.EqualTo(new[] { a.Id }));
            }
            Assert.That(source.Stopped, Is.EqualTo(1));
        }

        [Test]
        public void Push_FramesFromThread_DisposeStopsAndReleasesAll()
        {
            var source = new FakeSource();
            var node = new VideoSourceToQtImageNode(_client, _ctx);
            var a = UpdateUntilFrame(node, source);
            Assert.That(a, Is.Not.Null);
            var b = UpdateUntilFrame(node, source);
            Assert.That(b, Is.SameAs(a));
            node.Dispose();
            Assert.That(source.Stopped, Is.EqualTo(1), "player disposed on its thread before Dispose returns");
            Assert.That(source.Released, Is.EqualTo(source.Created), "queued and dropped frames released");
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a!.Id }));
        }

        [Test]
        public void SourceChange_FallbackUntilNewFrame_OneResource()
        {
            var first = new FakeSource();
            var second = new FakeSource();
            var fallback = new QtImage(_client.CreateImage());
            using var node = new VideoSourceToQtImageNode(_client, _ctx);
            Assert.That(node.Update(null, fallback, out _), Is.SameAs(fallback));
            var a = node.Update(first, fallback, out _, preferPush: false)!;
            Assert.That(a, Is.Not.SameAs(fallback));
            var b = UpdateUntilFrame(node, second, fallback);
            Assert.That(b, Is.SameAs(a), "same resource and image for the new source");
            Assert.That(first.Stopped, Is.EqualTo(1));
            Assert.That(first.Released, Is.EqualTo(first.Created));
            Assert.That(node.Update(null, fallback, out _), Is.SameAs(fallback));
            Assert.That(second.Stopped, Is.EqualTo(1));
            Assert.That(second.Released, Is.EqualTo(second.Created));
            Pump();
            Assert.That(_api.Released, Is.Empty, "resource kept for the next source");
        }

        [Test]
        public void ChangedTicket_Restarts()
        {
            var source = new FakeSource();
            using var node = new VideoSourceToQtImageNode(_client, _ctx);
            node.Update(source, null, out _, preferPush: false);
            node.Update(source, null, out _, preferPush: false);
            source.Ticket++;
            node.Update(source, null, out _, preferPush: false);
            Assert.That(source.Started, Is.EqualTo(2));
            Assert.That(source.Stopped, Is.EqualTo(1));
        }

        [Test]
        public void GrabError_ShownAtError()
        {
            var source = new FakeSource { GrabError = new InvalidOperationException("decoder broke") };
            using var node = new VideoSourceToQtImageNode(_client, _ctx);
            Assert.That(node.Update(source, null, out var error, preferPush: false), Is.Null);
            Assert.That(error, Does.Contain("decoder broke"));
            source.GrabError = null;
            Assert.That(node.Update(source, null, out error, preferPush: false), Is.Not.Null);
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void FrameNotInMemory_Error_FrameReleased()
        {
            var source = new FakeSource { InMemory = false };
            using var node = new VideoSourceToQtImageNode(_client, _ctx);
            Assert.That(node.Update(source, null, out var error, preferPush: false), Is.Null);
            Assert.That(error, Does.Contain("not in CPU memory"));
            Assert.That(source.Released, Is.EqualTo(source.Created));
            Assert.That(_api.Calls.Any(c => c.StartsWith("pixels")), Is.False);
        }
    }
}
