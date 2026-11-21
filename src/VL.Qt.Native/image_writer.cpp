#include "image_writer.h"

#include "app/last_error.h"
#include "resources/pixel_format.h"

#include <QByteArray>
#include <QDir>
#include <QFileInfo>
#include <QImage>
#include <QImageWriter>
#include <QLoggingCategory>
#include <QString>

#include <algorithm>
#include <cstring>

// Image file writer. Any thread, no QObject / GUI-thread-only API: QImage and QImageWriter only.
namespace vlqt::imagewriter
{
    Q_LOGGING_CATEGORY(lcImageWriter, "VL.Qt.Image.Writer")

    namespace
    {
        constexpr int okRc = VLQT_OK, badValueRc = VLQT_ERR_BAD_ARGUMENT, unsupportedFormatRc = VLQT_ERR_UNSUPPORTED,
                      writeFailedRc = VLQT_ERR_IO;

        // File formats that store alpha; every other format gets the pixels over black.
        bool formatHasAlpha(const QByteArray& suffix)
        {
            return suffix == "png" || suffix == "webp" || suffix == "tif" || suffix == "tiff" || suffix == "avif";
        }

        // File formats that store more than 8 bits per channel (R16, half and full float pixels); the others get 8 bit
        // and a warning. TIFF is not in the list: QtNative ships no TIFF plugin.
        bool formatKeepsDeepPixels(const QByteArray& suffix)
        {
            return suffix == "png";
        }

        bool formatIsLossy(const QByteArray& suffix)
        {
            return suffix == "jpg" || suffix == "jpeg" || suffix == "webp";
        }
    }

    int write(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality)
    {
        if (!desc || !data || !utf8Path || !*utf8Path)
            return fail(badValueRc, QStringLiteral("null argument or empty path"));
        if (desc->width <= 0 || desc->height <= 0)
            return fail(badValueRc, QStringLiteral("image size must be > 0"));

        const QImage::Format qtFormat = res::qimageFormat(desc->format, desc->premultiplied != 0);
        if (qtFormat == QImage::Format_Invalid)
            return fail(badValueRc, QStringLiteral("unsupported pixel format %1").arg(desc->format));
        if (!res::holdsPixels(len, desc->width, desc->height, desc->row_bytes, desc->format))
            return fail(badValueRc,
                QStringLiteral("%1 bytes do not hold %2x%3 pixels of format %4 with %5 row bytes")
                    .arg(len).arg(desc->width).arg(desc->height).arg(desc->format).arg(desc->row_bytes));
        const bool hasAlpha = res::hasAlpha(desc->format);
        const qsizetype row = qsizetype(desc->width) * res::bytesPerPixel(desc->format);
        const qsizetype stride = desc->row_bytes > 0 ? desc->row_bytes : row;

        const QString path = QString::fromUtf8(utf8Path);
        const QByteArray suffix = QFileInfo(path).suffix().toLower().toLatin1();
        if (suffix.isEmpty() || !QImageWriter::supportedImageFormats().contains(suffix))
            return fail(unsupportedFormatRc,
                QStringLiteral("unsupported image file format \"%1\" (by extension)").arg(QString::fromLatin1(suffix)));

        // An owned copy, row by row: the last row of the caller's buffer may lack its padding, and a QImage that wraps
        // the buffer would read it as soon as a conversion detaches.
        QImage source(desc->width, desc->height, qtFormat);
        if (source.isNull())
            return fail(VLQT_ERR_INTERNAL, QStringLiteral("could not allocate the pixels (out of memory?)"));
        for (int y = 0; y < desc->height; y++)
            std::memcpy(source.scanLine(y), static_cast<const uchar*>(data) + stride * y, size_t(row));

        QImage image;
        if (res::isDeep(desc->format) && formatKeepsDeepPixels(suffix))
        {
            // 16 bit in the file: R16 as it is, float pixels as straight RGBA64 (values clamped to 0..1).
            image = hasAlpha ? source.convertToFormat(QImage::Format_RGBA64) : source;
        }
        else
        {
            if (res::isDeep(desc->format))
                qCWarning(lcImageWriter,
                    "warning: '%s' is written with 8 bit per channel; the pixels have more (format %d, HDR is lost). "
                    "PNG keeps 16 bit.", qPrintable(path), desc->format);
            if (formatHasAlpha(suffix) && hasAlpha)
            {
                // Straight alpha in the file; the writer does not un-premultiply for us in every format.
                image = source.convertToFormat(QImage::Format_ARGB32);
            }
            else if (hasAlpha)
            {
                // No alpha in the file: premultiplied color is color over black, so alpha is just dropped.
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
            return fail(VLQT_ERR_INTERNAL, QStringLiteral("could not convert the pixels (out of memory?)"));

        const QString folder = QFileInfo(path).absolutePath();
        if (!QDir().mkpath(folder))
            return fail(writeFailedRc, QStringLiteral("could not create the folder \"%1\"").arg(QDir::toNativeSeparators(folder)));

        QImageWriter writer(path, suffix);
        if (formatIsLossy(suffix) && quality >= 0)
            writer.setQuality(std::min(quality, 100));
        if (!writer.write(image))
        {
            const int rc = writer.error() == QImageWriter::UnsupportedFormatError ? unsupportedFormatRc : writeFailedRc;
            return fail(rc, writer.errorString());
        }
        return okRc;
    }
}
