using System;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Model;
using VL.Lib.Basics.Imaging;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Writes an image (e.g. the Image of Snapshot) to a file on Write: PNG, JPG, BMP, ... by the extension of File Path.
    /// Asynchronous: the file is encoded and written on a worker thread, Is Done comes in a later frame.
    /// </summary>
    [ProcessNode(Name = "FileWriter", Category = Categories.Images)]
    public sealed class FileWriterNode : IDisposable
    {
        private readonly ImageFileWriter _writer;
        private readonly ResourceNodeReport _report;
        private string _error = string.Empty;
        private bool _success;

        public FileWriterNode(NodeContext nodeContext)
            : this(ImageFileWriter.CreateNative(), new NodeErrors(nodeContext))
        {
        }

        internal FileWriterNode(ImageFileWriter writer, NodeErrors? messages = null)
        {
            _writer = writer;
            _report = new ResourceNodeReport(messages, lasting: false);
        }

        /// <summary>Writes Image to File Path on Write.</summary>
        /// <param name="image">Image to write, e.g. from Snapshot; premultiplied alpha is handled. 16 bit (R16) and half/full float RGBA
        /// are written as 16-bit PNG (float values clamped to 0..1); other file formats get 8 bit and a warning in the log.</param>
        /// <param name="filePath">Target file; the extension picks the format (png, jpg, bmp, ...). Missing folders are created, an
        /// existing file is overwritten.</param>
        /// <param name="isDone">True in the frame the write ended, also when it failed (see Success).</param>
        /// <param name="success">True if the last write that ended wrote the file; set in the frame of Is Done and held until the next one.
        /// A failure is logged.</param>
        /// <param name="quality">0..100 for lossy formats (JPG, WebP); ignored for PNG and BMP.</param>
        /// <param name="write">Starts writing the current Image. A Write while another write runs waits; of several waiting Writes only the
        /// newest is written.</param>
        public void Update(
            IImage? image,
            Path filePath,
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
                _error = string.Empty;
                var path = filePath?.Value ?? string.Empty;
                if (path.Length == 0)
                    _error = ImageFileWriter.NoPathError;
                else if (image is null)
                    _error = ImageFileWriter.NoImageError;
                if (_error.Length > 0)
                    // Nothing is written: Done without Success in the same frame, so a patch waiting for Is Done does not hang.
                    isDone = true;
                else
                    _writer.Start(image!, path, quality);
            }
            if (isDone)
                _success = _error.Length == 0;
            success = _success;
            _report.Update(null, _error);
        }

        public void Dispose()
        {
            _writer.Dispose();
            _report.Dispose();
        }
    }
}
