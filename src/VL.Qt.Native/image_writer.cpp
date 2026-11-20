#include "image_writer.h"

#include <QByteArray>
#include <QDir>
#include <QFileInfo>
#include <QImage>
#include <QImageWriter>
#include <QString>

#include <algorithm>
#include <cstring>

// Image file writer. Any thread, no QObject / GUI-thread-only API: QImage and QImageWriter only.
namespace vlqt::imagewriter
{
    namespace
    {
        constexpr int okRc = 0, badValueRc = -1, unsupportedFormatRc = -2, writeFailedRc = -3;

        // VL PixelFormat numbers (VL.Core PixelFormat.cs, same table as image_resource.cpp and protocol.h).
        enum PixelFormat
        {
            R8 = 1, R16 = 2, R32F = 3, R8G8B8 = 4, B8G8R8 = 5, R8G8B8X8 = 6, R8G8B8A8 = 7, B8G8R8X8 = 8, B8G8R8A8 = 9,
            R32G32B32A32F = 10, R32G32F = 11, R16G16B16A16F = 12
        };

        struct FormatInfo
        {
            int bytesPerPixel = 0;
            QImage::Format qt = QImage::Format_Invalid;
            bool hasAlpha = false;
        };

        // Formats the writer supports; R32F and R32G32F have no QImage counterpart (0 = unsupported).
        FormatInfo formatInfo(int format, bool premultiplied)
        {
            switch (format)
            {
            case R8:            return {1, QImage::Format_Grayscale8, false};
            case R16:           return {2, QImage::Format_Grayscale16, false};
            case R8G8B8:        return {3, QImage::Format_RGB888, false};
            case B8G8R8:        return {3, QImage::Format_BGR888, false};
            case R8G8B8X8:      return {4, QImage::Format_RGBX8888, false};
            case B8G8R8X8:      return {4, QImage::Format_RGB32, false};
            case R8G8B8A8:      return {4, premultiplied ? QImage::Format_RGBA8888_Premultiplied : QImage::Format_RGBA8888, true};
            case B8G8R8A8:      return {4, premultiplied ? QImage::Format_ARGB32_Premultiplied : QImage::Format_ARGB32, true};
            case R16G16B16A16F: return {8, premultiplied ? QImage::Format_RGBA16FPx4_Premultiplied : QImage::Format_RGBA16FPx4, true};
            case R32G32B32A32F: return {16, premultiplied ? QImage::Format_RGBA32FPx4_Premultiplied : QImage::Format_RGBA32FPx4, true};
            default:            return {};
            }
        }

        // File formats that store alpha; every other format gets the pixels over black.
        bool formatHasAlpha(const QByteArray& suffix)
        {
            return suffix == "png" || suffix == "webp" || suffix == "tif" || suffix == "tiff" || suffix == "avif";
        }

        bool formatIsLossy(const QByteArray& suffix)
        {
            return suffix == "jpg" || suffix == "jpeg" || suffix == "webp";
        }

        int fail(int rc, const QString& message, char* err, int errCap)
        {
            if (err && errCap > 0)
            {
                const QByteArray utf8 = message.toUtf8();
                auto n = static_cast<size_t>(std::min<qsizetype>(utf8.size(), errCap - 1));
                // Do not cut a UTF-8 sequence in half.
                while (n > 0 && n < static_cast<size_t>(utf8.size()) && (static_cast<uchar>(utf8[static_cast<qsizetype>(n)]) & 0xC0) == 0x80)
                    n--;
                std::memcpy(err, utf8.constData(), n);
                err[n] = '\0';
            }
            return rc;
        }
    }

    int write(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality, char* err, int errCap)
    {
        if (err && errCap > 0)
            err[0] = '\0';
        if (!desc || !data || !utf8Path || !*utf8Path)
            return fail(badValueRc, QStringLiteral("null argument or empty path"), err, errCap);
        if (desc->width <= 0 || desc->height <= 0)
            return fail(badValueRc, QStringLiteral("image size must be > 0"), err, errCap);

        const FormatInfo info = formatInfo(desc->format, desc->premultiplied != 0);
        if (info.bytesPerPixel == 0)
            return fail(badValueRc, QStringLiteral("unsupported pixel format %1").arg(desc->format), err, errCap);
        const qsizetype row = qsizetype(desc->width) * info.bytesPerPixel;
        const qsizetype stride = desc->row_bytes > 0 ? desc->row_bytes : row;
        if (stride < row || len < 0 || qsizetype(len) < stride * desc->height)
            return fail(badValueRc,
                QStringLiteral("%1 bytes do not hold %2x%3 pixels of format %4 with %5 row bytes")
                    .arg(len).arg(desc->width).arg(desc->height).arg(desc->format).arg(desc->row_bytes), err, errCap);

        const QString path = QString::fromUtf8(utf8Path);
        const QByteArray suffix = QFileInfo(path).suffix().toLower().toLatin1();
        if (suffix.isEmpty() || !QImageWriter::supportedImageFormats().contains(suffix))
            return fail(unsupportedFormatRc,
                QStringLiteral("unsupported image file format \"%1\" (by extension)").arg(QString::fromLatin1(suffix)), err, errCap);

        // The QImage only wraps the caller's pixels; every conversion below makes a copy.
        const QImage source(static_cast<const uchar*>(data), desc->width, desc->height, stride, info.qt);
        QImage image;
        if (formatHasAlpha(suffix) && info.hasAlpha)
        {
            // Straight alpha in the file; the writer does not un-premultiply for us in every format.
            image = source.convertToFormat(QImage::Format_ARGB32);
        }
        else
        {
            // No alpha in the file: premultiplied color is color over black, so alpha is just dropped.
            if (info.hasAlpha)
            {
                image = source.convertToFormat(QImage::Format_ARGB32_Premultiplied);
                for (int y = 0; y < image.height(); y++)
                {
                    auto* p = reinterpret_cast<quint32*>(image.scanLine(y));
                    for (int x = 0; x < image.width(); x++)
                        p[x] |= 0xff000000u;
                }
                image.reinterpretAsFormat(QImage::Format_RGB32);
            }
            else
                image = source.convertToFormat(QImage::Format_RGB32);
        }
        if (image.isNull())
            return fail(writeFailedRc, QStringLiteral("could not convert the pixels (out of memory?)"), err, errCap);

        const QString folder = QFileInfo(path).absolutePath();
        if (!QDir().mkpath(folder))
            return fail(writeFailedRc, QStringLiteral("could not create the folder \"%1\"").arg(QDir::toNativeSeparators(folder)), err, errCap);

        QImageWriter writer(path, suffix);
        if (formatIsLossy(suffix) && quality >= 0)
            writer.setQuality(std::min(quality, 100));
        if (!writer.write(image))
        {
            const int rc = writer.error() == QImageWriter::UnsupportedFormatError ? unsupportedFormatRc : writeFailedRc;
            return fail(rc, writer.errorString(), err, errCap);
        }
        return okRc;
    }
}
