using System;
using VL.Lib.Basics.Imaging;

namespace VL.Qt
{
    /// <summary>
    /// Writes images to files on a worker thread for the FileWriter nodes (#157). One write runs at a time per instance;
    /// a Start while one runs replaces the waiting one (the newest wins, at most one waits). Start and Poll are called on
    /// the main thread, the write delegate runs on a worker thread.
    /// </summary>
    internal sealed class ImageFileWriter : IDisposable
    {
        readonly Func<IImage, string, int, string> _write;

        /// <param name="write">Writes the image to the path with the quality and returns the error, empty on success; worker thread.</param>
        internal ImageFileWriter(Func<IImage, string, int, string> write)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        /// <summary>A writer that encodes through QtNative (vlqt_image_write).</summary>
        public static ImageFileWriter CreateNative() => new(WriteNative);

        /// <summary>True while a write runs or waits.</summary>
        public bool IsBusy => false;

        /// <summary>
        /// Starts writing image to path (quality 0..100 for lossy formats). A volatile image is copied here, before this
        /// call returns. Main thread.
        /// </summary>
        public void Start(IImage image, string path, int quality)
        {
            // TODO: Implement (#157).
        }

        /// <summary>Main thread, once per frame: true in the frame a write finished, error is its error or empty.</summary>
        public bool Poll(out string error)
        {
            // TODO: Implement (#157).
            error = string.Empty;
            return false;
        }

        /// <summary>Lets a running write finish in the background, drops the waiting one, reports nothing more.</summary>
        public void Dispose()
        {
        }

        static string WriteNative(IImage image, string path, int quality)
        {
            // TODO: Implement (#157): IImage -> Native.vlqt_pixel_desc + bytes -> Native.ImageWrite.
            return "Not implemented";
        }
    }
}
