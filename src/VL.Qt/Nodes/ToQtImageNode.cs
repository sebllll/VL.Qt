using System;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Basics.Imaging;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Uploads an image of vvvv (IImage, e.g. from Skia, OpenCV or a camera) into a QtImage for the Image element.
    /// Uploads only when a new image arrives (or every frame for volatile images); the pixels are copied at once, the
    /// GPU upload follows within the frame budget, the previous content stays visible until it is complete.
    /// </summary>
    [ProcessNode(Name = "ToQtImage", Category = Categories.Qt)]
    public sealed class ToQtImageNode : IDisposable
    {
        readonly ResourceClient _client;
        readonly ImageNodeReport _report;
        IImage? _last;
        ImageResource? _resource;
        QtImage? _image;
        string _error = string.Empty;

        public ToQtImageNode(NodeContext nodeContext)
        {
            _client = ResourceClient.ForApp(nodeContext.AppHost);
            _report = new ImageNodeReport(new NodeErrors(nodeContext));
        }

        /// <summary>For tests: a client on a fake API, nothing is reported.</summary>
        internal ToQtImageNode(ResourceClient client)
        {
            _client = client;
            _report = new ImageNodeReport(null);
        }

        /// <summary>Uploads Input when it is a new instance or volatile.</summary>
        /// <param name="input">8-bit RGBA/BGRA/RGBX/BGRX directly; RGB, grey, 16 bit and float are converted to 8 bit RGBA (with a warning for 16 bit and float). Nothing = no image.</param>
        /// <param name="error">Why the upload failed, empty otherwise.</param>
        /// <returns>The image: a new QtImage for each new Input instance (one GPU resource per node, its content is replaced).</returns>
        [return: Pin(Name = "Image")]
        public QtImage? Update(IImage? input, out string error)
        {
            if (input is null)
            {
                _last = null;
                _image = null;
            }
            else if (!ReferenceEquals(input, _last) || input.IsVolatile)
            {
                var isNew = !ReferenceEquals(input, _last);
                _last = input;
                Upload(input);
                if (_resource is not null && (isNew || _image is null))
                    _image = new QtImage(_resource);
            }
            error = _error.Length > 0 ? _error : _image is null ? string.Empty : _resource?.Error ?? string.Empty;
            _report.Update(_image is null ? null : _resource, error);
            return _image;
        }

        void Upload(IImage input)
        {
            try
            {
                _resource ??= _client.CreateImage();
                var info = input.Info;
                using var data = input.GetData();
                _client.SetPixels(_resource, info.Width, info.Height, data.ScanSize, info.Format, info.IsPremultipliedAlpha, data.Bytes.Span);
                _error = string.Empty;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // QtNative refusing the pixels fails the resource (its Error); this is for create and the IImage itself.
                _error = e.Message;
            }
        }

        public void Dispose()
        {
            _client.Release(_resource);
            _resource = null;
            _image = null;
            _last = null;
            _report.Dispose();
        }
    }
}
