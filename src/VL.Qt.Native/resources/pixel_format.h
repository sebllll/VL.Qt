/* pixel_format.h - the VL PixelFormat numbers (VL.Core PixelFormat.cs, append-only, the same table as protocol.h) and what
 * QtNative knows about them: bytes per pixel and the QImage format that holds them. Used by the image kind and the image
 * writer.
 */
#pragma once

#include <QtGui/QImage>

namespace vlqt::res {

enum class VlPixelFormat : int
{
    R8 = 1, R16 = 2, R32F = 3, R8G8B8 = 4, B8G8R8 = 5, R8G8B8X8 = 6, R8G8B8A8 = 7, B8G8R8X8 = 8, B8G8R8A8 = 9,
    R32G32B32A32F = 10, R32G32F = 11, R16G16B16A16F = 12
};

// Bytes per pixel of a VL PixelFormat number, 0 = unknown.
int bytesPerPixel(int format);

// The QImage format with the memory layout of the VL PixelFormat number; Format_Invalid for formats without one (R32F,
// R32G32F, unknown numbers). premultiplied selects the premultiplied variant of the formats with alpha.
QImage::Format qimageFormat(int format, bool premultiplied);

// The format has an alpha channel (R8G8B8A8, B8G8R8A8, R16G16B16A16F, R32G32B32A32F).
bool hasAlpha(int format);

} // namespace vlqt::res
