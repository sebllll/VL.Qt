using System;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Lib.Basics.Imaging;
using VL.Qt.Images;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Uploads an image of vvvv (IImage, e.g. from Skia, OpenCV or a camera) into a QtImage for the Image element.
    /// Uploads only when a new image arrives (or every frame for volatile images); the pixels are copied at once, the
    /// GPU upload follows within the frame budget, the previous content stays visible until it is complete.
    /// </summary>
    [ProcessNode(Name = "ToQtImage", Category = Categories.Images)]
    public sealed class ToQtImageNode : IDisposable
    {
        private readonly ResourceClient _client;
        private readonly ResourceNodeReport _report;
        private IImage? _last;
        private ImageUpload _upload;
        private QtResource? _resource;
        private QtImage? _image;
        private string _error = string.Empty;

        public ToQtImageNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake API; null <paramref name="messages"/> reports nothing.</summary>
        internal ToQtImageNode(ResourceClient client, NodeErrors? messages = null)
        {
            _client = client;
            _report = new ResourceNodeReport(messages, lasting: true);
        }

        /// <summary>Uploads Input when it is a new instance or volatile, or when Upload changes.</summary>
        /// <param name="input">8-bit RGBA/BGRA/RGBX/BGRX directly; RGB, grey, 16 bit and float are converted to 8 bit RGBA (with a warning
        /// for 16 bit and float). Nothing = no image.</param>
        /// <param name="upload">How the pixels get onto the GPU: Auto (files in a worker, pixels on the main thread), Main Thread (in
        /// stripes within the frame budget) or Worker (texture made in a worker, D3D11). For measurements; Auto fits nearly always.</param>
        /// <returns>The image: the same QtImage for all inputs (one GPU resource per node, its content is replaced), nothing without an
        /// Input. A failed upload is shown at the node and logged.</returns>
        [return: Pin(Name = "Image")]
        public QtImage? Update(IImage? input,
            [Pin(Visibility = PinVisibility.Optional)] ImageUpload upload)
        {
            if (input is null)
            {
                _last = null;
                _image = null;
            }
            else if (!ReferenceEquals(input, _last) || input.IsVolatile || upload != _upload)
            {
                _last = input;
                _upload = upload;
                Upload(input);
                // One QtImage for the resource, like the video variant: an upstream that delivers a new IImage every
                // frame (a renderer, a camera) must not allocate one per frame; the Image element draws new content.
                if (_resource is not null)
                    _image ??= new QtImage(_resource);
            }
            var error = _error.Length > 0 ? _error : _image is null ? string.Empty : _resource?.Error ?? string.Empty;
            _report.Update(_image is null ? null : _resource, error);
            return _image;
        }

        private void Upload(IImage input)
        {
            try
            {
                _resource ??= _client.CreateImage();
                var info = input.Info;
                using var data = input.GetData();
                _client.SetPixels(_resource, info.Width, info.Height, data.ScanSize, info.Format, info.IsPremultipliedAlpha, data.Bytes.Span, _upload);
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
