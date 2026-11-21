using System;
using System.Runtime.InteropServices;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        /// <summary>Pixels of vlqt_image_set_pixels (vlqt.h vlqt_pixel_desc); format = VL PixelFormat number.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_pixel_desc
        {
            public int width, height, row_bytes, format, premultiplied;
            /// <summary>vlqt_image_set_pixels: <see cref="Protocol.ImageLoadFlags"/> flags; 0 when QtNative fills the desc.</summary>
            public int flags;
        }

        /// <summary>
        /// Starts or replaces the content of an image resource from a file (flags <see cref="Protocol.ImageLoadFlags"/>); upload:
        /// <see cref="Protocol.ImageUpload"/> (the pin Upload of the image nodes).
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_image_load(ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint flags, int upload = Protocol.ImageUpload.Auto);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_image_set_pixels(ulong id, in vlqt_pixel_desc desc, in byte data, int len, int upload);

        /// <summary>Starts or replaces the content of an image resource from pixels (copied synchronously); upload: <see cref="Protocol.ImageUpload"/>.</summary>
        public static Result ImageSetPixels(ulong id, in vlqt_pixel_desc desc, ReadOnlySpan<byte> data, int upload = Protocol.ImageUpload.Auto)
            => vlqt_image_set_pixels(id, in desc, in MemoryMarshal.GetReference(data), data.Length, upload);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_image_read_preview(ulong id, ref byte dst, int cap, out vlqt_pixel_desc desc, out int written, out int required);

        /// <summary>
        /// Tooltip preview of an image loaded with <see cref="Protocol.ImageLoadFlags.Preview"/> (BGRA8 premultiplied, &lt;= 256 px), a
        /// buffer export: <see cref="Result.BufferTooSmall"/> with <paramref name="required"/> (desc set),
        /// <see cref="Result.NoData"/> none, <see cref="Result.UnknownId"/>.
        /// </summary>
        public static Result ImageReadPreview(ulong id, Span<byte> dst, out vlqt_pixel_desc desc, out int written, out int required)
            => vlqt_image_read_preview(id, ref MemoryMarshal.GetReference(dst), dst.Length, out desc, out written, out required);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_image_write_file(in vlqt_pixel_desc desc, in byte data, int len, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int quality);

        /// <summary>
        /// Writes pixels to an image file, format by extension (vlqt.h vlqt_image_write_file); any thread, synchronous.
        /// Returns <see cref="Result.Ok"/>, <see cref="Result.BadArgument"/>, <see cref="Result.Unsupported"/> (file format) or
        /// <see cref="Result.Io"/> (write failed), with the reason in error (<see cref="LastError"/>, empty when ok).
        /// </summary>
        public static Result ImageWriteFile(in vlqt_pixel_desc desc, ReadOnlySpan<byte> data, string path, int quality, out string error)
        {
            var rc = vlqt_image_write_file(in desc, in MemoryMarshal.GetReference(data), data.Length, path, quality);
            error = rc != Result.Ok ? LastError() : string.Empty;
            return rc;
        }
    }
}
