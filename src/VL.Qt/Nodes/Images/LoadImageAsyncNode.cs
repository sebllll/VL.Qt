using System;
using System.ComponentModel;
using System.Reactive.Subjects;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Images;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Loads an image file into a QtImage without blocking the frame: decoding runs in a worker, the upload is spread over
    /// frames. With Keep Previous on, the previous image stays at the output until the new one is ready.
    /// </summary>
    [ProcessNode(Name = "LoadImage (Async)", Category = Categories.Images)]
    public sealed class LoadImageAsyncNode : IDisposable
    {
        private readonly ResourceClient _client;
        private readonly ResourceNodeReport _report;
        private readonly Subject<QtImage> _onLoaded = new();
        private readonly Action<ImageResource> _onSettled;
        private string? _path;
        private bool _mipmaps;
        private ImageUpload _upload;
        // Current = at the output; pending = loading while the current one stays (Keep Previous).
        private ImageResource? _current, _pending;
        private QtImage? _currentImage, _pendingImage;
        private string _error = string.Empty;

        public LoadImageAsyncNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake API; null <paramref name="messages"/> reports nothing.</summary>
        internal LoadImageAsyncNode(ResourceClient client, NodeErrors? messages = null)
        {
            _client = client;
            _report = new ResourceNodeReport(messages, lasting: true);
            _onSettled = OnSettled;
        }

        /// <summary>Starts loading when Path, Mipmaps or Upload change; the output switches when the new image is ready.</summary>
        /// <param name="path">Image file (PNG, JPG, BMP, ... everything Qt reads; EXIF rotation is applied). Empty = no image.</param>
        /// <param name="mipmaps">Generate mipmaps (+33 % GPU memory): smoother when the image is drawn much smaller than its size.</param>
        /// <param name="keepPrevious">Keep the previous image at the output while the new one loads (and when it fails). Off: the output switches at once to the loading image, which draws nothing until it is ready.</param>
        /// <param name="isLoaded">True when the image of the current Path is ready. False also when it failed: the node then shows why and logs it.</param>
        /// <param name="onLoaded">Fires the image when a load is complete (on the main thread, during the frame's Qt pump).</param>
        /// <param name="upload">How the pixels get onto the GPU: Auto (files in a worker, pixels on the main thread), Main Thread (in stripes within the frame budget) or Worker (texture made in a worker, D3D11). For measurements; Auto fits nearly always.</param>
        /// <returns>The image; nothing until the first load is complete (Keep Previous) or for an empty path.</returns>
        [return: Pin(Name = "Image")]
        public QtImage? Update(
            Path path,
            bool mipmaps,
            [DefaultValue(true)] bool keepPrevious,
            [Pin(Visibility = PinVisibility.Optional)] ImageUpload upload,
            out bool isLoaded,
            [Pin(Visibility = PinVisibility.Optional)] out IObservable<QtImage> onLoaded)
        {
            var p = path?.Value ?? string.Empty;
            if (p != _path || mipmaps != _mipmaps || upload != _upload)
                Load(p, mipmaps, upload, keepPrevious);
            Promote();
            // _error: the load of the current Path failed (a kept previous image does not count).
            isLoaded = _error.Length == 0 && _pending is null && _current is { State: QtImageState.Ready };
            onLoaded = _onLoaded;
            _report.Update(_current, _error.Length > 0 ? _error : _current?.Error ?? string.Empty);
            return _currentImage;
        }

        private void Load(string path, bool mipmaps, ImageUpload upload, bool keepPrevious)
        {
            _path = path;
            _mipmaps = mipmaps;
            _upload = upload;
            _error = string.Empty;
            ReleasePending();
            if (path.Length == 0)
            {
                ReleaseCurrent();
                return;
            }
            // The switch is decided before the create: with Keep Previous off the old image goes even if QtNative refuses the new one.
            if (!keepPrevious)
                ReleaseCurrent();
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
                _current = resource;
                _currentImage = image;
            }
            _client.Load(resource, path, mipmaps, upload);
        }

        /// <summary>The pending image replaces the current one once ready; a failed one is dropped (the current one stays).</summary>
        private void Promote()
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
        private void OnSettled(ImageResource resource)
        {
            if (resource.State != QtImageState.Ready)
                return;
            if (resource == _pending && _pendingImage is not null)
                _onLoaded.OnNext(_pendingImage);
            else if (resource == _current && _currentImage is not null)
                _onLoaded.OnNext(_currentImage);
        }

        private void ReleasePending()
        {
            _client.Release(_pending);
            _pending = null;
            _pendingImage = null;
        }

        private void ReleaseCurrent()
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
