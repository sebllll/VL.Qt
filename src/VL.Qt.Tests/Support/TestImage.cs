using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using VL.Lib.Basics.Imaging;

namespace VL.Qt.Tests.Support
{
    /// <summary>Image of the tests: own bytes that a test can change, optionally volatile.</summary>
    internal sealed class TestImage : IImage, IImageData
    {
        public TestImage(int width, int height, byte fill, bool isVolatile = false, PixelFormat format = PixelFormat.B8G8R8A8)
        {
            Info = new ImageInfo(width, height, format, isPremultipliedAlpha: true);
            IsVolatile = isVolatile;
            Buffer = Enumerable.Repeat(fill, width * height * 4).ToArray();
        }

        public byte[] Buffer { get; }
        public ImageInfo Info { get; }
        public bool IsVolatile { get; }
        public IImageData GetData() => this;
        public ReadOnlyMemory<byte> Bytes => Buffer;
        public int ScanSize => Info.Width * 4;
        public void Dispose() { }
    }
}
