// The image kind: decode (file or pixels), preview, device loss and the C API backends. The upload is in
// image_upload_rhi.cpp, the worker path in image_upload_d3d11.cpp.

#include "resources/image_kind.h"
#include "resources/gpu_texture.h"
#include "resources/image_upload_d3d11.h"
#include "resources/pixel_format.h"

#include "app/exports.h"
#include "device/device.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QLoggingCategory>
#include <QtGui/QImageReader>
#include <rhi/qrhi.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <mutex>
#include <vector>

namespace vlqt::res::image {

namespace {

/* VL PixelFormat (VL.Core PixelFormat.cs, append-only) -> what QtNative does with it. Same table as protocol.h.
 *   direct:              R8G8B8A8 (7), B8G8R8A8 (9), R8G8B8X8 (6), B8G8R8X8 (8)   X: alpha forced to 255
 *   converted to RGBA8:  R8G8B8 (4), B8G8R8 (5), R8 (1, grey)
 *   reduced to 8 bit:    R16 (2, grey), R32F (3, grey, [0,1]), R32G32F (11, R, G, 0), R32G32B32A32F (10),
 *                        R16G16B16A16F (12); one warning per resource
 *   else (Unknown = 0, newer numbers): Failed */
constexpr const char* reducedWarning = "warning: %1 has more than 8 bits per channel, reduced to 8 bit (HDR is lost)";

bool deepQtFormat(QImage::Format f)
{
    switch (f) {
    case QImage::Format_RGBX64:
    case QImage::Format_RGBA64:
    case QImage::Format_RGBA64_Premultiplied:
    case QImage::Format_Grayscale16:
    case QImage::Format_BGR30:
    case QImage::Format_A2BGR30_Premultiplied:
    case QImage::Format_RGB30:
    case QImage::Format_A2RGB30_Premultiplied:
    case QImage::Format_RGBX16FPx4:
    case QImage::Format_RGBA16FPx4:
    case QImage::Format_RGBA16FPx4_Premultiplied:
    case QImage::Format_RGBX32FPx4:
    case QImage::Format_RGBA32FPx4:
    case QImage::Format_RGBA32FPx4_Premultiplied:
        return true;
    default:
        return false;
    }
}

void forceAlpha(QByteArray& raw)
{
    auto* d = reinterpret_cast<uchar*>(raw.data());
    for (qsizetype i = 3; i < raw.size(); i += 4)
        d[i] = 255;
}

uchar unitToByte(float v)
{
    return uchar(std::clamp(v, 0.0f, 1.0f) * 255.0f + 0.5f);
}

// Upload path (measured in #360 and #390, numbers in the PRs): files -> worker path (empty textures in the worker,
// the pixels through a mapped staging texture filled by a copy task, then a GPU copy: < 1 ms per 4K on the main
// thread; stripes instead: the first write into a new 4K texture costs ~9 ms on the main thread, ~1 ms per 8 MB after
// it, 20 x 4K: 22 % hitches), pixels -> stripes in the frame budget (set_pixels per frame). Mipmaps take the worker
// path too: the texture gets its mip chain in the worker (GENERATE_MIPS), the main thread copies level 0 and records
// GenerateMips, without an offscreen frame (whose endOffscreenFrame waited actively for the GPU: 2.2 ms per frame in the
// profile Images). upload: vlqt_image_upload of the image (the pin Upload of the image nodes, for A/B measurements);
// AUTO is the choice above.
[[maybe_unused]] bool workerUpload(bool file, int upload)
{
    return upload == VLQT_IMAGE_UPLOAD_WORKER || (upload == VLQT_IMAGE_UPLOAD_AUTO && file);
}

bool validUpload(int upload)
{
    return upload == VLQT_IMAGE_UPLOAD_AUTO || upload == VLQT_IMAGE_UPLOAD_MAIN_THREAD || upload == VLQT_IMAGE_UPLOAD_WORKER;
}

/* Raw buffers of set_pixels, kept for the next set_pixels of the same size: a 1080p frame is 8 MB that the main thread
 * would otherwise allocate and fault in per frame (W1-17: 1.7 ms of set_pixels at 1080p). Taken on the main thread
 * (setPixels), given back by the worker (convertPixels, when the converted copy replaces the raw one) or by the last
 * holder of a content (~ImageContent, any thread): hence the mutex. Only buffers no other QByteArray shares are kept
 * (a restored content shares the raw copy of the old one). The buffers themselves are freed outside the lock. */
class BufferPool
{
public:
    // A free buffer of exactly size bytes, else an empty one.
    QByteArray take(qsizetype size)
    {
        if (size < minBytes)
            return {};
        std::lock_guard lock(m_mutex);
        for (auto it = m_free.begin(); it != m_free.end(); ++it) {
            if (it->buffer.size() == size) {
                QByteArray b = std::move(it->buffer);
                m_free.erase(it);
                return b;
            }
        }
        return {};
    }

