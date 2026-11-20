using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using VL.Lib.Basics.Resources;
using VL.Lib.Basics.Video;

namespace VL.Qt
{
    /// <summary>
    /// The frames of one <see cref="IVideoSource"/> for one consumer (#114), the pattern of VideoSourceToImage in
    /// VL.CoreLib against the VL.Core types only (VL.Qt does not reference VL.CoreLib):
    /// <list type="bullet">
    /// <item>Push: a dedicated thread starts the player (IVideoSource2) and grabs frames; it only puts the frame handle
    /// into a queue of capacity 1 (waits up to 100 ms for the consumer, then drops the frame).</item>
    /// <item>Pull: <see cref="TryTake"/> grabs on the calling (main) thread.</item>
    /// </list>
    /// The consumer takes a handle in its Update on the main thread and disposes it after the upload. Dispose stops the
    /// thread, disposes the player on it and releases the frames still queued.
    /// </summary>
    internal sealed class VideoFrameStream : IDisposable
    {
        private const int joinTimeoutMs = 2000;

        private readonly IVideoSource _source;
        private readonly VideoPlaybackContext _ctx;
        private readonly bool _push;
        private readonly BlockingCollection<IResourceHandle<VideoFrame>> _queue = new(boundedCapacity: 1);
        private readonly CancellationTokenSource _cancel = new();
        private readonly Thread? _thread;
        private IVideoPlayer? _player;
        private bool _started, _disposed;
        private volatile string _error = string.Empty;

        public VideoFrameStream(IVideoSource source, VideoPlaybackContext ctx, bool push)
        {
            _source = source;
            _ctx = ctx;
            _push = push;
            if (push)
            {
                // Render contexts of the players are thread affine: start, grab and dispose on one dedicated thread.
                _thread = new Thread(Run) { IsBackground = true, Name = "VL.Qt video frames" };
                _thread.Start();
            }
        }

        /// <summary>Last error of the source (start or grab), empty if none. Any thread.</summary>
        public string Error => _error;

        /// <summary>
        /// Main thread: the next frame, null if none arrived. The caller owns the handle and disposes it. Pull mode grabs
        /// here.
        /// </summary>
        public IResourceHandle<VideoFrame>? TryTake()
        {
            if (_disposed)
                return null;
            if (_push)
                return _queue.TryTake(out var handle) ? handle : null;

            if (!_started)
            {
                _started = true;
                if (_source is IVideoSource2 source2 && !TryStart(source2, out _player))
                    return null;
            }
            if (_source is IVideoSource2 && _player is null)
                return null;
            return TryGrab(out var frame) ? frame?.GetHandle() : null;
        }

        private void Run()
        {
            var token = _cancel.Token;
            IVideoPlayer? player = null;
            try
            {
                if (_source is IVideoSource2 source2)
                {
                    if (!TryStart(source2, out player) || player is null)
                        return;
                }
                _player = player;
                while (!token.IsCancellationRequested)
                {
                    if (!TryGrab(out var frame))
                    {
                        token.WaitHandle.WaitOne(100);
                        continue;
                    }
                    if (frame is null)
                    {
                        // To prevent the CPU going to 100 % (as VL.CoreLib: 16 ms with a player, 2 ms without).
                        token.WaitHandle.WaitOne(player is null ? 2 : 16);
                        continue;
                    }
                    var handle = frame.GetHandle();
                    if (!TryAdd(handle, token))
                        handle.Dispose();
                }
            }
            catch (Exception e)
            {
                SetError(e, "Video frame thread failed");
            }
            finally
            {
                try
                {
                    player?.Dispose();
                }
                catch (Exception e)
                {
                    _ctx.Logger.LogError(e, "Disposing the video player failed");
                }
            }
        }

        private bool TryAdd(IResourceHandle<VideoFrame> handle, CancellationToken token)
        {
            try
            {
                return _queue.TryAdd(handle, 100, token);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }

        private bool TryStart(IVideoSource2 source, out IVideoPlayer? player)
        {
            try
            {
                player = source.Start(_ctx);
                return true;
            }
            catch (Exception e)
            {
                SetError(e, "Starting the video playback failed");
                player = null;
                return false;
            }
        }

        private bool TryGrab(out IResourceProvider<VideoFrame>? frame)
        {
            try
            {
                frame = _player is not null ? _player.GrabVideoFrame() : _source.GrabVideoFrame();
                if (_error.Length > 0)
                    _error = string.Empty;
                return true;
            }
            catch (Exception e)
            {
                SetError(e, "Grabbing a video frame failed");
                frame = null;
                return false;
            }
        }

        private void SetError(Exception e, string what)
        {
            var text = $"{what}: {e.Message}";
            if (text == _error)
                return;
            _error = text;
            _ctx.Logger.LogError(e, "{What}: {Message}", what, e.Message);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            var joined = true;
            if (_thread is not null)
            {
                _cancel.Cancel();
                joined = _thread.Join(joinTimeoutMs);
                if (!joined)
                    _ctx.Logger.LogWarning("The video frame thread did not stop within {Timeout} ms, its frames are released when it ends", joinTimeoutMs);
            }
            else
            {
                try
                {
                    _player?.Dispose();
                }
                catch (Exception e)
                {
                    _ctx.Logger.LogError(e, "Disposing the video player failed");
                }
            }
            _player = null;

            // A thread still running cannot add any more (it disposes its frame itself).
            _queue.CompleteAdding();
            while (_queue.TryTake(out var handle))
                handle.Dispose();
            if (joined)
            {
                _queue.Dispose();
                _cancel.Dispose();
            }
        }
    }
}
