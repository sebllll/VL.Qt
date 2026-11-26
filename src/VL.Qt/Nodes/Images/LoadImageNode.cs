using System;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Images;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Loads an image file (PNG, JPG, ...) into a QtImage for the Image element. Blocks the frame – use at startup or in
    /// Create. Use LoadImage (Async) while running.
    /// </summary>
    [ProcessNode(Name = "LoadImage", Category = Categories.Images)]
    public sealed class LoadImageNode : IDisposable
    {
        private readonly ResourceClient _client;
        private readonly ResourceNodeReport _report;
        private string? _path;
        private bool _mipmaps;
        private ImageUpload _upload;
        private QtResource? _resource;
        private QtImage? _image;
        private string _createError = string.Empty;

        public LoadImageNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake API; null <paramref name="messages"/> reports nothing.</summary>
        internal LoadImageNode(ResourceClient client, NodeErrors? messages = null)
        {
            _client = client;
            _report = new ResourceNodeReport(messages, lasting: true);
        }

        /// <summary>Loads the file when Path, Mipmaps or Upload change and waits until it is on the GPU.</summary>
        /// <param name="path">Image file (PNG, JPG, BMP, ... everything Qt reads; EXIF rotation is applied). Empty = no image.</param>
        /// <param name="mipmaps">Generate mipmaps (+33 % GPU memory): smoother when the image is drawn much smaller than its size.</param>
        /// <param name="upload">How the pixels get onto the GPU: Auto (files in a worker, pixels on the main thread), Main Thread (in
        /// stripes within the frame budget) or Worker (texture made in a worker, D3D11). For measurements; Auto fits nearly always.</param>
        /// <returns>The image, also when loading failed (State Failed, draws nothing; the node shows why and logs it). Nothing for an empty path.</returns>
        [return: Pin(Name = "Image")]
        public QtImage? Update(Path path, bool mipmaps,
            [Pin(Visibility = PinVisibility.Optional)] ImageUpload upload)
        {
            var p = path?.Value ?? string.Empty;
            if (p != _path || mipmaps != _mipmaps || upload != _upload)
                Load(p, mipmaps, upload);
            _report.Update(_resource, _resource?.Error ?? _createError);
            return _image;
        }

        private void Load(string path, bool mipmaps, ImageUpload upload)
        {
            _path = path;
            _mipmaps = mipmaps;
            _upload = upload;
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
                if (_client.Load(resource, path, mipmaps, upload))
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
