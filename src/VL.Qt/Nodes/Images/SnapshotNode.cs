using System;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Basics.Imaging;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Reconciliation;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Grabs an element (or a Group with its children) as an image of vvvv on Grab, e.g. for thumbnails, export or
    /// processing in Skia/OpenCV. Asynchronous: Is Ready typically comes one frame after Grab (never in the same frame). Not a frame stream.
    /// The element has to be shown in a renderer.
    /// </summary>
    [ProcessNode(Name = "Snapshot", Category = Categories.Images)]
    public sealed class SnapshotNode : IDisposable
    {
        private readonly SnapshotGrab _grab;
        private readonly ResourceNodeReport _report;
        private IImage? _image;
        private string _error = string.Empty;

        public SnapshotNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), OccurrenceRegistry.ForApp(nodeContext.AppHost), new NodeErrors(nodeContext))
        {
        }

        /// <summary>For tests: a client on a fake API and an own scene registry; null <paramref name="messages"/> reports nothing.</summary>
        internal SnapshotNode(ResourceClient client, OccurrenceRegistry scene, NodeErrors? messages = null)
        {
            _grab = new SnapshotGrab(client, scene);
            _report = new ResourceNodeReport(messages, lasting: false);
        }

        /// <summary>Starts a snapshot of Element on Grab; Image keeps the last finished one.</summary>
        /// <param name="input">Element or Group to grab; it has to be shown in a renderer.</param>
        /// <param name="scale">Pixels per DIP: pixel size = element size in DIP * Scale, independent of the screen's DPI scaling
        /// (deterministic for export).</param>
        /// <param name="grab">Starts a new snapshot (the previous image stays until it is done).</param>
        /// <param name="isReady">True in the frame a new image arrived, typically one frame after Grab. A failed snapshot sets no Is Ready
        /// and is logged.</param>
        /// <returns>The last finished snapshot as BGRA8 premultiplied (own copy, not volatile); null before the first one.</returns>
        [return: Pin(Name = "Image")]
        public IImage? Update(IElement? input, [DefaultValue(1f)] float scale, bool grab, out bool isReady)
        {
            if (grab)
                // Every attempt starts clean: an error of an earlier attempt (e.g. not shown yet at the start) does not stay.
                _error = _grab.Start(input, scale);
            isReady = false;
            if (_grab.TakeSettled())
            {
                if (_grab.Read() is { } image)
                {
                    _image = image;
                    isReady = true;
                }
            }
            var error = _error.Length > 0 ? _error : _grab.Resource is { State: QtImageState.Failed } failed ? failed.Error : string.Empty;
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
}
