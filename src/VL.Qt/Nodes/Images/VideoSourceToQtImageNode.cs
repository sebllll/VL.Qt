using System;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Animation;
using VL.Lib.Basics.Video;
using VL.Qt.Images;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Shows a video source of vvvv (VideoPlayer, camera, ... of VL.Video) in the Image element: each new frame replaces
    /// the content of one QtImage. Frames are copied from CPU memory; the output keeps the same QtImage while only its
    /// content changes.
    /// </summary>
    [ProcessNode(Name = "VideoSourceToQtImage", Category = Categories.Images)]
    public sealed class VideoSourceToQtImageNode : IDisposable
    {
        private readonly ResourceClient _client;
        private readonly ImageNodeReport _report;
        private readonly VideoPlaybackContext _ctx;
        private IVideoSource? _source;
        private int _ticket;
        private bool _preferPush;
        private ImageUpload _upload;
        private VideoFrameStream? _stream;
        private ImageResource? _resource;
        private QtImage? _image;
        private bool _hasFrame;
        private string _error = string.Empty;

        public VideoSourceToQtImageNode(NodeContext nodeContext)
        {
            _client = ResourceClient.ForApp(nodeContext.AppHost);
            var errors = new NodeErrors(nodeContext);
            _report = new ImageNodeReport(errors);
            var clock = nodeContext.AppHost.Services.GetService(typeof(IFrameClock)) as IFrameClock
                ?? throw new InvalidOperationException("VL.Qt: the app host has no IFrameClock");
            // CPU frames only: no graphics device in the context, the players deliver frames in memory.
            // GPU zero-copy: pass the device of the Qt RHI here (preferGpu) and take texture-backed frames below.
            _ctx = new VideoPlaybackContext(clock, errors.Logger);
        }

        /// <summary>For tests: a client on a fake API and a playback context without a device, nothing is reported.</summary>
        internal VideoSourceToQtImageNode(ResourceClient client, VideoPlaybackContext ctx)
        {
            _client = client;
            _report = new ImageNodeReport(null);
            _ctx = ctx;
        }

        /// <summary>Uploads the latest frame of Input, if a new one arrived.</summary>
        /// <param name="input">The video source, e.g. VideoPlayer or VideoIn. Nothing = Fallback.</param>
        /// <param name="fallback">Returned until the first frame of the current source is uploaded.</param>
        /// <param name="preferPush">True: frames are grabbed on a thread of their own as they arrive. False: one frame is grabbed per vvvv frame on the main thread.</param>
        /// <param name="error">Why the source or the upload failed, empty otherwise.</param>
        /// <param name="upload">How the pixels get onto the GPU: Auto (files in a worker, pixels on the main thread), Main Thread (in stripes within the frame budget) or Worker (texture made in a worker, D3D11). For measurements; Auto fits nearly always.</param>
        /// <returns>The image: the same QtImage for all frames (one GPU resource per node, its content is replaced).</returns>
        [return: Pin(Name = "Output")]
        public QtImage? Update(IVideoSource? input, QtImage? fallback, out string error, bool preferPush = true,
            [Pin(Visibility = Model.PinVisibility.Optional)] ImageUpload upload = ImageUpload.Auto)
        {
            // A new mode applies from the next frame on.
            _upload = upload;
            var ticket = GetChangedTicket(input);
            if (!ReferenceEquals(input, _source) || ticket != _ticket || preferPush != _preferPush)
            {
                _source = input;
                _preferPush = preferPush;
                Restart();
            }

            if (_stream?.TryTake() is { } handle)
            {
                using (handle)
                    Upload(handle.Resource);
            }

            var output = _hasFrame ? _image : fallback;
            var streamError = _stream?.Error ?? string.Empty;
            error = streamError.Length > 0 ? streamError
                : _error.Length > 0 ? _error
                : _hasFrame ? _resource?.Error ?? string.Empty : string.Empty;
            _report.Update(_hasFrame ? _resource : null, error);
            return output;
        }

        private static int GetChangedTicket(IVideoSource? source) => source is IVideoSource2 source2 ? source2.ChangedTicket : 0;

        private void Restart()
        {
            // Stop the old stream and release its frames; the resource stays for the next source.
            _stream?.Dispose();
            _stream = null;
            _hasFrame = false;
            _error = string.Empty;
            // Fetch the ticket after the old stream was disposed (it might have incremented it).
            _ticket = GetChangedTicket(_source);
            if (_source is not null)
                _stream = new VideoFrameStream(_source, _ctx, _preferPush);
        }

        private void Upload(VideoFrame? frame)
        {
            if (frame is null)
                return;
            try
            {
                if (!frame.TryGetMemory(out var memory))
                {
                    // Texture-backed frames only come with a graphics device in the context; their zero-copy path is #368.
                    _error = $"The video frame ({frame.Width} x {frame.Height}) is not in CPU memory";
                    return;
                }
                _resource ??= _client.CreateImage();
                _image ??= new QtImage(_resource);
                _client.SetPixels(_resource, frame.Width, frame.Height, frame.RowLengthInBytes, frame.PixelFormat, false, memory.Span, _upload);
                _hasFrame = true;
                _error = string.Empty;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // QtNative refusing the pixels fails the resource (its Error); this is for create and the frame itself.
                _error = e.Message;
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
            _client.Release(_resource);
            _resource = null;
            _image = null;
            _source = null;
            _hasFrame = false;
            _report.Dispose();
        }
    }
}
