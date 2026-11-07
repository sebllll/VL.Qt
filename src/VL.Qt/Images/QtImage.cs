using System;
using System.Threading.Tasks;

namespace VL.Qt
{
    /// <summary>Loading state of a <see cref="QtImage"/>.</summary>
    public enum QtImageState
    {
        /// <summary>Decoding or uploading; the Image element keeps showing its previous image meanwhile.</summary>
        Loading,
        /// <summary>On the GPU, drawn by every Image element that uses it.</summary>
        Ready,
        /// <summary>Could not be loaded (see Error); draws nothing.</summary>
        Failed,
    }

    /// <summary>
    /// GPU-backed image of VL.Qt, created by LoadImage, LoadImage (Async) or ToQtImage. Use it at the Image input of any
    /// number of Image elements in any window: it is uploaded once, switching between loaded images costs nothing.
    /// The node that created it owns it and frees it when it is deleted or loads another image; pass the image around
    /// freely, it needs no disposing.
    /// </summary>
    public sealed class QtImage
    {
        readonly ImageResource _resource;

        internal QtImage(ImageResource resource) => _resource = resource;

        internal ImageResource Resource => _resource;

        /// <summary>Resource id in QtNative (index | generation &lt;&lt; 32).</summary>
        internal ulong Id => _resource.Id;

        /// <summary>Width in pixels, 0 until loaded.</summary>
        public int Width => _resource.Width;

        /// <summary>Height in pixels, 0 until loaded.</summary>
        public int Height => _resource.Height;

        /// <summary>Loading, Ready or Failed.</summary>
        public QtImageState State => _resource.State;

        /// <summary>Why loading failed, empty otherwise.</summary>
        public string Error => _resource.Error;

        public override string ToString() => $"QtImage {Width} x {Height} ({State})";
    }

    /// <summary>
    /// One image resource of QtNative as seen from C# (#362): id, state and size from the status queue
    /// (<see cref="ResourceClient"/>). Owned by the node that created it; <see cref="QtImage"/>s are views on it.
    /// Main (Qt) thread only, except <see cref="ResourceClient.Release"/>.
    /// </summary>
    internal sealed class ImageResource
    {
        TaskCompletionSource<ImageResource>? _settled;

        public ImageResource(ulong id) => Id = id;

        public ulong Id { get; }
        public QtImageState State { get; private set; } = QtImageState.Loading;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Error { get; private set; } = string.Empty;

        /// <summary>Counts finished loads (Ready or Failed, also replacements): changes whenever the content may be new (#546).</summary>
        public int Version { get; private set; }

        /// <summary>RES_EV_WARNING text (e.g. 16 bit reduced to 8 bit), empty = none. At most one per resource.</summary>
        public string Warning { get; private set; } = string.Empty;

        /// <summary>Release was requested (queued or done); no further native calls for this id.</summary>
        public bool IsReleased { get; private set; }

        /// <summary>
        /// Called on the main thread when a load ends (Ready or Failed, also after a replacement), from the pump or a
        /// synchronous wait. Exceptions are logged by the client.
        /// </summary>
        public Action<ImageResource>? Settled { get; set; }

        /// <summary>Completes (on the main thread) at the next Ready/Failed; cancelled when released before.</summary>
        public Task<ImageResource> WhenSettled()
        {
            if (State != QtImageState.Loading)
                return Task.FromResult(this);
            _settled ??= new TaskCompletionSource<ImageResource>();
            return _settled.Task;
        }

        internal void OnLoadStarted()
        {
            // Replacement on a Ready resource keeps it Ready until the new content is uploaded (protocol.h).
            if (State == QtImageState.Failed)
            {
                State = QtImageState.Loading;
                Error = string.Empty;
            }
        }

        internal void OnState(byte state)
        {
            var next = state switch
            {
                Protocol.ResState.Ready => QtImageState.Ready,
                Protocol.ResState.Failed => QtImageState.Failed,
                _ => QtImageState.Loading,
            };
            State = next;
            if (next == QtImageState.Ready)
                Error = string.Empty;
        }

        internal void OnInfo(int width, int height)
        {
            Width = width;
            Height = height;
        }

        internal void OnError(string error) => Error = error;

        internal void OnWarning(string warning) => Warning = warning;

        /// <summary>Fails without a native load (bad argument, unsupported format).</summary>
        internal void Fail(string error)
        {
            Error = error;
            State = QtImageState.Failed;
        }

        /// <summary>Completes the awaiters of <see cref="WhenSettled"/>; the client calls <see cref="Settled"/> next.</summary>
        internal void CompleteSettled()
        {
            Version++;
            var tcs = _settled;
            _settled = null;
            tcs?.TrySetResult(this);
        }

        internal void MarkReleased() => IsReleased = true;

        internal void CancelSettled()
        {
            var tcs = _settled;
            _settled = null;
            tcs?.TrySetCanceled();
        }
    }
}