    void give(QByteArray&& buffer)
    {
        QByteArray b = std::move(buffer);
        if (b.size() < minBytes || !b.isDetached())
            return;
        QByteArray evicted; // the oldest one when the pool is full, freed after the lock
        std::lock_guard lock(m_mutex);
        if (m_free.size() >= maxBuffers) {
            evicted = std::move(m_free.front().buffer);
            m_free.erase(m_free.begin());
        }
        m_free.push_back({std::move(b), m_frame});
    }

    // Main thread, once per pump: frees the buffers nobody took for about a second.
    void endFrame()
    {
        std::array<QByteArray, maxBuffers> idle; // freed after the lock
        size_t n = 0;
        std::lock_guard lock(m_mutex);
        m_frame++;
        for (auto it = m_free.begin(); it != m_free.end();) {
            if (m_frame - it->since > idleFrames) {
                idle[n++] = std::move(it->buffer);
                it = m_free.erase(it);
            } else {
                ++it;
            }
        }
    }

private:
    static constexpr qsizetype minBytes = 1 << 20; // smaller buffers are cheap to allocate
    static constexpr size_t maxBuffers = 4;         // current, replacement, waiting and one an item still holds
    static constexpr int64_t idleFrames = 60;       // ~1 s at 60 Hz

    struct Entry
    {
        QByteArray buffer;
        int64_t since = 0; // m_frame when given back
    };
    std::mutex m_mutex;
    std::vector<Entry> m_free; // at most maxBuffers, oldest first
    int64_t m_frame = 0;
};

// Made at the first use and never deleted: a content may die at process end, after the statics.
BufferPool& buffers()
{
    static auto* s_buffers = new BufferPool();
    return *s_buffers;
}

} // namespace

ImageContent::ImageContent() = default;

// Main thread for content on the shared device; the CPU copy of a window wrapper (image_texture_cache.h) may go on a
// render thread. Nothing here needs the main thread: the texture is only a reference while the pool holds it.
ImageContent::~ImageContent()
{
    ImageKind::releaseGpu(*this);
    m_image = QImage(); // may point into m_raw
    buffers().give(std::move(m_raw));
}

QRhiTexture* ImageContent::texture() const
{
    return m_gpu ? m_gpu->rhiTexture() : nullptr;
}

void* ImageContent::nativeTexture() const
{
    return m_gpu && m_gpu->rhiTexture() ? m_gpu->native() : nullptr;
}

std::string ImageKind::decode(Content& work)
{
    auto& r = static_cast<ImageContent&>(work);
    // Pixels restored after a device loss bring their converted copy along: nothing to convert.
    std::string error = !r.m_path.isEmpty() ? readFile(r) : r.m_image.isNull() ? convertPixels(r) : std::string();
    if (!error.empty() || r.cancel.load())
        return error.empty() ? std::string("cancelled") : error;
    r.m_size = r.m_image.size();
    makePreview(r);
#ifdef Q_OS_WIN
    if (r.m_device && workerUpload(!r.m_path.isEmpty(), r.m_upload)) {
        // Removed device: no error, the rebuild decodes again for the new device (stale). Mipmaps that the
        // device cannot make this way fall back to the stripes (generateMips through the upload QRhi).
        auto upload = std::make_unique<D3D11Upload>();
        const bool mips = (r.m_flags & VLQT_IMAGE_LOAD_MIPMAPS) != 0;
        if (upload->createInWorker(static_cast<ID3D11Device*>(r.m_device), r.m_size, r.m_bgra, mips))
            r.m_d3d11 = std::move(upload);
        else if (!D3D11Upload::deviceRemoved(r.m_device)) {
            if (!mips)
                return "CreateTexture2D failed";
            qCDebug(lcRes, "worker textures with mipmaps failed, uploading in stripes");
        }
    }
#endif
    return {};
}

// Files: the path again (decoded anew, the CPU copy is gone after the upload); pixels: the converted copy (and its
// raw buffer, the copy may point into it), only uploaded again.
std::shared_ptr<Content> ImageKind::restore(const Content& work)
{
    const auto& old = static_cast<const ImageContent&>(work);
    auto r = std::make_shared<ImageContent>();
    r->m_path = old.m_path;
    r->m_flags = old.m_flags;
    r->m_upload = old.m_upload;
    r->m_preview = old.m_preview; // same content: no new scaling (implicitly shared, read-only after Ready)
    if (old.m_path.isEmpty()) {
        r->m_raw = old.m_raw;
        r->m_rawWidth = old.m_rawWidth;
        r->m_rawHeight = old.m_rawHeight;
        r->m_rawFormat = old.m_rawFormat;
        r->m_rawPremultiplied = old.m_rawPremultiplied;
        r->m_image = old.m_image;
        r->m_bgra = old.m_bgra;
    }
    r->m_device = device::nativeDevice(device::uploadRhi());
    r->m_generation = device::generation();
    return r;
}

bool ImageKind::stale(const Content& work) const
{
    return static_cast<const ImageContent&>(work).m_generation != device::generation();
}

Info ImageKind::info(const Content& work) const
{
    const auto& r = static_cast<const ImageContent&>(work);
    return {uint32_t(r.m_size.width()), uint32_t(r.m_size.height()), r.format()};
}

// F5 Resources page: rows not yet uploaded (a worker texture goes up in one call: all of it until then).
int64_t ImageKind::pendingBytes(const Content& work) const
{
    const auto& r = static_cast<const ImageContent&>(work);
    if (r.m_d3d11)
        return bytes(r);
    const int rows = (std::max)(0, r.m_image.height() - r.m_uploadedRows); // windows.h max macro
    return int64_t(r.m_image.bytesPerLine()) * rows;
}

std::string ImageKind::takeWarning(Content& work)
{
    return std::move(static_cast<ImageContent&>(work).m_warning);
}

// Tooltip preview: longest edge <= 256 px (never upscaled), BGRA8 premultiplied. Made right after m_image is
// set (files: readFile, pixels: convertPixels, restored pixels: the copied m_image), before the upload drops m_image;
// m_preview lives until the Content is destroyed. A deep copy: m_image may share its data with m_raw / the file buffer.
// A restore takes the preview of the old Content over and skips this. Large images first go down to 4 x 256 px
// with the fast filter (a single smooth step from 8k costs tens of ms in the worker, which a synchronous LoadImage
// waits for), then smooth to 256.
void ImageKind::makePreview(ImageContent& r)
{
    if (!(r.m_flags & VLQT_IMAGE_LOAD_PREVIEW) || r.m_image.isNull() || !r.m_preview.isNull())
        return;
    constexpr int previewEdge = 256;
    constexpr int fastEdge = 4 * previewEdge;
    const QImage& img = r.m_image;
    QImage p;
    const int longest = (std::max)(img.width(), img.height()); // windows.h max macro
    if (longest > fastEdge)
        p = img.scaled(fastEdge, fastEdge, Qt::KeepAspectRatio, Qt::FastTransformation)
                .scaled(previewEdge, previewEdge, Qt::KeepAspectRatio, Qt::SmoothTransformation);
    else if (longest > previewEdge)
        p = img.scaled(previewEdge, previewEdge, Qt::KeepAspectRatio, Qt::SmoothTransformation);
    else
        p = img.copy();
    if (p.format() != QImage::Format_ARGB32_Premultiplied)
        p.convertTo(QImage::Format_ARGB32_Premultiplied);
    r.m_preview = std::move(p);
}

std::string ImageKind::readFile(ImageContent& r)
{
    QImageReader reader(r.m_path);
    reader.setAutoTransform(true); // EXIF rotation
    QImage img;
    if (!reader.read(&img))
        return QStringLiteral("cannot load '%1': %2").arg(r.m_path, reader.errorString()).toStdString();
    if (r.cancel.load())
        return "cancelled";
    if (deepQtFormat(img.format()))
        r.m_warning = QString::fromLatin1(reducedWarning).arg(QStringLiteral("'%1'").arg(r.m_path)).toStdString();
    switch (img.format()) {
    case QImage::Format_RGB32: // 0xffRRGGBB: already valid premultiplied BGRA in memory
        img.reinterpretAsFormat(QImage::Format_ARGB32_Premultiplied);
        r.m_bgra = true;
        break;
    case QImage::Format_ARGB32_Premultiplied:
        r.m_bgra = true;
        break;
    case QImage::Format_ARGB32:
        img.convertTo(QImage::Format_ARGB32_Premultiplied);
        r.m_bgra = true;
        break;
    default:
        img.convertTo(QImage::Format_RGBA8888_Premultiplied);
        r.m_bgra = false;
        break;
    }
    r.m_image = std::move(img);
    return {};
}

std::string ImageKind::convertPixels(ImageContent& r)
{
    using F = VlPixelFormat;
    const int w = r.m_rawWidth, h = r.m_rawHeight, fmt = r.m_rawFormat;
    const int bpp = bytesPerPixel(fmt);
    if (bpp == 0)
        return "unsupported pixel format " + std::to_string(fmt);
    const auto* d = reinterpret_cast<const uchar*>(r.m_raw.constData());
    const qsizetype bpl = qsizetype(w) * bpp;
    const bool pm = r.m_rawPremultiplied;
    auto reduced = [&](const char* what) {
        r.m_warning = QString::fromLatin1(reducedWarning).arg(QLatin1String(what)).toStdString();
    };
    QImage img;
    bool keepRaw = false; // img points into m_raw
    r.m_bgra = false;
    switch (F(fmt)) {
    case F::R8G8B8X8:
        forceAlpha(r.m_raw);
        img = QImage(d, w, h, bpl, QImage::Format_RGBA8888_Premultiplied);
        keepRaw = true;
        break;
    case F::R8G8B8A8:
        img = QImage(d, w, h, bpl, pm ? QImage::Format_RGBA8888_Premultiplied : QImage::Format_RGBA8888);
        keepRaw = pm;
        if (!pm)
            img = img.convertToFormat(QImage::Format_RGBA8888_Premultiplied);
        break;
    case F::B8G8R8X8:
        forceAlpha(r.m_raw);
        img = QImage(d, w, h, bpl, QImage::Format_ARGB32_Premultiplied);
        keepRaw = true;
        r.m_bgra = true;
        break;
    case F::B8G8R8A8:
        img = QImage(d, w, h, bpl, pm ? QImage::Format_ARGB32_Premultiplied : QImage::Format_ARGB32);
        keepRaw = pm;
        if (!pm)
            img = img.convertToFormat(QImage::Format_ARGB32_Premultiplied);
        r.m_bgra = true;
        break;
    case F::R8G8B8:
        img = QImage(d, w, h, bpl, QImage::Format_RGB888).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
        break;
    case F::B8G8R8:
        img = QImage(d, w, h, bpl, QImage::Format_BGR888).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
        break;
    case F::R8:
        img = QImage(d, w, h, bpl, QImage::Format_Grayscale8).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
        break;
    case F::R16:
        reduced("R16");
        img = QImage(d, w, h, bpl, QImage::Format_Grayscale16).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
        break;
    case F::R16G16B16A16F:
        reduced("R16G16B16A16F");
        img = QImage(d, w, h, bpl, pm ? QImage::Format_RGBA16FPx4_Premultiplied : QImage::Format_RGBA16FPx4)
                  .convertToFormat(QImage::Format_RGBA8888_Premultiplied);
        break;
    case F::R32G32B32A32F:
        reduced("R32G32B32A32F");
        img = QImage(d, w, h, bpl, pm ? QImage::Format_RGBA32FPx4_Premultiplied : QImage::Format_RGBA32FPx4)
                  .convertToFormat(QImage::Format_RGBA8888_Premultiplied);
        break;
    case F::R32F:
    case F::R32G32F: {
        reduced(F(fmt) == F::R32F ? "R32F" : "R32G32F");
        img = QImage(w, h, QImage::Format_RGBA8888_Premultiplied);
        for (int y = 0; y < h; y++) {
            const auto* src = reinterpret_cast<const float*>(d + bpl * y);
            uchar* dst = img.scanLine(y);
            for (int x = 0; x < w; x++, dst += 4) {
                if (F(fmt) == F::R32F) {
                    dst[0] = dst[1] = dst[2] = unitToByte(src[x]);
                } else {
                    dst[0] = unitToByte(src[2 * x]);
                    dst[1] = unitToByte(src[2 * x + 1]);
                    dst[2] = 0;
                }
                dst[3] = 255;
            }
        }
        break;
    }
    default:
        return "unsupported pixel format " + std::to_string(fmt);
    }
    if (img.isNull())
        return "conversion of pixel format " + std::to_string(fmt) + " failed (out of memory?)";
    if (!keepRaw)
        buffers().give(std::move(r.m_raw)); // the converted image is the CPU copy now
    r.m_image = std::move(img);
    return {};
}

int ImageKind::load(uint64_t id, const char* utf8Path, unsigned flags, int upload)
{
    if (!utf8Path || !*utf8Path) {
        qCWarning(lcRes, "load_image: empty path");
        return badValueRc;
    }
    if (!validUpload(upload))
        return fail(badValueRc, "image_load: unknown upload mode " + std::to_string(upload));
    auto r = std::make_shared<ImageContent>();
    r->m_path = QString::fromUtf8(utf8Path);
    r->m_flags = flags;
    r->m_upload = upload;
    r->m_device = device::nativeDevice(device::uploadRhi());
    r->m_generation = device::generation();
    return start(id, std::move(r), VLQT_RESOURCE_KIND_IMAGE);
}

int ImageKind::setPixels(uint64_t id, const vlqt_pixel_desc* desc, const void* data, int len, int upload)
{
    if (!desc || !data || desc->width <= 0 || desc->height <= 0) {
        qCWarning(lcRes, "set_pixels: %s", !desc ? "null desc" : !data ? "null data" : "size <= 0");
        return badValueRc;
    }
    if (!validUpload(upload))
        return fail(badValueRc, "image_set_pixels: unknown upload mode " + std::to_string(upload));
    auto r = std::make_shared<ImageContent>();
    r->m_flags = desc->flags;
    r->m_upload = upload;
    r->m_rawWidth = desc->width;
    r->m_rawHeight = desc->height;
    r->m_rawFormat = desc->format;
    r->m_rawPremultiplied = desc->premultiplied != 0;
    r->m_device = device::nativeDevice(device::uploadRhi());
    r->m_generation = device::generation();
    // Unknown formats are copied not at all and fail in the decode (Failed with text, like a broken file).
    const int bpp = bytesPerPixel(desc->format);
    if (bpp > 0) {
        if (!holdsPixels(len, desc->width, desc->height, desc->row_bytes, desc->format)) {
            qCWarning(lcRes, "set_pixels: %d bytes do not hold %dx%d pixels of format %d with %d row bytes", len,
                desc->width, desc->height, desc->format, desc->row_bytes);
            return badValueRc;
        }
        const qsizetype row = qsizetype(desc->width) * bpp;
        const qsizetype stride = desc->row_bytes > 0 ? desc->row_bytes : row;
        r->m_raw = buffers().take(row * desc->height);
        if (r->m_raw.isEmpty())
            r->m_raw = QByteArray(row * desc->height, Qt::Uninitialized);
        const auto* src = static_cast<const char*>(data);
        if (stride == row) {
            std::memcpy(r->m_raw.data(), src, size_t(row) * desc->height);
        } else {
            for (int y = 0; y < desc->height; y++)
                std::memcpy(r->m_raw.data() + row * y, src + stride * y, size_t(row));
        }
    }
    return start(id, std::move(r), VLQT_RESOURCE_KIND_IMAGE);
}

Kind* kind()
{
    static ImageKind s_kind;
    return &s_kind;
}

int load(uint64_t id, const char* utf8Path, unsigned flags, int upload)
{
    return ImageKind::load(id, utf8Path, flags, upload);
}

int setPixels(uint64_t id, const vlqt_pixel_desc* desc, const void* data, int len, int upload)
{
    return ImageKind::setPixels(id, desc, data, len, upload);
}

void endFrame()
{
    buffers().endFrame();
    texture_pool::endFrame();
}

std::shared_ptr<ImageContent> ready(uint64_t id)
{
    return std::static_pointer_cast<ImageContent>(readyContent(id, VLQT_RESOURCE_KIND_IMAGE));
}

int readPreview(uint64_t id, unsigned char* dst, int cap, vlqt_pixel_desc* desc, int* written, int* required)
{
    if (const int rc = beginBuffer(dst, cap, written, required); rc != okRc)
        return rc;
    const int k = kindOfId(id, "read_preview");
    if (k < 0)
        return k;
    if (k != VLQT_RESOURCE_KIND_IMAGE) {
        qCWarning(lcRes, "read_preview: id %llu is not an image resource", static_cast<unsigned long long>(id));
        return badValueRc;
    }
    // Replacement of a Ready resource: readyContent is the visible content (the replacement is the separate Slot::replacement
    // until its swap), so the preview always belongs to what ready() shows.
    const auto r = ready(id);
    if (!r || r->preview().isNull())
        return noDataRc; // not Ready yet / Failed / loaded without VLQT_IMAGE_LOAD_PREVIEW
    // No lock: the worker wrote the preview in decode, before the Ready transition (made under the manager's mutex,
    // which readyContent takes as well); afterwards nothing touches it until the Content is destroyed.
    const QImage& img = r->preview();
    const int rowBytes = img.width() * 4;
    const int64_t size = int64_t(rowBytes) * img.height();
    if (desc) {
        desc->width = img.width();
        desc->height = img.height();
        desc->row_bytes = rowBytes;
        desc->format = 9; // B8G8R8A8
        desc->premultiplied = 1;
        desc->flags = 0;
    }
    if (const int rc = bufferFits(size, cap, written, required); rc != okRc)
        return rc;
    for (int y = 0; y < img.height(); y++)
        std::memcpy(dst + size_t(y) * rowBytes, img.constScanLine(y), size_t(rowBytes));
    return okRc;
}

// test hook: vlqt_test_res_read_pixels (test/test_probes.cpp).
int testRead(uint64_t id, unsigned char* out, int cap, int* width, int* height, int* written, int* required)
{
    if (const int rc = beginBuffer(out, cap, written, required); rc != okRc)
        return rc;
    if (kindOfId(id, "test read_pixels") < 0) // unknown, stale or released by the host; ready() does not check
        return staleIdRc;
    auto r = ready(id);
    if (!r)
        return noDataRc;
    const int w = r->size().width(), h = r->size().height();
    if (width)
        *width = w;
    if (height)
        *height = h;
    const int n = w * h * 4;
    if (const int rc = bufferFits(n, cap, written, required); rc != okRc)
        return rc;
    // A readback failure below must not leave the counts of a success.
    auto failed = [&](const char* what) {
        beginBuffer(out, cap, written, required);
        return fail(errorRc, std::string("test read_pixels: ") + what);
    };
    // rb outlives result: convertToFormat returns a shallow copy when the format already matches (RGBA8
    // readback), so result may point into rb.data; freed at the end of the else block, the copy below read freed memory.
    QRhiReadbackResult rb;
    QImage result;
    if (!r->texture()) {
        if (r->image().isNull()) // file content whose texture went with a lost device, until its replacement
            return failed("no texture and no image (lost device)");
        result = r->image().convertToFormat(QImage::Format_RGBA8888_Premultiplied);
    } else {
        QRhi* rhi = device::uploadRhi();
        {
            device::ContextLock lock; // as in the stripes upload (image_upload_rhi.cpp)
            QRhiCommandBuffer* cb = nullptr;
            if (rhi->beginOffscreenFrame(&cb) != QRhi::FrameOpSuccess)
                return failed("beginOffscreenFrame failed");
            QRhiResourceUpdateBatch* batch = rhi->nextResourceUpdateBatch();
            batch->readBackTexture(QRhiReadbackDescription(r->texture()), &rb);
            cb->resourceUpdate(batch);
            rhi->endOffscreenFrame(); // waits for the readback
        }
        if (rb.data.size() < qsizetype(n))
            return failed("readback returned too few bytes");
        const qsizetype bpl = rb.data.size() / h;
        const auto* src = reinterpret_cast<const uchar*>(rb.data.constData());
        result = QImage(src, w, h, bpl, rb.format == QRhiTexture::BGRA8 ? QImage::Format_ARGB32_Premultiplied
                                                                       : QImage::Format_RGBA8888_Premultiplied)
                     .convertToFormat(QImage::Format_RGBA8888_Premultiplied);
    }
    for (int y = 0; y < h; y++)
        std::memcpy(out + size_t(w) * 4 * y, result.constScanLine(y), size_t(w) * 4);
    return okRc;
}

} // namespace vlqt::res::image
