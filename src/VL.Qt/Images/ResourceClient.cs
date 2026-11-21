using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Lib.Basics.Imaging;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>The resource C-API of QtNative (vlqt_res_*); a fake in unit tests, which have no Qt.</summary>
    internal interface IResourceApi
    {
        Native.Result Create(int kind, out ulong id);
        Native.Result Release(ulong id);
        Native.Result Poll(Span<byte> buffer, out int written, out int required);
        /// <summary>The state of the resource (<see cref="Protocol.ResState"/>) or a negative <see cref="Native.Result"/>.</summary>
        int Wait(ulong id, int timeoutMs);
        Native.Result LoadImage(ulong id, string utf8Path, uint flags);
        Native.Result LoadShader(ulong id, string utf8Path, int stage);
        Native.Result LoadShaderSource(ulong id, string source, int stage);
        Native.Result SetPixels(ulong id, in Native.vlqt_pixel_desc desc, ReadOnlySpan<byte> data);
        int List(Span<Native.vlqt_res_info> buffer);
        void Shutdown();
        Native.Result Snapshot(ulong id, IntPtr window, uint elementId, float scale);
        Native.Result ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required);
        Native.Result SnapshotFit(ulong id, IntPtr window, uint elementId, int maxEdgePx);
        Native.Result ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required);
        Native.Result AudioTapInit(ulong id, int channels, int sampleRate);
    }

    internal sealed class NativeResourceApi : IResourceApi
    {
        public Native.Result Create(int kind, out ulong id) => Native.vlqt_res_create(kind, out id);
        public Native.Result Release(ulong id) => Native.vlqt_res_release(id);
        public Native.Result Poll(Span<byte> buffer, out int written, out int required) => Native.ResPoll(buffer, out written, out required);
        public int Wait(ulong id, int timeoutMs) => Native.vlqt_res_wait(id, timeoutMs);
        public Native.Result LoadImage(ulong id, string utf8Path, uint flags) => Native.vlqt_res_load_image(id, utf8Path, flags);
        public Native.Result LoadShader(ulong id, string utf8Path, int stage) => Native.vlqt_res_load_shader(id, utf8Path, stage);
        public Native.Result LoadShaderSource(ulong id, string source, int stage) => Native.vlqt_res_load_shader_source(id, source, stage);
        public Native.Result SetPixels(ulong id, in Native.vlqt_pixel_desc desc, ReadOnlySpan<byte> data) => Native.ResSetPixels(id, in desc, data);
        public int List(Span<Native.vlqt_res_info> buffer) => Native.ResList(buffer);
        public void Shutdown() => Native.vlqt_res_shutdown();
        public Native.Result Snapshot(ulong id, IntPtr window, uint elementId, float scale) => Native.vlqt_res_snapshot(id, window, elementId, scale);
        public Native.Result ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required) => Native.ResReadSnapshot(id, buffer, out desc, out written, out required);
        public Native.Result SnapshotFit(ulong id, IntPtr window, uint elementId, int maxEdgePx) => Native.vlqt_res_snapshot_fit(id, window, elementId, maxEdgePx);
        public Native.Result ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required) => Native.ResReadPreview(id, buffer, out desc, out written, out required);
        public Native.Result AudioTapInit(ulong id, int channels, int sampleRate) => Native.vlqt_res_audio_tap_init(id, channels, sampleRate);
    }

    /// <summary>
    /// C# side of QtNative's resource manager, one per <see cref="QtApp"/>:
    /// <list type="bullet">
    /// <item>Creates image resources and starts loads/pixel uploads for the nodes (LoadImage, LoadImage (Async), ToQtImage).</item>
    /// <item>Empties the status queue (vlqt_res_poll) once per pump on the main thread and updates the
    /// <see cref="ImageResource"/>s; completes their tasks and calls <see cref="ImageResource.Settled"/> there.</item>
    /// <item>Release queue: a node's Dispose only queues the id (any thread); vlqt_res_release runs in the next pump, after
    /// the commands of that pump are applied (an element switching away from the image acquires the new one first).
    /// Without a running pump (app stopped) the release runs at once on the main thread.</item>
    /// <item>When the last pump stops and nothing is referenced any more, vlqt_res_shutdown joins the workers; the next
    /// load starts them again.</item>
    /// </list>
    /// No finalizers and no native callbacks. Main (Qt) thread only, except <see cref="Release"/>.
    /// </summary>
    internal sealed class ResourceClient
    {
        /// <summary>LoadImage waits at most this long for a worker; afterwards the image arrives like an async one.</summary>
        internal const int waitTimeoutMs = 10_000;

        /// <summary>Longest edge of the tooltip preview QtNative makes.</summary>
        private const int previewMaxEdge = 256;

        /// <summary>Path prefix of a shader shipped with VL.Qt, resolved by QtNative (vlqt_res_load_shader).</summary>
        internal const string builtinShaderPrefix = "builtin:";

        private readonly IResourceApi _api;
        private readonly int _threadId;
        private readonly Func<ILogger?> _logger;
        private readonly Dictionary<ulong, ImageResource> _live = new();
        private readonly object _syncRoot = new();
        private readonly List<ImageResource> _releases = new();
        private ImageResource[] _flushing = new ImageResource[8];
        private byte[] _buffer = new byte[4096];
        private Native.vlqt_res_info[] _list = new Native.vlqt_res_info[64];
        private bool _pumping;

        public ResourceClient(IResourceApi api, int threadId, Func<ILogger?> logger, bool previews = false)
        {
            _api = api;
            _threadId = threadId;
            _logger = logger;
            Previews = previews;
        }

        /// <summary>
        /// Whether loads also ask the worker for a tooltip preview (<see cref="Protocol.Img.Preview"/>): only while the
        /// vvvv editor runs, never in an exported app (it costs a small copy per image).
        /// </summary>
        public bool Previews { get; internal set; }

        /// <summary>
        /// The client of the process-wide QtApp for a node of <paramref name="appHost"/>. Makes sure the app has its pump
        /// (decode results, uploads, status and release queues), also without a Renderer [Qt].
        /// </summary>
        public static ResourceClient ForApp(AppHost appHost)
        {
            QtPump.ForApp(appHost);
            var client = QtApp.Get(null).Resources;
            client.Previews = !appHost.IsExported;
            return client;
        }

        private ILogger? Logger => _logger();

        private bool IsMainThread => Environment.CurrentManagedThreadId == _threadId;

        /// <summary>Resources created and not yet released.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Releases waiting for the next pump.</summary>
        public int PendingReleases
        {
            get
            {
                lock (_syncRoot)
                    return _releases.Count;
            }
        }

        /// <summary>New image resource (Loading, no content yet). Throws if QtNative refuses (logged by the node).</summary>
        public ImageResource CreateImage()
        {
            var rc = _api.Create(Protocol.ResKind.Image, out var id);
            if (rc != Native.Result.Ok)
                throw new InvalidOperationException($"vlqt_res_create failed ({rc})");
            var resource = new ImageResource(id);
            _live.Add(id, resource);
            return resource;
        }

        /// <summary>
        /// New shader resource (Loading, no content yet); <see cref="ImageResource"/> is the generic resource view
        /// (id, state, error) also for shaders. Throws if QtNative refuses (reported by the node).
        /// </summary>
        public ImageResource CreateShader()
        {
            var rc = _api.Create(Protocol.ResKind.Shader, out var id);
            if (rc != Native.Result.Ok)
                throw new InvalidOperationException($"vlqt_res_create failed ({rc})");
            var resource = new ImageResource(id);
            _live.Add(id, resource);
            return resource;
        }

        /// <summary>
        /// Recognizes <c>builtin:&lt;name&gt;</c>, also after vvvv resolved a Path value against the document directory
        /// (<c>C:\doc\dir\builtin:wave</c>): then the last path segment carries the prefix.
        /// </summary>
        internal static bool TryGetBuiltinShader(string path, out string builtin)
        {
            var segment = path.AsSpan();
            var i = path.LastIndexOfAny(new[] { '\\', '/' });
            if (i >= 0)
                segment = segment.Slice(i + 1);
            if (segment.StartsWith(builtinShaderPrefix, StringComparison.Ordinal))
            {
                builtin = segment.ToString();
                return true;
            }
            builtin = path;
            return false;
        }

        /// <summary>
        /// Starts loading the .qsb <paramref name="path"/> of <paramref name="stage"/> (<see cref="Protocol.ShaderStage"/>) into
        /// <paramref name="resource"/> (asynchronous, errors arrive as status records). <c>builtin:&lt;name&gt;</c> (a shader shipped
        /// with VL.Qt, see <see cref="TryGetBuiltinShader"/>) is passed as <c>builtin:&lt;name&gt;</c>, any other path is made absolute here. Returns false and fails the resource if
        /// the path is invalid or QtNative refuses.
        /// </summary>
        public bool LoadShader(ImageResource resource, string path, int stage)
        {
            if (resource.IsReleased)
                return false;
            var fullPath = path;
            if (TryGetBuiltinShader(path, out var builtin))
                fullPath = builtin;
            else
            {
                try
                {
                    fullPath = System.IO.Path.GetFullPath(path);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
                {
                    resource.Fail($"Invalid path '{path}': {e.Message}");
                    return false;
                }
            }
            resource.OnLoadStarted();
            var rc = _api.LoadShader(resource.Id, fullPath, stage);
            if (rc != Native.Result.Ok)
            {
                resource.Fail($"Loading shader '{fullPath}' was refused by QtNative ({rc})");
                Settle(resource);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Starts baking full Qt GLSL <paramref name="source"/> (stage <see cref="Protocol.ShaderStage"/>) into
        /// <paramref name="resource"/> (asynchronous, bake errors arrive as status records). Returns false and fails
        /// the resource if QtNative refuses.
        /// </summary>
        public bool LoadShaderSource(ImageResource resource, string source, int stage)
        {
            if (resource.IsReleased)
                return false;
            resource.OnLoadStarted();
            var rc = _api.LoadShaderSource(resource.Id, source, stage);
            if (rc != Native.Result.Ok)
            {
                resource.Fail($"Baking the shader source was refused by QtNative ({rc})");
                Settle(resource);
                return false;
            }
            return true;
        }

        /// <summary>
        /// New audio tap (protocol.h "Audio tap") with a fixed format (float, channels, sampleRate): Ready at once,
        /// its id goes to <see cref="Elements.Video"/>.AudioTap. A new sample rate needs a new tap; release the old one
        /// with <see cref="Release"/> after the Video switched to the new id. Throws if QtNative refuses.
        /// </summary>
        public ImageResource CreateAudioTap(int channels, int sampleRate)
        {
            var rc = _api.Create(Protocol.ResKind.AudioTap, out var id);
            if (rc != Native.Result.Ok)
                throw new InvalidOperationException($"vlqt_res_create failed ({rc})");
            var resource = new ImageResource(id);
            _live.Add(id, resource);
            rc = _api.AudioTapInit(id, channels, sampleRate);
            if (rc != Native.Result.Ok)
            {
                Release(resource);
                throw new InvalidOperationException($"vlqt_res_audio_tap_init failed ({rc}, {channels} channels, {sampleRate} Hz)");
            }
            return resource;
        }

        /// <summary>New snapshot resource: no content until the first <see cref="Snapshot"/> is done. Throws if QtNative refuses.</summary>
        public ImageResource CreateSnapshot()
        {
            var rc = _api.Create(Protocol.ResKind.Snapshot, out var id);
            if (rc != Native.Result.Ok)
                throw new InvalidOperationException($"vlqt_res_create failed ({rc})");
            var resource = new ImageResource(id);
            _live.Add(id, resource);
            return resource;
        }

        /// <summary>
        /// Starts grabbing element <paramref name="elementId"/> of <paramref name="window"/> into <paramref name="resource"/>
        /// (asynchronous, <see cref="ImageResource.Settled"/> fires in a later pump). Returns false and fails the resource
        /// if QtNative refuses.
        /// </summary>
        public bool Snapshot(ImageResource resource, IntPtr window, uint elementId, float scale)
        {
            if (resource.IsReleased)
                return false;
            resource.OnLoadStarted();
            var rc = _api.Snapshot(resource.Id, window, elementId, scale);
            if (rc != Native.Result.Ok)
            {
                resource.Fail($"The snapshot of element {elementId} was refused by QtNative ({rc})");
                Settle(resource);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Like <see cref="Snapshot"/>, but QtNative picks the scale so that the longest edge is at most
        /// <paramref name="maxEdgePx"/> pixels and never above the DIP size (tooltip previews).
        /// </summary>
        public bool SnapshotFit(ImageResource resource, IntPtr window, uint elementId, int maxEdgePx)
        {
            if (resource.IsReleased)
                return false;
            resource.OnLoadStarted();
            var rc = _api.SnapshotFit(resource.Id, window, elementId, maxEdgePx);
            if (rc != Native.Result.Ok)
            {
                resource.Fail($"The snapshot of element {elementId} was refused by QtNative ({rc})");
                Settle(resource);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Copies the pixels of the last finished snapshot into a new <see cref="IImage"/> (BGRA8 premultiplied, own memory,
        /// not volatile). Null if there is none yet or the read failed (logged).
        /// </summary>
        public IImage? ReadSnapshot(ImageResource resource)
        {
            if (resource.IsReleased)
                return null;
            var bytes = new byte[Math.Max(0, resource.Width) * Math.Max(0, resource.Height) * 4];
            var rc = _api.ReadSnapshot(resource.Id, bytes, out var desc, out var written, out var required);
            if (rc == Native.Result.BufferTooSmall)
            {
                // The snapshot was replaced by one of another size since the last INFO record.
                bytes = new byte[required];
                rc = _api.ReadSnapshot(resource.Id, bytes, out desc, out written, out _);
            }
            if (rc != Native.Result.Ok || written <= 0)
            {
                // No snapshot yet and a stale id are normal.
                if (rc is not (Native.Result.NoData or Native.Result.UnknownId))
                    Logger?.LogWarning("vlqt_res_read_pixels of snapshot resource {Id:X} returned {Rc}: {Error}", resource.Id, rc, Native.LastError());
                return null;
            }
            return new SnapshotImage(desc.width, desc.height, bytes);
        }

        /// <summary>
        /// Copies the tooltip preview of a Ready image (made by the worker for <see cref="Protocol.Img.Preview"/>, at most
        /// 256 px) into a new <see cref="IImage"/> (BGRA8 premultiplied, own memory). Null if there is none.
        /// </summary>
        public IImage? ReadPreview(ImageResource resource)
        {
            if (resource.IsReleased)
                return null;
            // QtNative scales to at most 256 px on the longest edge, never up: estimate the size from the image.
            var width = Math.Max(0, resource.Width);
            var height = Math.Max(0, resource.Height);
            var longest = Math.Max(width, height);
            if (longest > previewMaxEdge)
            {
                width = Math.Max(1, (int)((long)width * previewMaxEdge / longest));
                height = Math.Max(1, (int)((long)height * previewMaxEdge / longest));
            }
            var bytes = new byte[width * height * 4];
            var rc = _api.ReadPreview(resource.Id, bytes, out var desc, out var written, out var required);
            if (rc == Native.Result.BufferTooSmall)
            {
                // The estimate was too small (rounding, or a replacement of another size).
                bytes = new byte[required];
                rc = _api.ReadPreview(resource.Id, bytes, out desc, out written, out _);
            }
            if (rc != Native.Result.Ok || written <= 0)
            {
                // No preview (no flag, not Ready yet) and a stale id are normal.
                if (rc is not (Native.Result.NoData or Native.Result.UnknownId))
                    Logger?.LogWarning("vlqt_res_read_preview of image resource {Id:X} returned {Rc}: {Error}", resource.Id, rc, Native.LastError());
                return null;
            }
            return new SnapshotImage(desc.width, desc.height, bytes);
        }

        /// <summary>The pixels of a finished snapshot: BGRA8 premultiplied, owned by the image, rows packed (width * 4).</summary>
        private sealed class SnapshotImage : IImage, IImageData
        {
            private readonly byte[] _bytes;

            public SnapshotImage(int width, int height, byte[] bytes)
            {
                Info = new ImageInfo(width, height, PixelFormat.B8G8R8A8, isPremultipliedAlpha: true);
                _bytes = bytes;
            }

            public ImageInfo Info { get; }
            public bool IsVolatile => false;
            public IImageData GetData() => this;
            public ReadOnlyMemory<byte> Bytes => _bytes;
            public int ScanSize => Info.Width * 4;
            public void Dispose() { }
        }

        /// <summary>
        /// Starts loading <paramref name="path"/> (made absolute here) into <paramref name="resource"/>. Returns false and
        /// fails the resource if QtNative refuses.
        /// </summary>
        public bool Load(ImageResource resource, string path, bool mipmaps)
        {
            if (resource.IsReleased)
                return false;
            string fullPath;
            try
            {
                fullPath = System.IO.Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                resource.Fail($"Invalid path '{path}': {e.Message}");
                return false;
            }
            resource.OnLoadStarted();
            var flags = (mipmaps ? Protocol.Img.Mipmaps : 0) | (Previews ? Protocol.Img.Preview : 0);
            var rc = _api.LoadImage(resource.Id, fullPath, flags);
            if (rc != Native.Result.Ok)
            {
                resource.Fail($"Loading '{fullPath}' was refused by QtNative ({rc})");
                Settle(resource);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Blocks until <paramref name="resource"/> is Ready or Failed (decode on this thread if no worker took it yet,
        /// upload at once), then applies the status queue so size and state are current.
        /// </summary>
        public void Wait(ImageResource resource, int timeoutMs = waitTimeoutMs)
        {
            if (resource.IsReleased || resource.State == QtImageState.Failed)
                return;
            var state = _api.Wait(resource.Id, timeoutMs);
            Poll();
            if (state > 0 && state != Protocol.ResState.Ready && state != Protocol.ResState.Failed)
                Logger?.LogWarning("Image resource {Id:X} still loading after {Timeout} ms, it is shown when ready", resource.Id, timeoutMs);
        }

        /// <summary>
        /// Replaces the content of <paramref name="resource"/> with pixels (copied synchronously by QtNative). Returns
        /// false and fails the resource if the format is not supported or QtNative refuses.
        /// </summary>
        public bool SetPixels(ImageResource resource, int width, int height, int rowBytes, PixelFormat format, bool premultiplied, ReadOnlySpan<byte> data)
        {
            if (resource.IsReleased)
                return false;
            var qtNativeFormat = ToQtNativeFormat(format);
            if (qtNativeFormat == 0)
            {
                resource.Fail($"Pixel format {format} is not supported");
                Settle(resource);
                return false;
            }
            resource.OnLoadStarted();
            var desc = new Native.vlqt_pixel_desc
            {
                width = width,
                height = height,
                row_bytes = rowBytes,
                format = qtNativeFormat,
                premultiplied = premultiplied ? 1 : 0,
                flags = Previews ? (int)Protocol.Img.Preview : 0,
            };
            var rc = _api.SetPixels(resource.Id, in desc, data);
            if (rc != Native.Result.Ok)
            {
                resource.Fail($"{width} x {height} {format} pixels ({data.Length} bytes, {rowBytes} per row) were refused by QtNative ({rc})");
                Settle(resource);
                return false;
            }
            return true;
        }

        /// <summary>
        /// VL <see cref="PixelFormat"/> → format number of vlqt_res_set_pixels (protocol.h: the VL numbers, append-only).
        /// 0 = not supported (Unknown or a format newer than QtNative's table).
        /// </summary>
        internal static int ToQtNativeFormat(PixelFormat format) => format switch
        {
            PixelFormat.R8 => 1,
            PixelFormat.R16 => 2,
            PixelFormat.R32F => 3,
            PixelFormat.R8G8B8 => 4,
            PixelFormat.B8G8R8 => 5,
            PixelFormat.R8G8B8X8 => 6,
            PixelFormat.R8G8B8A8 => 7,
            PixelFormat.B8G8R8X8 => 8,
            PixelFormat.B8G8R8A8 => 9,
            PixelFormat.R32G32B32A32F => 10,
            PixelFormat.R32G32F => 11,
            PixelFormat.R16G16B16A16F => 12,
            _ => 0,
        };

        /// <summary>
        /// Queues the release of <paramref name="resource"/> (idempotent, any thread). vlqt_res_release runs in the next
        /// pump, or at once when no pump runs and this is the main thread.
        /// </summary>
        public void Release(ImageResource? resource)
        {
            if (resource is null)
                return;
            lock (_syncRoot)
            {
                if (resource.IsReleased)
                    return;
                resource.MarkReleased();
                _releases.Add(resource);
            }
            if (!_pumping && IsMainThread)
            {
                FlushReleases();
                TryShutdown();
            }
        }

        /// <summary>Pump: after the participants applied their commands, before Qt processes events.</summary>
        public void BeforeEvents() => FlushReleases();

        /// <summary>Pump: after Qt processed events (uploads done), before the participants poll their windows.</summary>
        public void AfterEvents()
        {
            if (_live.Count > 0)
                Poll();
        }

        /// <summary>QtApp: whether at least one pump (vvvv app) runs. The last one stopping flushes and may shut down.</summary>
        public void SetPumping(bool pumping)
        {
            _pumping = pumping;
            if (!pumping && IsMainThread)
            {
                FlushReleases();
                TryShutdown();
            }
        }

        private void FlushReleases()
        {
            int count;
            lock (_syncRoot)
            {
                count = _releases.Count;
                if (count == 0)
                    return;
                if (_flushing.Length < count)
                    _flushing = new ImageResource[Math.Max(count, _flushing.Length * 2)];
                _releases.CopyTo(_flushing);
                _releases.Clear();
            }
            for (int i = 0; i < count; i++)
            {
                var resource = _flushing[i];
                _flushing[i] = null!;
                _live.Remove(resource.Id);
                resource.CancelSettled();
                var rc = _api.Release(resource.Id);
                if (rc != Native.Result.Ok)
                    Logger?.LogWarning("vlqt_res_release of image resource {Id:X} returned {Rc}", resource.Id, rc);
            }
        }

        /// <summary>
        /// vlqt_res_shutdown (joins the workers) once nothing is referenced: no live image here and no resource held by
        /// anyone else (an element of a window still open, another API user). A clean end therefore logs no leak.
        /// </summary>
        private void TryShutdown()
        {
            if (_live.Count > 0)
                return;
            int count;
            while (true)
            {
                count = _api.List(_list);
                if (count <= _list.Length)
                    break;
                _list = new Native.vlqt_res_info[count * 2];
            }
            for (int i = 0; i < count; i++)
            {
                if (_list[i].host_refs > 0 || _list[i].element_refs > 0)
                    return;
            }
            _api.Shutdown();
        }

        /// <summary>Applies all queued status records (main thread).</summary>
        internal void Poll()
        {
            var rc = _api.Poll(_buffer, out var n, out var required);
            if (rc == Native.Result.BufferTooSmall)
            {
                // The records stay queued; once more with the size QtNative asked for.
                _buffer = new byte[Math.Max(required, _buffer.Length * 2)];
                rc = _api.Poll(_buffer, out n, out _);
            }
            if (rc != Native.Result.Ok)
            {
                Logger?.LogWarning("vlqt_res_poll returned {Rc}: {Error}", rc, Native.LastError());
                return;
            }
            var span = _buffer.AsSpan(0, n);
            while (span.Length >= 12)
            {
                var ev = BinaryPrimitives.ReadUInt16LittleEndian(span);
                var len = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                var size = (4 + len + 3) & ~3;
                if (len < 8 || 4 + len > span.Length)
                {
                    Logger?.LogError("Malformed resource status record (event {Event}, len {Len}, {Left} bytes left)", ev, len, span.Length);
                    return;
                }
                var id = BinaryPrimitives.ReadUInt64LittleEndian(span[4..]);
                var payload = span.Slice(12, len - 8);
                if (_live.TryGetValue(id, out var resource))
                    Dispatch(resource, ev, payload);
                span = span[Math.Min(size, span.Length)..];
            }
        }

        private void Dispatch(ImageResource resource, ushort ev, ReadOnlySpan<byte> payload)
        {
            switch (ev)
            {
                case Protocol.ResEv.State when payload.Length >= 1:
                    resource.OnState(payload[0]);
                    if (resource.State != QtImageState.Loading)
                        Settle(resource);
                    break;
                case Protocol.ResEv.Error:
                    resource.OnError(Encoding.UTF8.GetString(payload));
                    break;
                case Protocol.ResEv.Info when payload.Length >= 8:
                    var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    var replaced = resource.State == QtImageState.Ready;
                    resource.OnInfo(width, height);
                    // A replacement keeps the resource Ready: no STATE record, the INFO record marks its end.
                    if (replaced)
                        Settle(resource);
                    break;
                case Protocol.ResEv.Warning:
                    resource.OnWarning(Encoding.UTF8.GetString(payload));
                    break;
            }
        }

        private void Settle(ImageResource resource)
        {
            resource.CompleteSettled();
            try
            {
                resource.Settled?.Invoke(resource);
            }
            catch (Exception e)
            {
                Logger?.LogError(e, "Image resource {Id:X}: a subscriber of the load result failed", resource.Id);
            }
        }
    }
}
