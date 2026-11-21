#include "image_writer.h"

#include "app/last_error.h"
#include "resources/pixel_format.h"

#include <QByteArray>
#include <QDir>
#include <QFileInfo>
#include <QImage>
#include <QImageWriter>
#include <QString>

#include <algorithm>

// Image file writer. Any thread, no QObject / GUI-thread-only API: QImage and QImageWriter only.
namespace vlqt::imagewriter
{
    namespace
    {
        constexpr int okRc = VLQT_OK, badValueRc = VLQT_ERR_BAD_ARGUMENT, unsupportedFormatRc = VLQT_ERR_UNSUPPORTED,
                      writeFailedRc = VLQT_ERR_IO;

        // File formats that store alpha; every other format gets the pixels over black.
        bool formatHasAlpha(const QByteArray& suffix)
        {
            return suffix == "png" || suffix == "webp" || suffix == "tif" || suffix == "tiff" || suffix == "avif";
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
        const bool hasAlpha = res::hasAlpha(desc->format);
        const qsizetype row = qsizetype(desc->width) * res::bytesPerPixel(desc->format);
        const qsizetype stride = desc->row_bytes > 0 ? desc->row_bytes : row;
        if (stride < row || len < 0 || qsizetype(len) < stride * desc->height)
            return fail(badValueRc,
                QStringLiteral("%1 bytes do not hold %2x%3 pixels of format %4 with %5 row bytes")
                    .arg(len).arg(desc->width).arg(desc->height).arg(desc->format).arg(desc->row_bytes));

        const QString path = QString::fromUtf8(utf8Path);
        const QByteArray suffix = QFileInfo(path).suffix().toLower().toLatin1();
        if (suffix.isEmpty() || !QImageWriter::supportedImageFormats().contains(suffix))
            return fail(unsupportedFormatRc,
                QStringLiteral("unsupported image file format \"%1\" (by extension)").arg(QString::fromLatin1(suffix)));

        // The QImage only wraps the caller's pixels; every conversion below makes a copy.
        const QImage source(static_cast<const uchar*>(data), desc->width, desc->height, stride, qtFormat);
        QImage image;
        if (formatHasAlpha(suffix) && hasAlpha)
        {
            // Straight alpha in the file; the writer does not un-premultiply for us in every format.
            image = source.convertToFormat(QImage::Format_ARGB32);
        }
        else
        {
            // No alpha in the file: premultiplied color is color over black, so alpha is just dropped.
            if (hasAlpha)
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
