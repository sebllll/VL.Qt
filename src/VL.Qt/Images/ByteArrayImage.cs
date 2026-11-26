using System;
using VL.Lib.Basics.Imaging;

namespace VL.Qt
{
    /// <summary>An image over a byte array it owns: not volatile, valid for as long as anyone holds it, nothing to dispose.</summary>
    internal sealed class ByteArrayImage : IImage, IImageData
    {
        private readonly byte[] _bytes;

        public ByteArrayImage(ImageInfo info, int scanSize, byte[] bytes)
        {
            Info = info;
            ScanSize = scanSize;
            _bytes = bytes;
        }

        /// <summary>Own copy of the pixels of <paramref name="source"/>, valid after a volatile source was reused.</summary>
        public static ByteArrayImage CopyOf(IImage source)
        {
            using var data = source.GetData();
            return new ByteArrayImage(source.Info, data.ScanSize, data.Bytes.ToArray());
        }

        public ImageInfo Info { get; }
        public bool IsVolatile => false;
        public IImageData GetData() => this;
        public ReadOnlyMemory<byte> Bytes => _bytes;
        public int ScanSize { get; }
        public void Dispose() { }
    }
}
