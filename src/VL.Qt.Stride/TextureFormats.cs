using Stride.Graphics;

namespace VL.Qt.Stride
{
    /// <summary>
    /// The texture formats of a QtTexture scene (docs/design/stride-output.md, "Formats, colour space and alpha"). No D3D11 here, so the unit tests compile
    /// it in; the native side is in <see cref="StrideInterop"/>.
    /// </summary>
    internal static class TextureFormats
    {
        /// <summary>Text of the error for a format that is not allowed.</summary>
        public const string FormatError = "Format not supported: use R8G8B8A8_UNorm, R8G8B8A8_UNorm_SRgb, B8G8R8A8_UNorm, B8G8R8A8_UNorm_SRgb or R16G16B16A16_Float";

        /// <summary>Returns the error text for a <paramref name="format"/> that is not allowed.</summary>
        public static string FormatErrorFor(PixelFormat format) => $"{FormatError} (got {format})";

        /// <summary>
        /// Returns true if <paramref name="format"/> is allowed for a QtTexture, with the format of the texture resource.
        /// Stride's view and the QtNative bind use <paramref name="format"/> itself (a DXGI_FORMAT; Stride's PixelFormat
        /// values are the same). 8-bit SRgb textures are typeless: QtNative writes them through an SRgb view, so SRgb and
        /// float textures hold linear premultiplied values (vlqt_offscreen_bind).
        /// </summary>
        public static bool TryGetTextureFormat(PixelFormat format, out PixelFormat textureFormat)
        {
            textureFormat = format switch
            {
                PixelFormat.R8G8B8A8_UNorm or PixelFormat.B8G8R8A8_UNorm or PixelFormat.R16G16B16A16_Float => format,
                PixelFormat.R8G8B8A8_UNorm_SRgb => PixelFormat.R8G8B8A8_Typeless,
                PixelFormat.B8G8R8A8_UNorm_SRgb => PixelFormat.B8G8R8A8_Typeless,
                _ => PixelFormat.None,
            };
            return textureFormat != PixelFormat.None;
        }
    }
}
