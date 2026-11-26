using System;
using System.Threading.Tasks;
using VL.Qt.Wire;

namespace VL.Qt.Images
{
    /// <summary>
    /// One image resource of QtNative as seen from C#: id, state and size from the status queue
    /// (<see cref="ResourceClient"/>). Owned by the node that created it; <see cref="QtImage"/>s are views on it.
    /// Main (Qt) thread only, except <see cref="ResourceClient.Release"/>.
    /// </summary>
    internal sealed class QtResource
    {
        private TaskCompletionSource<QtResource>? _settled;

        public QtResource(ulong id) => Id = id;

        public ulong Id { get; }
        public QtImageState State { get; private set; } = QtImageState.Loading;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Error { get; private set; } = string.Empty;

        /// <summary>Counts finished loads (Ready or Failed, also replacements): changes whenever the content may be new.</summary>
        public int Version { get; private set; }

        /// <summary>RESOURCE_EVENT_WARNING text (e.g. 16 bit reduced to 8 bit), empty = none. At most one per resource.</summary>
        public string Warning { get; private set; } = string.Empty;

        /// <summary>Release was requested (queued or done); no further native calls for this id.</summary>
        public bool IsReleased { get; private set; }

        /// <summary>
        /// Called on the main thread when a load ends (Ready or Failed, also after a replacement), from the pump or a
        /// synchronous wait. Exceptions are logged by the client.
        /// </summary>
        public Action<QtResource>? Settled { get; set; }

        /// <summary>Completes (on the main thread) at the next Ready/Failed; cancelled when released before.</summary>
        public Task<QtResource> WhenSettled()
        {
            if (State != QtImageState.Loading)
                return Task.FromResult(this);
            _settled ??= new TaskCompletionSource<QtResource>();
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
                Protocol.ResourceState.Ready => QtImageState.Ready,
                Protocol.ResourceState.Failed => QtImageState.Failed,
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
