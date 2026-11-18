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
    [ProcessNode(Name = "FileWriter", Category = Categories.Qt)]
    public sealed class FileWriterNode : IDisposable
    {
        readonly ImageFileWriter _writer;

        public FileWriterNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext)
        {
            _writer = ImageFileWriter.CreateNative();
        }

        /// <summary>For tests: a writer on a fake write function.</summary>
        internal FileWriterNode(ImageFileWriter writer)
        {
            _writer = writer;
        }

        /// <summary>Writes Image to File Path on Write.</summary>
        /// <param name="image">Image to write, e.g. from Snapshot; premultiplied alpha is handled.</param>
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
            // TODO: Implement (#157).
            isDone = false;
            error = string.Empty;
        }

        public void Dispose() => _writer.Dispose();
    }
}
