#include "resources/pixel_format.h"

namespace vlqt::res {

int bytesPerPixel(int format)
{
    switch (VlPixelFormat(format)) {
    case VlPixelFormat::R8:            return 1;
    case VlPixelFormat::R16:           return 2;
    case VlPixelFormat::R8G8B8:
    case VlPixelFormat::B8G8R8:        return 3;
    case VlPixelFormat::R32F:
    case VlPixelFormat::R8G8B8X8:
    case VlPixelFormat::R8G8B8A8:
    case VlPixelFormat::B8G8R8X8:
    case VlPixelFormat::B8G8R8A8:      return 4;
    case VlPixelFormat::R32G32F:
    case VlPixelFormat::R16G16B16A16F: return 8;
    case VlPixelFormat::R32G32B32A32F: return 16;
    default:                           return 0;
    }
}

QImage::Format qimageFormat(int format, bool premultiplied)
{
    switch (VlPixelFormat(format)) {
    case VlPixelFormat::R8:            return QImage::Format_Grayscale8;
    case VlPixelFormat::R16:           return QImage::Format_Grayscale16;
    case VlPixelFormat::R8G8B8:        return QImage::Format_RGB888;
    case VlPixelFormat::B8G8R8:        return QImage::Format_BGR888;
    case VlPixelFormat::R8G8B8X8:      return QImage::Format_RGBX8888;
    case VlPixelFormat::B8G8R8X8:      return QImage::Format_RGB32;
    case VlPixelFormat::R8G8B8A8:      return premultiplied ? QImage::Format_RGBA8888_Premultiplied : QImage::Format_RGBA8888;
    case VlPixelFormat::B8G8R8A8:      return premultiplied ? QImage::Format_ARGB32_Premultiplied : QImage::Format_ARGB32;
    case VlPixelFormat::R16G16B16A16F: return premultiplied ? QImage::Format_RGBA16FPx4_Premultiplied : QImage::Format_RGBA16FPx4;
    case VlPixelFormat::R32G32B32A32F: return premultiplied ? QImage::Format_RGBA32FPx4_Premultiplied : QImage::Format_RGBA32FPx4;
    default:                           return QImage::Format_Invalid;
    }
}

bool hasAlpha(int format)
{
    switch (VlPixelFormat(format)) {
    case VlPixelFormat::R8G8B8A8:
    case VlPixelFormat::B8G8R8A8:
    case VlPixelFormat::R16G16B16A16F:
    case VlPixelFormat::R32G32B32A32F: return true;
    default:                           return false;
    }
}

bool isDeep(int format)
{
    switch (VlPixelFormat(format)) {
    case VlPixelFormat::R16:
    case VlPixelFormat::R16G16B16A16F:
    case VlPixelFormat::R32G32B32A32F: return true;
    default:                           return false;
    }
}

bool holdsPixels(int len, int width, int height, int rowBytes, int format)
{
    const int bpp = bytesPerPixel(format);
    if (bpp == 0 || width <= 0 || height <= 0 || len < 0)
        return false;
    const qsizetype row = qsizetype(width) * bpp;
    const qsizetype stride = rowBytes > 0 ? rowBytes : row;
    return stride >= row && qsizetype(len) >= stride * (height - 1) + row;
}

} // namespace vlqt::res
