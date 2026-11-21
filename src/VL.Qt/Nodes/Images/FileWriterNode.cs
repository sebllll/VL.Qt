using System;
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
        /// <summary>Error at the node when Write is hit without a File Path.</summary>
        internal const string NoPathError = "File Path is empty";
        /// <summary>Error at the node when Write is hit without an Image.</summary>
        internal const string NoImageError = "Image is empty";

        private readonly ImageFileWriter _writer;
        private readonly ImageNodeReport _report;
        private string _error = string.Empty;

        public FileWriterNode(NodeContext nodeContext)
        {
            _writer = ImageFileWriter.CreateNative();
            _report = new ImageNodeReport(new NodeErrors(nodeContext));
        }

        /// <summary>For tests: a writer on a fake write function.</summary>
        internal FileWriterNode(ImageFileWriter writer)
        {
            _writer = writer;
            _report = new ImageNodeReport(null);
        }

        /// <summary>Writes Image to File Path on Write.</summary>
        /// <param name="image">Image to write, e.g. from Snapshot; premultiplied alpha is handled. 16 bit (R16) and half/full float RGBA are written as 16-bit PNG (float values clamped to 0..1); other file formats get 8 bit and a warning in the log.</param>
        /// <param name="filePath">Target file; the extension picks the format (png, jpg, bmp, ...). Missing folders are created, an existing file is overwritten.</param>
        /// <param name="isDone">True in the frame the file was written (also when it failed, then Error says why).</param>
        /// <param name="error">Why the last write failed, empty otherwise.</param>
        /// <param name="quality">0..100 for lossy formats (JPG, WebP); ignored for PNG and BMP.</param>
        /// <param name="write">Starts writing the current Image (a write while one runs waits, only the newest one).</param>
        public void Update(
            IImage? image,
            Path filePath,
            out bool isDone,
            out string error,
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
                _error = string.Empty;
                var path = filePath?.Value ?? string.Empty;
                if (path.Length == 0)
                    _error = NoPathError;
                else if (image is null)
                    _error = NoImageError;
                if (_error.Length > 0)
                    // Nothing is written: Done and Error in the same frame, so a patch waiting for Is Done does not hang.
                    isDone = true;
                else
                    _writer.Start(image!, path, quality);
            }
            error = _error;
            _report.Update(null, error);
        }

        public void Dispose()
        {
            _writer.Dispose();
            _report.Dispose();
        }
    }
}
