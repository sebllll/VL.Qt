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
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Wire;
using Image = VL.Qt.Elements.Image;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests
{
    /// <summary>
    /// The resource API of QtNative without Qt: records calls, hands out ids and status records like vlqt_res_*.
    /// </summary>
    internal sealed class FakeResourceApi : IResourceApi
    {
        private readonly List<byte> _queue = new();
        private uint _next;

        public readonly List<string> Calls = new();
        public readonly List<ulong> Released = new();
        public Native.Result CreateRc, LoadRc, SnapshotRc;
        /// <summary>Size of the last finished snapshot as ReadSnapshot reports it (null = none yet: NoData); its bytes are <see cref="SnapshotByte"/>.</summary>
        public (int Width, int Height)? SnapshotSize;
        public byte SnapshotByte = 0x7F;
        public int Shutdowns;
        /// <summary>Size of the preview ReadPreview reports (null = none: NoData); its bytes are <see cref="PreviewByte"/>.</summary>
        public (int Width, int Height)? PreviewSize;
        public byte PreviewByte = 0x55;
        public int PreviewCalls;
        /// <summary>Flags of the LoadImage calls and desc.flags of the SetPixels calls.</summary>
        public readonly List<uint> LoadFlags = new();
        public readonly List<int> PixelFlags = new();
        /// <summary>Records written into the queue when Wait is called (QtNative's synchronous decode + upload).</summary>
        public Action<ulong>? OnWait;
        public Native.vlqt_res_info[] Listed = Array.Empty<Native.vlqt_res_info>();
        public int Polls;

        public Native.Result AudioTapInit(ulong id, int channels, int sampleRate)
        {
            Calls.Add($"audio tap init {id:X} {channels} {sampleRate}");
            return Native.Result.Ok;
        }

        public Native.Result Create(int kind, out ulong id)
        {
            id = 0;
            if (CreateRc != Native.Result.Ok)
                return CreateRc;
            id = ++_next | (7UL << 32); // index | generation
            Calls.Add($"create {kind}");
            return Native.Result.Ok;
        }

        public Native.Result Release(ulong id)
        {
            Released.Add(id);
            Calls.Add($"release {id:X}");
            return Native.Result.Ok;
        }

        public Native.Result Poll(Span<byte> buffer, out int written, out int required)
        {
            Polls++;
            written = 0;
            required = _queue.Count;
            if (_queue.Count > buffer.Length)
                return Native.Result.BufferTooSmall;
            var n = _queue.Count;
            for (int i = 0; i < n; i++)
                buffer[i] = _queue[i];
            _queue.Clear();
            written = n;
            return Native.Result.Ok;
        }

        public int Wait(ulong id, int timeoutMs)
        {
            Calls.Add($"wait {id:X}");
            OnWait?.Invoke(id);
            return Protocol.ResourceState.Ready;
        }

        public Native.Result LoadImage(ulong id, string utf8Path, uint flags)
        {
            Calls.Add($"load {id:X} {utf8Path} {flags}");
            LoadFlags.Add(flags);
            return LoadRc;
        }

        public Native.Result LoadShader(ulong id, string utf8Path, int stage)
        {
            Calls.Add($"shader {id:X} {utf8Path} {stage}");
            return LoadRc;
        }

        public Native.Result LoadShaderSource(ulong id, string source, int stage)
        {
            Calls.Add($"source {id:X} {source.Length} {stage}");
            return LoadRc;
        }

        public Native.Result SetPixels(ulong id, in Native.vlqt_pixel_desc desc, ReadOnlySpan<byte> data)
        {
            Calls.Add($"pixels {id:X} {desc.width}x{desc.height} row {desc.row_bytes} fmt {desc.format} pm {desc.premultiplied} len {data.Length}");
            PixelFlags.Add(desc.flags);
            return Native.Result.Ok;
        }

        public int List(Span<Native.vlqt_res_info> buffer)
        {
            for (int i = 0; i < Listed.Length && i < buffer.Length; i++)
                buffer[i] = Listed[i];
            return Listed.Length;
        }

        public void Shutdown() => Shutdowns++;

        public Native.Result Snapshot(ulong id, SceneHandle window, uint elementId, float scale)
        {
            Calls.Add($"snapshot {id:X} {elementId} {scale}");
            return SnapshotRc;
        }

        public Native.Result SnapshotFit(ulong id, SceneHandle window, uint elementId, int maxEdgePx)
        {
            Calls.Add($"snapshot_fit {id:X} {elementId} {maxEdgePx}");
            return SnapshotRc;
        }

        public Native.Result ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required)
        {
            desc = default;
            written = required = 0;
            if (SnapshotSize is not { } size)
                return Native.Result.NoData;
            required = size.Width * size.Height * 4;
            desc = new Native.vlqt_pixel_desc { width = size.Width, height = size.Height, row_bytes = size.Width * 4, format = 9, premultiplied = 1 };
            if (buffer.Length < required)
                return Native.Result.BufferTooSmall;
            buffer[..required].Fill(SnapshotByte);
            written = required;
            return Native.Result.Ok;
        }

        public Native.Result ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required)
        {
            PreviewCalls++;
            desc = default;
            written = required = 0;
            if (PreviewSize is not { } size)
                return Native.Result.NoData;
            required = size.Width * size.Height * 4;
            desc = new Native.vlqt_pixel_desc { width = size.Width, height = size.Height, row_bytes = size.Width * 4, format = 9, premultiplied = 1 };
            if (buffer.Length < required)
                return Native.Result.BufferTooSmall;
            buffer[..required].Fill(PreviewByte);
            written = required;
            return Native.Result.Ok;
        }

        private void Record(ushort ev, ulong id, ReadOnlySpan<byte> payload)
        {
            var len = 8 + payload.Length;
            Span<byte> head = stackalloc byte[Protocol.HeaderSize + 8];
            BinaryPrimitives.WriteUInt16LittleEndian(head, ev);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)len);
            BinaryPrimitives.WriteUInt64LittleEndian(head[Protocol.HeaderSize..], id);
            _queue.AddRange(head.ToArray());
            _queue.AddRange(payload.ToArray());
            while (_queue.Count % 4 != 0)
                _queue.Add(0);
        }

        public void State(ulong id, byte state) => Record(Protocol.ResourceEventId.State, id, new[] { state });
        public void Error(ulong id, string text) => Record(Protocol.ResourceEventId.Error, id, Encoding.UTF8.GetBytes(text));
        public void Warning(ulong id, string text) => Record(Protocol.ResourceEventId.Warning, id, Encoding.UTF8.GetBytes(text));

        public void Info(ulong id, int width, int height)
        {
            var p = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), (uint)height);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 7);
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(12), (ulong)(width * height * 4));
            Record(Protocol.ResourceEventId.Info, id, p);
        }

        public void Ready(ulong id, int width = 64, int height = 32)
        {
            Info(id, width, height);
            State(id, Protocol.ResourceState.Ready);
        }

        public void Failed(ulong id, string error)
        {
            Error(id, error);
            State(id, Protocol.ResourceState.Failed);
        }
    }

    /// <summary>ResourceClient: status records, release queue, stale ids, shutdown, format mapping.</summary>
    public class ResourceClientTests
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

        /// <summary>One pump as QtApp runs it: releases after apply, status after processEvents.</summary>
        private void Pump()
        {
            _client.BeforeEvents();
            _client.AfterEvents();
        }

        [Test, Ignore("SIP-03: a Settled handler that calls Wait polls into the buffer the outer Poll still parses; the remaining records of the batch are lost")]
        public void Settled_HandlerCallsWait_TheRestOfTheBatchIsStillApplied()
        {
            var a = _client.CreateImage();
            var b = _client.CreateImage();
            var c = _client.CreateImage();
            var d = _client.CreateImage();
            _client.Load(a, "C:/a.png", false);
            _client.Load(b, "C:/b.png", false);
            _client.Load(c, "C:/c.png", false);
            _client.Load(d, "C:/d.png", false);
            // What a patch does in On Loaded: a synchronous load, which Waits and so polls again.
            a.Settled = _ => _client.Wait(d);
            // The inner poll brings more bytes than the outer batch has already read.
            _api.OnWait = id => { _api.Ready(id, 4, 4); _api.Ready(id, 4, 4); _api.Ready(id, 4, 4); };

            _api.Ready(a.Id, 1, 1);
            _api.Ready(b.Id, 2, 2);
            _api.Ready(c.Id, 3, 3);
            Pump();

            Assert.That(new[] { a.State, b.State, c.State, d.State }, Is.All.EqualTo(QtImageState.Ready));
            Assert.That((b.Width, c.Width, d.Width), Is.EqualTo((2, 3, 4)));
        }

        [Test]
        public void States_LoadingToReady_WithSize()
        {
            var r = _client.CreateImage();
            Assert.That(_client.Load(r, "C:/a.png", false), Is.True);
            Assert.That(r.State, Is.EqualTo(QtImageState.Loading));
            var settled = 0;
            r.Settled = _ => settled++;
            var task = r.WhenSettled();

            _api.State(r.Id, Protocol.ResourceState.Decoding);
            Pump();
            Assert.That(r.State, Is.EqualTo(QtImageState.Loading));
            Assert.That(settled, Is.Zero);

            _api.Ready(r.Id, 640, 480);
            Pump();
            Assert.That(r.State, Is.EqualTo(QtImageState.Ready));
            Assert.That((r.Width, r.Height), Is.EqualTo((640, 480)));
            Assert.That(settled, Is.EqualTo(1));
            Assert.That(task.IsCompletedSuccessfully, Is.True);
            var image = new QtImage(r);
            Assert.That((image.Width, image.Height, image.State, image.Error), Is.EqualTo((640, 480, QtImageState.Ready, "")));
        }

        [Test]
        public void States_Failed_WithError()
        {
            var r = _client.CreateImage();
            _client.Load(r, "C:/missing.png", false);
            _api.Failed(r.Id, "file not found");
            Pump();
            Assert.That(r.State, Is.EqualTo(QtImageState.Failed));
            Assert.That(r.Error, Is.EqualTo("file not found"));

            // Loading again starts over (Failed -> Decoding in QtNative).
            _client.Load(r, "C:/a.png", false);
            Assert.That((r.State, r.Error), Is.EqualTo((QtImageState.Loading, "")));
        }

        [Test]
        public void Replacement_StaysReady_InfoSettles()
        {
            var r = _client.CreateImage();
            _client.Load(r, "C:/a.png", false);
            _api.Ready(r.Id, 10, 10);
            Pump();
            var settled = 0;
            r.Settled = _ => settled++;
            _client.Load(r, "C:/b.png", false);
            Assert.That(r.State, Is.EqualTo(QtImageState.Ready), "old content stays visible");
            _api.Info(r.Id, 20, 30);
            Pump();
            Assert.That((r.State, r.Width, r.Height, settled), Is.EqualTo((QtImageState.Ready, 20, 30, 1)));
        }

        [Test]
        public void Load_MakesThePathAbsolute_AndPassesMipmaps()
        {
            var r = _client.CreateImage();
            _client.Load(r, "rel/a.png", true);
            Assert.That(_api.Calls.Last(), Is.EqualTo($"load {r.Id:X} {System.IO.Path.GetFullPath("rel/a.png")} 1"));
        }

        [Test]
        public void Load_Refused_FailsAndSettles()
        {
            _api.LoadRc = Native.Result.BadArgument;
            var r = _client.CreateImage();
            var settled = 0;
            r.Settled = _ => settled++;
            Assert.That(_client.Load(r, "C:/a.png", false), Is.False);
            Assert.That(r.State, Is.EqualTo(QtImageState.Failed));
            Assert.That(r.Error, Does.Contain("BadArgument"));
            Assert.That(settled, Is.EqualTo(1));
        }

        [Test]
        public void Create_Refused_Throws()
        {
            _api.CreateRc = Native.Result.Internal;
            Assert.Throws<InvalidOperationException>(() => _client.CreateImage());
            Assert.That(_client.LiveCount, Is.Zero);
        }

        [Test]
        public void StaleOrUnknownId_IsIgnored()
        {
            var r = _client.CreateImage();
            _api.Ready(0xDEAD_0000_0001UL);
            _api.State(r.Id, Protocol.ResourceState.Decoding);
            Assert.DoesNotThrow(Pump);
            Assert.That(r.State, Is.EqualTo(QtImageState.Loading));
            Assert.That(r.Width, Is.Zero);
        }

        [Test]
        public void ReleaseQueue_ExactlyOneRelease_InTheNextPump()
        {
            var r = _client.CreateImage();
            var task = r.WhenSettled();
            _client.Release(r);
            _client.Release(r);
            Assert.That(_api.Released, Is.Empty, "not before the pump");
            Assert.That(_client.PendingReleases, Is.EqualTo(1));
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { r.Id }));
            Assert.That(task.IsCanceled, Is.True, "an unfinished load is cancelled");
            Pump();
            _client.Release(r);
            Pump();
            Assert.That(_api.Released, Has.Count.EqualTo(1), "exactly one release");
            Assert.That(_client.LiveCount, Is.Zero);
        }

        [Test]
        public void ReleaseQueue_FromAnotherThread_FlushedOnMainThread()
        {
            var r = _client.CreateImage();
            System.Threading.Tasks.Task.Run(() => _client.Release(r)).Wait();
            Assert.That(_api.Released, Is.Empty);
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { r.Id }));
        }

        [Test]
        public void Release_NoRecordsAfterwards()
        {
            var r = _client.CreateImage();
            _client.Release(r);
            Pump();
            _api.Ready(r.Id);
            Pump();
            Assert.That(r.State, Is.EqualTo(QtImageState.Loading), "records for a released id are dropped");
        }

        [Test]
        public void NoPump_ReleasesAtOnce_AndShutsDownWhenNothingIsReferenced()
        {
            var a = _client.CreateImage();
            var b = _client.CreateImage();
            _client.SetPumping(false);
            Assert.That(_api.Shutdowns, Is.Zero, "images still live");
            _client.Release(a);
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id }), "released at once without a pump");
            Assert.That(_api.Shutdowns, Is.Zero);
            _client.Release(b);
            Assert.That(_api.Shutdowns, Is.EqualTo(1), "last one released: workers joined");
        }

        [Test]
        public void LastPumpStops_NoShutdown_WhileSomeoneElseHoldsAResource()
        {
            _api.Listed = new[] { new Native.vlqt_res_info { id = 1, element_refs = 1 } };
            _client.SetPumping(false);
            Assert.That(_api.Shutdowns, Is.Zero);
            _api.Listed = Array.Empty<Native.vlqt_res_info>();
            _client.SetPumping(true);
            _client.SetPumping(false);
            Assert.That(_api.Shutdowns, Is.EqualTo(1));
        }

        [Test]
        public void Poll_OnlyWithLiveImages_GrowsTheBuffer()
        {
            Pump();
            Assert.That(_api.Polls, Is.Zero, "nothing to poll without images");
            var r = _client.CreateImage();
            _api.Error(r.Id, new string('x', 6000));
            _api.State(r.Id, Protocol.ResourceState.Failed);
            Pump();
            Assert.That(r.Error, Has.Length.EqualTo(6000));
            Assert.That(r.State, Is.EqualTo(QtImageState.Failed));
        }

        [Test]
        public void Warning_IsKept()
        {
            var r = _client.CreateImage();
            _api.Warning(r.Id, "16 bit reduced to 8 bit");
            _api.Ready(r.Id);
            Pump();
            Assert.That(r.Warning, Is.EqualTo("16 bit reduced to 8 bit"));
        }

        [Test]
        public void SubscriberException_IsCaught()
        {
            var r = _client.CreateImage();
            r.Settled = _ => throw new InvalidOperationException("boom");
            _api.Ready(r.Id);
            Assert.DoesNotThrow(Pump);
            Assert.That(r.State, Is.EqualTo(QtImageState.Ready));
        }

        [TestCase(PixelFormat.Unknown, 0)]
        [TestCase(PixelFormat.R8, 1)]
        [TestCase(PixelFormat.R16, 2)]
        [TestCase(PixelFormat.R32F, 3)]
        [TestCase(PixelFormat.R8G8B8, 4)]
        [TestCase(PixelFormat.B8G8R8, 5)]
        [TestCase(PixelFormat.R8G8B8X8, 6)]
        [TestCase(PixelFormat.R8G8B8A8, 7)]
        [TestCase(PixelFormat.B8G8R8X8, 8)]
        [TestCase(PixelFormat.B8G8R8A8, 9)]
        [TestCase(PixelFormat.R32G32B32A32F, 10)]
        [TestCase(PixelFormat.R32G32F, 11)]
        [TestCase(PixelFormat.R16G16B16A16F, 12)]
        [TestCase((PixelFormat)99, 0)]
        public void FormatMapping_VlPixelFormat_ToQtNativeNumbers(PixelFormat format, int expected)
            => Assert.That(ResourceClient.ToQtNativeFormat(format), Is.EqualTo(expected));

        [Test]
        public void SetPixels_UnknownFormat_FailsWithoutNativeCall()
        {
            var r = _client.CreateImage();
            Assert.That(_client.SetPixels(r, 2, 2, 8, PixelFormat.Unknown, false, new byte[16]), Is.False);
            Assert.That(r.State, Is.EqualTo(QtImageState.Failed));
            Assert.That(_api.Calls.Any(c => c.StartsWith("pixels")), Is.False);
        }
    }

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
            using var node = new LoadImageNode(_client);
            var image = node.Update(P("C:/a.png"), false, out var error);
            Assert.That(image, Is.Not.Null);
            Assert.That((image!.State, image.Width, image.Height, error), Is.EqualTo((QtImageState.Ready, 100, 50, "")));
            Assert.That(node.Update(P("C:/a.png"), false, out _), Is.SameAs(image), "unchanged path = same image, no reload");
            Assert.That(_api.Calls.Count(c => c.StartsWith("load")), Is.EqualTo(1));
        }

        [Test]
        public void LoadImage_PathChange_NewImage_OldReleasedInNextPump()
        {
            _api.OnWait = id => _api.Ready(id);
            using var node = new LoadImageNode(_client);
            var a = node.Update(P("C:/a.png"), false, out _)!;
            var b = node.Update(P("C:/b.png"), false, out _)!;
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(b.Id, Is.Not.EqualTo(a.Id));
            Assert.That(_api.Released, Is.Empty);
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id }));
            Assert.That(node.Update(P(""), false, out _), Is.Null, "empty path = no image");
            node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id, b.Id }));
        }

        [Test]
        public void LoadImage_Failed_ErrorOutput()
        {
            _api.OnWait = id => _api.Failed(id, "cannot decode");
            using var node = new LoadImageNode(_client);
            var image = node.Update(P("C:/broken.png"), false, out var error);
            Assert.That(image!.State, Is.EqualTo(QtImageState.Failed));
            Assert.That(error, Is.EqualTo("cannot decode"));
        }

        [Test]
        public void LoadImageAsync_KeepPrevious_SwitchesWhenReady()
        {
            using var node = new LoadImageAsyncNode(_client);
            var loaded = new List<QtImage>();
            QtImage? Update(string path, out bool isLoaded, out string error, bool keep = true)
            {
                var image = node.Update(P(path), false, keep, out isLoaded, out error, out var onLoaded);
                if (loaded.Count == 0 && onLoaded is not null)
                    onLoaded.Subscribe(loaded.Add);
                return image;
            }

            var first = Update("C:/a.png", out var isLoaded, out _);
            Assert.That(first, Is.Not.Null, "nothing before: the loading image is the output");
            Assert.That(isLoaded, Is.False);
            var idA = _api.Calls.Count;
            _api.Ready(first!.Id);
            Pump();
            Assert.That(loaded, Is.EqualTo(new[] { first }));
            Assert.That(Update("C:/a.png", out isLoaded, out _), Is.SameAs(first));
            Assert.That(isLoaded, Is.True);

            var stillA = Update("C:/b.png", out isLoaded, out _);
            Assert.That(stillA, Is.SameAs(first), "previous stays while B loads");
            Assert.That(isLoaded, Is.False);
            var pendingId = first.Id + 1;
            _api.Ready(pendingId);
            Pump();
            var b = Update("C:/b.png", out isLoaded, out _);
            Assert.That(b, Is.Not.SameAs(first));
            Assert.That(b!.Id, Is.EqualTo(pendingId));
            Assert.That(isLoaded, Is.True);
            Assert.That(loaded, Has.Count.EqualTo(2));
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { first.Id }), "A released after the switch");

            // C fails: B stays, Error tells.
            Update("C:/c.png", out _, out _);
            _api.Failed(pendingId + 1, "not found");
            Pump();
            var stillB = Update("C:/c.png", out isLoaded, out var error);
            Assert.That(stillB, Is.SameAs(b));
            Assert.That((isLoaded, error), Is.EqualTo((false, "not found")));
        }

        [Test]
        public void LoadImageAsync_WithoutKeepPrevious_SwitchesAtOnce()
        {
            using var node = new LoadImageAsyncNode(_client);
            var a = node.Update(P("C:/a.png"), false, false, out _, out _, out _);
            var b = node.Update(P("C:/b.png"), false, false, out var isLoaded, out _, out _);
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(b!.State, Is.EqualTo(QtImageState.Loading));
            Assert.That(isLoaded, Is.False);
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a!.Id }));
        }

        [Test]
        public void LoadImageAsync_PathChangeWhileLoading_CancelsThePending()
        {
            using var node = new LoadImageAsyncNode(_client);
            var a = node.Update(P("C:/a.png"), false, true, out _, out _, out _)!;
            _api.Ready(a.Id);
            Pump();
            node.Update(P("C:/b.png"), false, true, out _, out _, out _);
            node.Update(P("C:/c.png"), false, true, out _, out _, out _);
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
            using var node = new ToQtImageNode(_client);
            var input = new TestImage(4, 2, PixelFormat.B8G8R8A8);
            var a = node.Update(input, out var error)!;
            Assert.That(error, Is.Empty);
            Assert.That(_api.Calls.Last(), Is.EqualTo($"pixels {a.Id:X} 4x2 row 16 fmt 9 pm 0 len 32"));
            Assert.That(node.Update(input, out _), Is.SameAs(a));
            Assert.That(_api.Calls.Count(c => c.StartsWith("pixels")), Is.EqualTo(1), "same instance: no upload");

            var b = node.Update(new TestImage(8, 8, PixelFormat.R8G8B8A8), out _)!;
            Assert.That(b, Is.Not.SameAs(a), "new instance = new QtImage");
            Assert.That(b.Id, Is.EqualTo(a.Id), "one resource per node, content replaced");
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1));
            Assert.That(node.Update(null, out _), Is.Null);
            Pump();
            Assert.That(_api.Released, Is.Empty, "resource kept for the next input");
            node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { a.Id }));
        }

        [Test]
        public void ToQtImage_Volatile_UploadsEveryFrame_SameImage()
        {
            using var node = new ToQtImageNode(_client);
            var input = new TestImage(4, 4, PixelFormat.R8G8B8A8, volatileImage: true);
            var a = node.Update(input, out _);
            Assert.That(node.Update(input, out _), Is.SameAs(a));
            Assert.That(_api.Calls.Count(c => c.StartsWith("pixels")), Is.EqualTo(2));
        }

        [Test]
        public void ToQtImage_UnsupportedFormat_Error()
        {
            using var node = new ToQtImageNode(_client);
            var image = node.Update(new TestImage(2, 2, PixelFormat.Unknown), out var error);
            Assert.That(image!.State, Is.EqualTo(QtImageState.Failed));
            Assert.That(error, Does.Contain("not supported"));
        }

        [Test]
        public void ImagePin_SendsTheResourceId()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new SceneRegistry());
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
            var rec = new Reconciler(sink, new IdAllocator(), new SceneRegistry());
            var children = nodes.Select((n, i) => n.Update(new Vector2(1, 1), default, RectangleAnchor.Center, image,
                ImageFillMode.Stretch, true, 1, false, false, null, null, out _, out _)).ToArray();
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
