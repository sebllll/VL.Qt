using VL.Lib.Basics.Imaging;

namespace VL.Qt
{
    internal static class PixelFormats
    {
        /// <summary>
        /// VL <see cref="PixelFormat"/> → format number of vlqt_image_set_pixels (protocol.h: the VL numbers, append-only).
        /// 0 = not supported (Unknown or a format newer than QtNative's table).
        /// </summary>
        internal static int ToQtNative(PixelFormat format) => format switch
        {
            PixelFormat.R8 => 1,
            PixelFormat.R16 => 2,
            PixelFormat.R32F => 3,
            PixelFormat.R8G8B8 => 4,
            PixelFormat.B8G8R8 => 5,
            PixelFormat.R8G8B8X8 => 6,
            PixelFormat.R8G8B8A8 => 7,
            PixelFormat.B8G8R8X8 => 8,
            PixelFormat.B8G8R8A8 => 9,
            PixelFormat.R32G32B32A32F => 10,
            PixelFormat.R32G32F => 11,
            PixelFormat.R16G16B16A16F => 12,
            _ => 0,
        };
    }
}
