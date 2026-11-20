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

        private readonly SnapshotGrab _grab;
        private readonly ImageNodeReport _report;
        private IImage? _image;
        private string _error = string.Empty;

        public SnapshotNode(NodeContext nodeContext)
        {
            _grab = new SnapshotGrab(ResourceClient.ForApp(nodeContext.AppHost), SceneRegistry.ForApp(nodeContext.AppHost));
            _report = new ImageNodeReport(new NodeErrors(nodeContext));
        }

        /// <summary>For tests: a client on a fake API and an own scene registry, nothing is reported.</summary>
        internal SnapshotNode(ResourceClient client, SceneRegistry scene)
        {
            _grab = new SnapshotGrab(client, scene);
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
                // Every attempt starts clean: an error of an earlier attempt (e.g. not shown yet at the start) does not stay.
                _error = _grab.Start(element, scale);
            isReady = false;
            if (_grab.TakeSettled())
            {
                if (_grab.Read() is { } image)
                {
                    _image = image;
                    isReady = true;
                }
            }
            error = _error.Length > 0 ? _error : _grab.Resource is { State: QtImageState.Failed } failed ? failed.Error : string.Empty;
            _report.Update(_grab.Resource, error);
            return _image;
        }

        public void Dispose()
        {
            _grab.Dispose();
            _image = null;
            _report.Dispose();
        }
    }

    /// <summary>
    /// The grab of Snapshot and FileWriter (Element): locates the element in its renderer, starts the asynchronous snapshot
    /// into one resource per node and says when it settled. Main thread.
    /// </summary>
    internal sealed class SnapshotGrab : IDisposable
    {
        private readonly ResourceClient _client;
        private readonly SceneRegistry _scene;
        private bool _settled;

        public SnapshotGrab(ResourceClient client, SceneRegistry scene)
        {
            _client = client;
            _scene = scene;
        }

        /// <summary>The snapshot resource, null until the first <see cref="Start"/> got that far.</summary>
        public ImageResource? Resource { get; private set; }

        /// <summary>Starts a snapshot of <paramref name="element"/>; returns the error at once (not shown, refused by QtNative), empty on success.</summary>
        public string Start(Element? element, float scale)
        {
            if (element is null || !_scene.TryLocate(element, out var window, out var elementId))
                return SnapshotNode.NotShownError;
            try
            {
                if (Resource is null)
                {
                    Resource = _client.CreateSnapshot();
                    Resource.Settled = _ => _settled = true;
                }
                _client.Snapshot(Resource, window, elementId, scale);
                return string.Empty;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return e.Message;
            }
        }

        /// <summary>True once after a snapshot settled (finished or failed).</summary>
        public bool TakeSettled()
        {
            var settled = _settled;
            _settled = false;
            return settled;
        }

        /// <summary>The finished snapshot as an own copy; null if the resource is not Ready or has none.</summary>
        public IImage? Read() => Resource is { State: QtImageState.Ready } ? _client.ReadSnapshot(Resource) : null;

        public void Dispose()
        {
            _client.Release(Resource);
            Resource = null;
        }
    }
}
