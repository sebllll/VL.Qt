using System;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Images;
using VL.Qt.Reconciliation;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Grabs an element (or a Group with its children) and writes it to a file on Write: PNG, JPG, BMP, ... by the
    /// extension of File Path. Snapshot and FileWriter in one node; the element has to be shown in a renderer.
    /// Asynchronous: Is Done comes at least one frame after Write.
    /// </summary>
    [ProcessNode(Name = "FileWriter (Element)", Category = Categories.Images)]
    public sealed class FileWriterElementNode : IDisposable
    {
        private readonly SnapshotGrab _grab;
        private readonly ImageFileWriter _writer;
        private readonly ResourceNodeReport _report;
        // Target of the grab in flight (the newest Write wins, like the snapshot itself).
        private string _path = string.Empty;
        private int _quality;
        private bool _grabbing;
        private string _error = string.Empty;
        private bool _success;

        public FileWriterElementNode(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), OccurrenceRegistry.ForApp(nodeContext.AppHost), ImageFileWriter.CreateNative(),
                new NodeErrors(nodeContext))
        {
        }

        internal FileWriterElementNode(ResourceClient client, OccurrenceRegistry scene, ImageFileWriter writer, NodeErrors? messages = null)
        {
            _grab = new SnapshotGrab(client, scene);
            _writer = writer;
            _report = new ResourceNodeReport(messages, lasting: false);
        }

        /// <summary>Grabs Element on Write and writes it to File Path.</summary>
        /// <param name="input">Element or Group to write; it has to be shown in a renderer.</param>
        /// <param name="filePath">Target file; the extension picks the format (png, jpg, bmp, ...). Missing folders are created, an
        /// existing file is overwritten.</param>
        /// <param name="isDone">True in the frame the write ended, also when the grab or the write failed (see Success).</param>
        /// <param name="success">True if the last write that ended wrote the file; set in the frame of Is Done and held until the next one.
        /// A failure is logged.</param>
        /// <param name="scale">Pixels per DIP: pixel size = element size in DIP * Scale, independent of the screen's DPI scaling.</param>
        /// <param name="quality">0..100 for lossy formats (JPG, WebP); ignored for PNG and BMP.</param>
        /// <param name="write">Grabs the element and starts writing it. A Write while another write runs waits; of several waiting Writes
        /// only the newest is written.</param>
        public void Update(
            IElement? input,
            Path filePath,
            [DefaultValue(1f)] float scale,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(90)] int quality,
            bool write,
            out bool isDone,
            out bool success)
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
                _error = _path.Length == 0 ? ImageFileWriter.NoPathError : _grab.Start(input, scale);
                if (_error.Length > 0)
                    // Nothing is written: Done without Success in the same frame, so a patch waiting for Is Done does not hang.
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
            if (isDone)
                _success = _error.Length == 0;
            success = _success;
            _report.Update(null, _error);
        }

        public void Dispose()
        {
            _writer.Dispose();
            _grab.Dispose();
            _report.Dispose();
        }
    }
}
