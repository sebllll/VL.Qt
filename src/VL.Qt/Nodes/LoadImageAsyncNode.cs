using System;
using System.ComponentModel;
using System.Reactive.Subjects;
using VL.Core;
using VL.Core.Import;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Loads an image file into a QtImage without blocking the frame: decoding runs in a worker, the upload is spread over
    /// frames. The previous image stays at the output until the new one is ready.
    /// </summary>
    [ProcessNode(Name = "LoadImage (Async)", Category = Categories.Qt)]
    public sealed class LoadImageAsyncNode : IDisposable
    {
        readonly ResourceClient _client;
        readonly ImageNodeReport _report;
        readonly Subject<QtImage> _onLoaded = new();
        readonly Action<ImageResource> _onSettled;
        string? _path;
        bool _mipmaps;
        // Current = at the output; pending = loading while the current one stays (Keep Previous).
        ImageResource? _current, _pending;
        QtImage? _currentImage, _pendingImage;
        string _error = string.Empty;

        public LoadImageAsyncNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake API, nothing is reported.</summary>
        internal LoadImageAsyncNode(ResourceClient client)
            : this(client, null)
        {
        }

        LoadImageAsyncNode(ResourceClient client, NodeErrors? errors)
        {
            _client = client;
            _report = new ImageNodeReport(errors);
            _onSettled = OnSettled;
        }

        /// <summary>Starts loading when Path or Mipmaps change; the output switches when the new image is ready.</summary>
        /// <param name="path">Image file (PNG, JPG, BMP, ... everything Qt reads; EXIF rotation is applied). Empty = no image.</param>
        /// <param name="mipmaps">Generate mipmaps (+33 % GPU memory): smoother when the image is drawn much smaller than its size.</param>
        /// <param name="keepPrevious">Keep the previous image at the output while the new one loads (and when it fails). Off: the output switches at once to the loading image, which draws nothing until it is ready.</param>
        /// <param name="isLoaded">True when the image of the current Path is ready.</param>
        /// <param name="error">Why loading the current Path failed, empty otherwise.</param>
        /// <param name="onLoaded">Fires the image when a load is complete (on the main thread, during the frame's Qt pump).</param>
        /// <returns>The image; nothing until the first load is complete (Keep Previous) or for an empty path.</returns>
        [return: Pin(Name = "Image")]
        public QtImage? Update(
            Path path,
            bool mipmaps,
            [DefaultValue(true)] bool keepPrevious,
            out bool isLoaded,
            out string error,
            [Pin(Visibility = Model.PinVisibility.Optional)] out IObservable<QtImage> onLoaded)
        {
            var p = path?.Value ?? string.Empty;
            if (p != _path || mipmaps != _mipmaps)
                Load(p, mipmaps, keepPrevious);
            Promote();
            // _error: the load of the current Path failed (a kept previous image does not count).
            isLoaded = _error.Length == 0 && _pending is null && _current is { State: QtImageState.Ready };
            error = _error.Length > 0 ? _error : _current?.Error ?? string.Empty;
            onLoaded = _onLoaded;
            _report.Update(_current, error);
            return _currentImage;
        }

        void Load(string path, bool mipmaps, bool keepPrevious)
        {
            _path = path;
            _mipmaps = mipmaps;
            _error = string.Empty;
            ReleasePending();
            if (path.Length == 0)
            {
                ReleaseCurrent();
                return;
            }
            ImageResource resource;
            try
            {
                resource = _client.CreateImage();
            }
            catch (InvalidOperationException e)
            {
                _error = e.Message;
                return;
            }
            resource.Settled = _onSettled;
            var image = new QtImage(resource);
            if (keepPrevious && _current is not null)
            {
                _pending = resource;
                _pendingImage = image;
            }
            else
            {
                ReleaseCurrent();
                _current = resource;
                _currentImage = image;
            }
            _client.Load(resource, path, mipmaps);
        }

        /// <summary>The pending image replaces the current one once ready; a failed one is dropped (the current one stays).</summary>
        void Promote()
        {
            if (_pending is null || _pending.State == QtImageState.Loading)
                return;
            if (_pending.State == QtImageState.Ready)
            {
                ReleaseCurrent();
                _current = _pending;
                _currentImage = _pendingImage;
                _pending = null;
                _pendingImage = null;
            }
            else
            {
                _error = _pending.Error;
                ReleasePending();
            }
        }

        // Main thread, in the pump or a synchronous failure of Load.
        void OnSettled(ImageResource resource)
        {
            if (resource.State != QtImageState.Ready)
                return;
            if (resource == _pending && _pendingImage is not null)
                _onLoaded.OnNext(_pendingImage);
            else if (resource == _current && _currentImage is not null)
                _onLoaded.OnNext(_currentImage);
        }

        void ReleasePending()
        {
            _client.Release(_pending);
            _pending = null;
            _pendingImage = null;
        }

        void ReleaseCurrent()
        {
            _client.Release(_current);
            _current = null;
            _currentImage = null;
        }

        public void Dispose()
        {
            ReleasePending();
            ReleaseCurrent();
            _onLoaded.OnCompleted();
            _onLoaded.Dispose();
            _report.Dispose();
        }
    }
}
