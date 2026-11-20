using System;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Grabs an element (or a Group with its children) and writes it to a file on Write: PNG, JPG, BMP, ... by the
    /// extension of File Path. Snapshot and FileWriter in one node; the element has to be shown in a renderer.
    /// Asynchronous: Is Done comes at least one frame after Write.
    /// </summary>
    [ProcessNode(Name = "FileWriter (Element)", Category = Categories.Qt)]
    public sealed class FileWriterElementNode : IDisposable
    {
        private readonly SnapshotGrab _grab;
        private readonly ImageFileWriter _writer;
        private readonly ImageNodeReport _report;
        // Target of the grab in flight (the newest Write wins, like the snapshot itself).
        private string _path = string.Empty;
        private int _quality;
        private bool _grabbing;
        private string _error = string.Empty;

        public FileWriterElementNode(NodeContext nodeContext)
        {
            _grab = new SnapshotGrab(ResourceClient.ForApp(nodeContext.AppHost), SceneRegistry.ForApp(nodeContext.AppHost));
            _writer = ImageFileWriter.CreateNative();
            _report = new ImageNodeReport(new NodeErrors(nodeContext));
        }

        /// <summary>For tests: a client on a fake API, an own scene registry and a writer on a fake write function.</summary>
        internal FileWriterElementNode(ResourceClient client, SceneRegistry scene, ImageFileWriter writer)
        {
            _grab = new SnapshotGrab(client, scene);
            _writer = writer;
            _report = new ImageNodeReport(null);
        }

        /// <summary>Grabs Element on Write and writes it to File Path.</summary>
        /// <param name="element">Element or Group to write; it has to be shown in a renderer.</param>
        /// <param name="filePath">Target file; the extension picks the format (png, jpg, bmp, ...). Missing folders are created, an existing file is overwritten.</param>
        /// <param name="isDone">True in the frame the file was written (also when it failed, then Error says why).</param>
        /// <param name="error">Why the last grab or write failed, empty otherwise.</param>
        /// <param name="scale">Pixels per DIP: pixel size = element size in DIP * Scale, independent of the screen's DPI scaling.</param>
        /// <param name="quality">0..100 for lossy formats (JPG, WebP); ignored for PNG and BMP.</param>
        /// <param name="write">Grabs the element and writes it (a write while one runs waits, only the newest one).</param>
        public void Update(
            Element? element,
            Path filePath,
            out bool isDone,
            out string error,
            float scale = 1,
            [Pin(Visibility = PinVisibility.Optional)] int quality = 90,
            bool write = false)
        {
            isDone = false;
            // Polled before Write is handled: a write never reports in the frame it was started.
            if (_writer.Poll(out var writeError))
            {
                isDone = true;
                _error = writeError;
            }
            if (write)
            {
                // Every write starts clean: the error of the last one does not stay.
                _path = filePath?.Value ?? string.Empty;
                _quality = quality;
                _grabbing = false;
                _error = _path.Length == 0 ? FileWriterNode.NoPathError : _grab.Start(element, scale);
                if (_error.Length > 0)
                    // Nothing is written: Done and Error in the same frame, so a patch waiting for Is Done does not hang.
                    isDone = true;
                else
                    _grabbing = true;
            }
            if (_grab.TakeSettled() && _grabbing)
            {
                _grabbing = false;
                if (_grab.Read() is { } image)
                    _writer.Start(image, _path, _quality);
                else
                {
                    _error = _grab.Resource is { State: QtImageState.Failed } failed && failed.Error.Length > 0 ? failed.Error : "The snapshot of the element failed";
                    isDone = true;
                }
            }
            error = _error;
            _report.Update(null, error);
        }

        public void Dispose()
        {
            _writer.Dispose();
            _grab.Dispose();
            _report.Dispose();
        }
    }
}
