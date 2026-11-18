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
        readonly ResourceClient _client;
        readonly SceneRegistry _scene;
        readonly ImageFileWriter _writer;

        public FileWriterElementNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext)
        {
            _client = ResourceClient.ForApp(nodeContext.AppHost);
            _scene = SceneRegistry.ForApp(nodeContext.AppHost);
            _writer = ImageFileWriter.CreateNative();
        }

        /// <summary>For tests: a client on a fake API, an own scene registry and a writer on a fake write function.</summary>
        internal FileWriterElementNode(ResourceClient client, SceneRegistry scene, ImageFileWriter writer)
        {
            _client = client;
            _scene = scene;
            _writer = writer;
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
            // TODO: Implement (#157).
            isDone = false;
            error = string.Empty;
        }

        public void Dispose() => _writer.Dispose();
    }
}
