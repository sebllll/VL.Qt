using System;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Basics.Imaging;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Grabs an element (or a Group with its children) as an image of vvvv on Grab, e.g. for thumbnails, export or
    /// processing in Skia/OpenCV. Asynchronous: Is Ready typically comes one frame after Grab (never in the same frame). Not a frame stream.
    /// The element has to be shown in a renderer.
    /// </summary>
    [ProcessNode(Name = "Snapshot", Category = Categories.Qt)]
    public sealed class SnapshotNode : IDisposable
    {
        /// <summary>Error at the node when Grab is hit for an element that no renderer shows.</summary>
        internal const string NotShownError = "Element is not shown in a renderer";

        readonly ResourceClient _client;
        readonly SceneRegistry _scene;
        readonly ImageNodeReport _report;
        ImageResource? _resource;
        IImage? _image;
        bool _settled;
        string _error = string.Empty;

        public SnapshotNode(NodeContext nodeContext)
        {
            _client = ResourceClient.ForApp(nodeContext.AppHost);
            _scene = SceneRegistry.ForApp(nodeContext.AppHost);
            _report = new ImageNodeReport(new NodeErrors(nodeContext));
        }

        /// <summary>For tests: a client on a fake API and an own scene registry, nothing is reported.</summary>
        internal SnapshotNode(ResourceClient client, SceneRegistry scene)
        {
            _client = client;
            _scene = scene;
            _report = new ImageNodeReport(null);
        }

        /// <summary>Starts a snapshot of Element on Grab; Image keeps the last finished one.</summary>
        /// <param name="element">Element or Group to grab; it has to be shown in a renderer.</param>
        /// <param name="scale">Pixels per DIP: pixel size = element size in DIP * Scale, independent of the screen's DPI scaling (deterministic for export).</param>
        /// <param name="grab">Starts a new snapshot (the previous image stays until it is done).</param>
        /// <param name="isReady">True in the frame a new image arrived, typically one frame after Grab.</param>
        /// <param name="error">Why the last snapshot failed, empty otherwise.</param>
        /// <returns>The last finished snapshot as BGRA8 premultiplied (own copy, not volatile); null before the first one.</returns>
        [return: Pin(Name = "Image")]
        public IImage? Update(Element? element, out bool isReady, out string error, float scale = 1, bool grab = false)
        {
            if (grab)
                Grab(element, scale);
            isReady = false;
            if (_settled)
            {
                _settled = false;
                if (_resource is { State: QtImageState.Ready } && _client.ReadSnapshot(_resource) is { } image)
                {
                    _image = image;
                    isReady = true;
                }
            }
            error = _error.Length > 0 ? _error : _resource is { State: QtImageState.Failed } ? _resource.Error : string.Empty;
            _report.Update(_resource, error);
            return _image;
        }

        void Grab(Element? element, float scale)
        {
            // Every attempt starts clean: an error of an earlier attempt (e.g. not shown yet at the start) does not stay.
            _error = string.Empty;
            if (element is null || !_scene.TryLocate(element, out var window, out var elementId))
            {
                _error = NotShownError;
                return;
            }
            try
            {
                if (_resource is null)
                {
                    _resource = _client.CreateSnapshot();
                    _resource.Settled = _ => _settled = true;
                }
                _client.Snapshot(_resource, window, elementId, scale);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _error = e.Message;
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
