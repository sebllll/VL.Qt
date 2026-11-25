using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Lib.Basics.Imaging;
using VL.Qt.Images;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt
{
    /// <summary>
    /// C# side of QtNative's resource manager, one per <see cref="QtApp"/>:
    /// <list type="bullet">
    /// <item>Creates image resources and starts loads/pixel uploads for the nodes (LoadImage, LoadImage (Async), ToQtImage).</item>
    /// <item>Empties the status queue (vlqt_res_poll) once per pump on the main thread and updates the
    /// <see cref="QtResource"/>s; completes their tasks and calls <see cref="QtResource.Settled"/> there.</item>
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

        private readonly IResourceApi _api;
        private readonly int _threadId;
        private readonly Func<ILogger?> _logger;
        private readonly Dictionary<ulong, QtResource> _live = new();
        private readonly object _syncRoot = new();
        private readonly List<QtResource> _releases = new();
        private QtResource[] _flushing = new QtResource[8];
        private byte[] _buffer = new byte[4096];
        private Native.vlqt_res_info[] _list = new Native.vlqt_res_info[64];
        private bool _pumping;
        // Resources whose load ended in the records just applied; settled after the last record (see Poll).
        private readonly List<QtResource> _toSettle = new();
        private bool _settling;

        public ResourceClient(IResourceApi api, int threadId, Func<ILogger?> logger, bool previews = false)
        {
            _api = api;
            _threadId = threadId;
            _logger = logger;
            Previews = previews;
        }

        /// <summary>
        /// Whether loads also ask the worker for a tooltip preview (<see cref="Protocol.ImageLoadFlags.Preview"/>): only while the
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
            var client = QtApp.Get().Resources;
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
        public QtResource CreateImage() => Create(Protocol.ResourceKind.Image);

        /// <summary>
        /// New shader resource (Loading, no content yet); <see cref="QtResource"/> is the generic resource view
        /// (id, state, error) also for shaders. Throws if QtNative refuses (reported by the node).
        /// </summary>
        public QtResource CreateShader() => Create(Protocol.ResourceKind.Shader);

        /// <summary>New snapshot resource: no content until the first <see cref="Snapshot"/> is done. Throws if QtNative refuses.</summary>
        public QtResource CreateSnapshot() => Create(Protocol.ResourceKind.Snapshot);

        /// <summary>
        /// New audio tap (protocol.h "Audio tap") with a fixed format (float, channels, sampleRate): Ready at once,
        /// its id goes to <see cref="Elements.Video"/>.AudioTap. A new sample rate needs a new tap; release the old one
        /// with <see cref="Release"/> after the Video switched to the new id. Throws if QtNative refuses.
        /// </summary>
        public QtResource CreateAudioTap(int channels, int sampleRate)
        {
            var resource = Create(Protocol.ResourceKind.AudioTap);
            var rc = _api.AudioTapInit(resource.Id, channels, sampleRate);
            if (rc != Native.Result.Ok)
            {
                Release(resource);
                throw new InvalidOperationException($"vlqt_audio_tap_init failed ({rc}, {channels} channels, {sampleRate} Hz)");
            }
            return resource;
        }

        private QtResource Create(int kind)
        {
            var rc = _api.Create(kind, out var id);
            if (rc != Native.Result.Ok)
                throw new InvalidOperationException($"vlqt_res_create failed ({rc})");
            var resource = new QtResource(id);
            _live.Add(id, resource);
            return resource;
        }

        /// <summary>
        /// Every refused start ends here: fails the resource and settles it, so <see cref="QtResource.Settled"/> fires and
        /// the version changes whatever the reason. Returns false.
        /// </summary>
        private bool Refuse(QtResource resource, string error)
        {
            resource.Fail(error);
            Settle(resource);
            return false;
        }

        /// <summary><see cref="System.IO.Path.GetFullPath(string)"/>; refuses the resource if the path is invalid.</summary>
        private bool TryGetFullPath(QtResource resource, string path, out string fullPath)
        {
            try
            {
                fullPath = System.IO.Path.GetFullPath(path);
                return true;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                fullPath = path;
                return Refuse(resource, $"Invalid path '{path}': {e.Message}");
            }
        }

        /// <summary>
        /// Starts loading the .qsb <paramref name="path"/> of <paramref name="stage"/> (<see cref="Protocol.ShaderStage"/>) into
        /// <paramref name="resource"/> (asynchronous, errors arrive as status records). <c>builtin:&lt;name&gt;</c> (a shader shipped
        /// with VL.Qt, see <see cref="ShaderSlot.TryGetBuiltinShader"/>) is passed as <c>builtin:&lt;name&gt;</c>, any other path is made absolute here. Returns false and fails the resource if
        /// the path is invalid or QtNative refuses.
        /// </summary>
        public bool LoadShader(QtResource resource, string path, int stage)
        {
            if (resource.IsReleased)
                return false;
            var fullPath = path;
            if (ShaderSlot.TryGetBuiltinShader(path, out var builtin))
                fullPath = builtin;
            else if (!TryGetFullPath(resource, path, out fullPath))
                return false;
            resource.OnLoadStarted();
            var rc = _api.LoadShader(resource.Id, fullPath, stage);
            return rc == Native.Result.Ok || Refuse(resource, $"Loading shader '{fullPath}' was refused by QtNative ({rc})");
        }

        /// <summary>
        /// Starts baking full Qt GLSL <paramref name="source"/> (stage <see cref="Protocol.ShaderStage"/>) into
        /// <paramref name="resource"/> (asynchronous, bake errors arrive as status records). Returns false and fails
        /// the resource if QtNative refuses.
        /// </summary>
        public bool LoadShaderSource(QtResource resource, string source, int stage)
        {
            if (resource.IsReleased)
                return false;
            resource.OnLoadStarted();
            var rc = _api.LoadShaderSource(resource.Id, source, stage);
            return rc == Native.Result.Ok || Refuse(resource, $"Baking the shader source was refused by QtNative ({rc})");
        }

        /// <summary>
        /// Starts grabbing element <paramref name="elementId"/> of <paramref name="window"/> into <paramref name="resource"/>
        /// (asynchronous, <see cref="QtResource.Settled"/> fires in a later pump). Returns false and fails the resource
        /// if QtNative refuses.
        /// </summary>
        public bool Snapshot(QtResource resource, SceneHandle window, uint elementId, float scale)
        {
            if (resource.IsReleased)
                return false;
            resource.OnLoadStarted();
            var rc = _api.Snapshot(resource.Id, window, elementId, scale);
            return rc == Native.Result.Ok || Refuse(resource, $"The snapshot of element {elementId} was refused by QtNative ({rc})");
        }

        /// <summary>
        /// Like <see cref="Snapshot"/>, but QtNative picks the scale so that the longest edge is at most
        /// <paramref name="maxEdgePx"/> pixels and never above the DIP size (tooltip previews).
        /// </summary>
        public bool SnapshotFit(QtResource resource, SceneHandle window, uint elementId, int maxEdgePx)
        {
            if (resource.IsReleased)
                return false;
            resource.OnLoadStarted();
            var rc = _api.SnapshotFit(resource.Id, window, elementId, maxEdgePx);
            return rc == Native.Result.Ok || Refuse(resource, $"The snapshot of element {elementId} was refused by QtNative ({rc})");
        }

        /// <summary>
        /// Starts loading <paramref name="path"/> (made absolute here) into <paramref name="resource"/>, uploaded the way
        /// <paramref name="upload"/> says. Returns false and fails the resource if the path is invalid or QtNative refuses.
        /// </summary>
        public bool Load(QtResource resource, string path, bool mipmaps, ImageUpload upload = ImageUpload.Auto)
        {
            if (resource.IsReleased || !TryGetFullPath(resource, path, out var fullPath))
                return false;
            resource.OnLoadStarted();
            var flags = (mipmaps ? Protocol.ImageLoadFlags.Mipmaps : 0) | (Previews ? Protocol.ImageLoadFlags.Preview : 0);
            var rc = _api.LoadImage(resource.Id, fullPath, flags, (int)upload);
            return rc == Native.Result.Ok || Refuse(resource, $"Loading '{fullPath}' was refused by QtNative ({rc})");
        }

        /// <summary>
        /// Replaces the content of <paramref name="resource"/> with pixels (copied synchronously by QtNative), uploaded the
        /// way <paramref name="upload"/> says. Returns false and fails the resource if the format is not supported or
        /// QtNative refuses.
        /// </summary>
        public bool SetPixels(QtResource resource, int width, int height, int rowBytes, PixelFormat format, bool premultiplied, ReadOnlySpan<byte> data,
            ImageUpload upload = ImageUpload.Auto)
        {
            if (resource.IsReleased)
                return false;
            var qtNativeFormat = PixelFormats.ToQtNative(format);
            if (qtNativeFormat == 0)
                return Refuse(resource, $"Pixel format {format} is not supported");
            resource.OnLoadStarted();
            var desc = new Native.vlqt_pixel_desc
            {
                width = width,
                height = height,
                row_bytes = rowBytes,
                format = qtNativeFormat,
                premultiplied = premultiplied ? 1 : 0,
                flags = Previews ? (int)Protocol.ImageLoadFlags.Preview : 0,
            };
            var rc = _api.SetPixels(resource.Id, in desc, data, (int)upload);
            return rc == Native.Result.Ok || Refuse(resource, $"{width} x {height} {format} pixels ({data.Length} bytes, {rowBytes} per row) were refused by QtNative ({rc})");
        }

        /// <summary>
        /// Blocks until <paramref name="resource"/> is Ready or Failed (decode on this thread if no worker took it yet,
        /// upload at once), then applies the status queue so size and state are current.
        /// </summary>
        public void Wait(QtResource resource, int timeoutMs = waitTimeoutMs)
        {
            if (resource.IsReleased || resource.State == QtImageState.Failed)
                return;
            var state = _api.Wait(resource.Id, timeoutMs);
            Poll();
            if (state > 0 && state != Protocol.ResourceState.Ready && state != Protocol.ResourceState.Failed)
                Logger?.LogWarning("Image resource {Id:X} still loading after {Timeout} ms, it is shown when ready", resource.Id, timeoutMs);
        }

        /// <summary>
        /// Copies the pixels of the last finished snapshot into a new <see cref="IImage"/> (BGRA8 premultiplied, own memory,
        /// not volatile). Null if there is none yet or the read failed (logged).
        /// </summary>
        public IImage? ReadSnapshot(QtResource resource)
        {
            if (resource.IsReleased)
                return null;
            // The size of the last INFO record; a snapshot replaced since then is read with a second call.
            var estimate = Math.Max(0, resource.Width) * Math.Max(0, resource.Height) * 4;
            return ReadPixels(resource.Id, estimate, _api.ReadSnapshot, "vlqt_snapshot_read");
        }

        /// <summary>
        /// Copies the tooltip preview of a Ready image (made by the worker for <see cref="Protocol.ImageLoadFlags.Preview"/>, at most
        /// 256 px) into a new <see cref="IImage"/> (BGRA8 premultiplied, own memory). Null if there is none.
        /// </summary>
        public IImage? ReadPreview(QtResource resource)
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
            return ReadPixels(resource.Id, width * height * 4, _api.ReadPreview, "vlqt_image_read_preview");
        }

        private delegate Native.Result ReadFn(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required);

        /// <summary>
        /// The pixels <paramref name="read"/> returns as an own BGRA8 premultiplied image, rows packed (width * 4). Null if
        /// there are none or the call failed (logged; no pixels yet and a stale id are normal).
        /// </summary>
        private ByteArrayImage? ReadPixels(ulong id, int estimatedBytes, ReadFn read, string call)
        {
            var bytes = new byte[estimatedBytes];
            var rc = read(id, bytes, out var desc, out var written, out var required);
            if (rc == Native.Result.BufferTooSmall)
            {
                // The estimate was too small (rounding, or a replacement of another size).
                bytes = new byte[required];
                rc = read(id, bytes, out desc, out written, out _);
            }
            if (rc != Native.Result.Ok || written <= 0)
            {
                if (rc is not (Native.Result.NoData or Native.Result.UnknownId))
                    Logger?.LogWarning("{Call} of resource {Id:X} returned {Rc}: {Error}", call, id, rc, Native.LastError());
                return null;
            }
            var info = new ImageInfo(desc.width, desc.height, PixelFormat.B8G8R8A8, isPremultipliedAlpha: true);
            return new ByteArrayImage(info, desc.width * 4, bytes);
        }

        /// <summary>
        /// Queues the release of <paramref name="resource"/> (idempotent, any thread). vlqt_res_release runs in the next
        /// pump, or at once when no pump runs and this is the main thread.
        /// </summary>
        public void Release(QtResource? resource)
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
                    _flushing = new QtResource[Math.Max(count, _flushing.Length * 2)];
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

        /// <summary>
        /// Applies all queued status records (main thread), then settles the resources that finished. The records are parsed
        /// in place in <see cref="_buffer"/>; user code (<see cref="QtResource.Settled"/>, the On Loaded observable) runs only
        /// after the last record, so it may poll again (a synchronous load waits) without overwriting the batch.
        /// </summary>
        internal void Poll()
        {
            ApplyRecords();
            // A nested call (from a Settled handler) only applies records: the outermost call settles what it collected.
            if (_settling)
                return;
            _settling = true;
            try
            {
                for (int i = 0; i < _toSettle.Count; i++)
                {
                    var resource = _toSettle[i];
                    if (!resource.IsReleased)
                        Settle(resource);
                }
            }
            finally
            {
                _toSettle.Clear();
                _settling = false;
            }
        }

        private void ApplyRecords()
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
            // Record: u16 event, u16 flags (0), u32 len, u64 id, payload; len = 8 + payload bytes (see protocol.h).
            const int idSize = 8;
            while (span.Length >= Protocol.HeaderSize + idSize)
            {
                var ev = BinaryPrimitives.ReadUInt16LittleEndian(span);
                var len = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                if (len < idSize || len > span.Length - Protocol.HeaderSize)
                {
                    Logger?.LogError("Malformed resource status record (event {Event}, len {Len}, {Left} bytes left)", ev, len, span.Length);
                    return;
                }
                var size = (Protocol.HeaderSize + (int)len + 3) & ~3;
                var id = BinaryPrimitives.ReadUInt64LittleEndian(span[Protocol.HeaderSize..]);
                var payload = span.Slice(Protocol.HeaderSize + idSize, (int)len - idSize);
                if (_live.TryGetValue(id, out var resource))
                    Dispatch(resource, ev, payload);
                span = span[Math.Min(size, span.Length)..];
            }
        }

        private void Dispatch(QtResource resource, ushort ev, ReadOnlySpan<byte> payload)
        {
            switch (ev)
            {
                case Protocol.ResourceEventId.State when payload.Length >= 1:
                    resource.OnState(payload[0]);
                    if (resource.State != QtImageState.Loading)
                        _toSettle.Add(resource);
                    break;
                case Protocol.ResourceEventId.Error:
                    resource.OnError(Encoding.UTF8.GetString(payload));
                    break;
                case Protocol.ResourceEventId.Info when payload.Length >= 8:
                    var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                    var replaced = resource.State == QtImageState.Ready;
                    resource.OnInfo(width, height);
                    // A replacement keeps the resource Ready: no STATE record, the INFO record marks its end.
                    if (replaced)
                        _toSettle.Add(resource);
                    break;
                case Protocol.ResourceEventId.Warning:
                    resource.OnWarning(Encoding.UTF8.GetString(payload));
                    break;
            }
        }

        private void Settle(QtResource resource)
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
