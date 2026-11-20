using System;
using System.Threading;
using System.Threading.Tasks;
using VL.Lib.Basics.Imaging;

namespace VL.Qt
{
    /// <summary>
    /// Writes images to files on a worker thread for the FileWriter nodes. One write runs at a time per instance;
    /// a Start while one runs replaces the waiting one (the newest wins, at most one waits). Start and Poll are called on
    /// the main thread, the write delegate runs on a worker thread.
    /// </summary>
    internal sealed class ImageFileWriter : IDisposable
    {
        private readonly Func<IImage, string, int, string> _write;
        private readonly object _syncRoot = new();
        // Idle = no write runs or waits; the tests wait on it.
        private readonly ManualResetEventSlim _idle = new(true);
        private Job? _pending;
        private bool _running, _finished, _disposed;
        private string _error = string.Empty;

        private sealed record Job(IImage Image, string Path, int Quality);

        /// <param name="write">Writes the image to the path with the quality and returns the error, empty on success; worker thread.</param>
        internal ImageFileWriter(Func<IImage, string, int, string> write)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        /// <summary>A writer that encodes through QtNative (vlqt_image_write).</summary>
        public static ImageFileWriter CreateNative() => new(WriteNative);

        /// <summary>True while a write runs or waits.</summary>
        public bool IsBusy
        {
            get
            {
                lock (_syncRoot)
                    return _running;
            }
        }

        /// <summary>
        /// Starts writing image to path (quality 0..100 for lossy formats). A volatile image is copied here, before this
        /// call returns. Main thread.
        /// </summary>
        public void Start(IImage image, string path, int quality)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(path);
            var job = new Job(image.IsVolatile ? CopiedImage.Of(image) : image, path, quality);
            lock (_syncRoot)
            {
                if (_disposed)
                    return;
                // A new write starts clean: the result of an earlier one that nobody polled yet is dropped.
                _finished = false;
                if (_running)
                {
                    _pending = job;
                    return;
                }
                _running = true;
                _idle.Reset();
            }
            Task.Run(() => Run(job));
        }

        /// <summary>Main thread, once per frame: true in the frame a write finished, error is its error or empty.</summary>
        public bool Poll(out string error)
        {
            lock (_syncRoot)
            {
                if (!_finished || _disposed)
                {
                    error = string.Empty;
                    return false;
                }
                _finished = false;
                error = _error;
                return true;
            }
        }

        /// <summary>Lets a running write finish in the background, drops the waiting one, reports nothing more.</summary>
        public void Dispose()
        {
            lock (_syncRoot)
            {
                _disposed = true;
                _pending = null;
            }
        }

        /// <summary>For tests: waits until no write runs or waits any more; false on timeout.</summary>
        internal bool WaitIdle(TimeSpan timeout) => _idle.Wait(timeout);

        private void Run(Job? job)
        {
            while (job is not null)
            {
                string error;
                try
                {
                    error = _write(job.Image, job.Path, job.Quality) ?? string.Empty;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    error = e.Message;
                }
                lock (_syncRoot)
                {
                    _error = error;
                    _finished = true;
                    job = _pending;
                    _pending = null;
                    if (job is null)
                    {
                        _running = false;
                        _idle.Set();
                    }
                    else
                    {
                        // The next write starts: the result of this one is not reported (only the newest write reports).
                        _finished = false;
                    }
                }
            }
        }

        private static string WriteNative(IImage image, string path, int quality)
        {
            try
            {
                var info = image.Info;
                var format = ResourceClient.ToQtNativeFormat(info.Format);
                if (format == 0)
                    return $"Pixel format {info.Format} is not supported";
                // QtNative resolves a relative path against its own working directory, which is not the patch's.
                var fullPath = System.IO.Path.GetFullPath(path);
                using var data = image.GetData();
                var desc = new Native.vlqt_pixel_desc
                {
                    width = info.Width,
                    height = info.Height,
                    row_bytes = data.ScanSize,
                    format = format,
                    premultiplied = info.IsPremultipliedAlpha ? 1 : 0,
                };
                var rc = Native.ImageWrite(in desc, data.Bytes.Span, fullPath, quality, out var error);
                if (rc == 0)
                    return string.Empty;
                return error.Length > 0 ? error : $"Writing '{fullPath}' failed ({rc})";
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return e.Message;
            }
        }

        /// <summary>Own copy of the pixels of a volatile image, valid after the source was reused.</summary>
        private sealed class CopiedImage : IImage, IImageData
        {
            private readonly byte[] _bytes;

            private CopiedImage(ImageInfo info, int scanSize, byte[] bytes)
            {
                Info = info;
                ScanSize = scanSize;
                _bytes = bytes;
            }

            public static CopiedImage Of(IImage source)
            {
                using var data = source.GetData();
                return new CopiedImage(source.Info, data.ScanSize, data.Bytes.ToArray());
            }

            public ImageInfo Info { get; }
            public bool IsVolatile => false;
            public IImageData GetData() => this;
            public ReadOnlyMemory<byte> Bytes => _bytes;
            public int ScanSize { get; }
            public void Dispose() { }
        }
    }
}
