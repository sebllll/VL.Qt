using System;
using VL.Core;
using VL.Core.Import;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Loads an image file (PNG, JPG, ...) into a QtImage for the Image element. Blocks the frame – use at startup or in
    /// Create. Use LoadImage (Async) while running.
    /// </summary>
    [ProcessNode(Name = "LoadImage", Category = Categories.Qt)]
    public sealed class LoadImageNode : IDisposable
    {
        readonly ResourceClient _client;
        readonly ImageNodeReport _report;
        string? _path;
        bool _mipmaps;
        ImageResource? _resource;
        QtImage? _image;
        string _createError = string.Empty;

        public LoadImageNode(NodeContext nodeContext)
        {
            _client = ResourceClient.ForApp(nodeContext.AppHost);
            _report = new ImageNodeReport(new NodeErrors(nodeContext));
        }

        /// <summary>For tests: a client on a fake API, nothing is reported.</summary>
        internal LoadImageNode(ResourceClient client)
        {
            _client = client;
            _report = new ImageNodeReport(null);
        }

        /// <summary>Loads the file when Path or Mipmaps change and waits until it is on the GPU.</summary>
        /// <param name="path">Image file (PNG, JPG, BMP, ... everything Qt reads; EXIF rotation is applied). Empty = no image.</param>
        /// <param name="mipmaps">Generate mipmaps (+33 % GPU memory): smoother when the image is drawn much smaller than its size.</param>
        /// <param name="error">Why loading failed, empty otherwise.</param>
        /// <returns>The image, also when loading failed (State Failed, draws nothing). Nothing for an empty path.</returns>
        [return: Pin(Name = "Image")]
        public QtImage? Update(Path path, bool mipmaps, out string error)
        {
            var p = path?.Value ?? string.Empty;
            if (p != _path || mipmaps != _mipmaps)
                Load(p, mipmaps);
            error = _resource?.Error ?? _createError;
            _report.Update(_resource, error);
            return _image;
        }

        void Load(string path, bool mipmaps)
        {
            _path = path;
            _mipmaps = mipmaps;
            _client.Release(_resource);
            _resource = null;
            _image = null;
            _createError = string.Empty;
            if (path.Length == 0)
                return;
            try
            {
                var resource = _client.CreateImage();
                _resource = resource;
                _image = new QtImage(resource);
                if (_client.Load(resource, path, mipmaps))
                    _client.Wait(resource);
            }
            catch (InvalidOperationException e)
            {
                _createError = e.Message;
            }
        }

        public void Dispose()
        {
            _client.Release(_resource);
            _resource = null;
            _image = null;
            _report.Dispose();
        }
    }
}
