#include "image_resource.h"

#include "device.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtGui/QColor>
#include <QtGui/QImageReader>
#include <QtGui/QPainter>
#include <rhi/qrhi.h>

#include <algorithm>
#include <chrono>
#include <cstring>

#ifdef Q_OS_WIN
#include <d3d11.h>
#endif

namespace vlqt::res::image {

namespace {

Q_LOGGING_CATEGORY(lcImg, "VL.Qt.Resources")

/* VL PixelFormat (VL.Core PixelFormat.cs, append-only) -> what QtNative does with it. Same table as protocol.h.
 *   direct:              R8G8B8A8 (7), B8G8R8A8 (9), R8G8B8X8 (6), B8G8R8X8 (8)   X: alpha forced to 255
 *   converted to RGBA8:  R8G8B8 (4), B8G8R8 (5), R8 (1, grey)
 *   reduced to 8 bit:    R16 (2, grey), R32F (3, grey, [0,1]), R32G32F (11, R, G, 0), R32G32B32A32F (10),
 *                        R16G16B16A16F (12); one warning per resource
 *   else (Unknown = 0, newer numbers): Failed */
enum PixelFormat
{
    R8 = 1, R16 = 2, R32F = 3, R8G8B8 = 4, B8G8R8 = 5, R8G8B8X8 = 6, R8G8B8A8 = 7, B8G8R8X8 = 8, B8G8R8A8 = 9,
    R32G32B32A32F = 10, R32G32F = 11, R16G16B16A16F = 12
};

// Bytes per pixel of a VL PixelFormat, 0 = unknown.
int bytesPerPixel(int format)
{
    switch (format) {
    case R8:            return 1;
    case R16:           return 2;
    case R8G8B8:
    case B8G8R8:        return 3;
    case R32F:
    case R8G8B8X8:
    case R8G8B8A8:
    case B8G8R8X8:
    case B8G8R8A8:      return 4;
    case R32G32F:
    case R16G16B16A16F: return 8;
    case R32G32B32A32F: return 16;
    default:            return 0;
    }
}

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
// it, 20 x 4K: 22 % hitches), pixels -> stripes in the frame budget (set_pixels per frame). VLQT_IMG_UPLOAD=
// stripes|worker forces one path (A/B). Mipmaps take the worker path too (#562): the texture gets its mip chain in the
// worker (GENERATE_MIPS), the main thread copies level 0 and records GenerateMips, without an offscreen frame (whose
// endOffscreenFrame waited actively for the GPU: 2.2 ms per frame in the profile Images).
[[maybe_unused]] bool workerUpload(bool file)
{
    static const QByteArray s_mode = qgetenv("VLQT_IMG_UPLOAD");
    return s_mode == "worker" || (s_mode != "stripes" && file);
}

#ifdef Q_OS_WIN
// The device (ID3D11Device*) was removed (device lost, #367); thread safe, also in a worker.
bool deviceRemoved(void* device)
{
    return device && static_cast<ID3D11Device*>(device)->GetDeviceRemovedReason() != S_OK;
}
#endif

} // namespace

void* ImageResource::nativeTexture() const
{
    return m_texture ? reinterpret_cast<void*>(m_texture->nativeTexture().object) : nullptr;
}

class ImageKind final : public Kind
{
public:
    const char* name() const override { return "image"; }

    std::string decode(Work& work) override
    {
        auto& r = static_cast<ImageResource&>(work);
        // Pixels restored after a device loss (#367) bring their converted copy along: nothing to convert.
        std::string error = !r.m_path.isEmpty() ? readFile(r) : r.m_image.isNull() ? convertPixels(r) : std::string();
        if (!error.empty() || r.cancel.load())
            return error.empty() ? std::string("cancelled") : error;
        r.m_size = r.m_image.size();
        makePreview(r);
#ifdef Q_OS_WIN
        if (r.m_device && workerUpload(!r.m_path.isEmpty())) {
            // Removed device (#367): no error, the rebuild decodes again for the new device (stale). Mipmaps that the
            // device cannot make this way (#562) fall back to the stripes (generateMips through the upload QRhi).
            if (!createWorkerTextures(r) && !deviceRemoved(r.m_device)) {
                if (!(r.m_flags & VLQT_IMG_MIPMAPS))
                    return "CreateTexture2D failed";
                qCDebug(lcImg, "worker textures with mipmaps failed, uploading in stripes");
            }
        }
#endif
        return {};
    }

    int64_t upload(Work& work, int64_t budget, bool& done, std::string& error) override
    {
        auto& r = static_cast<ImageResource&>(work);
        const int64_t used = uploadNow(r, budget, done, error);
#ifdef Q_OS_WIN
        // Removed device (#367): no Failed, the rebuild after checkLost uploads again on the new device.
        if (!error.empty() && (deviceRemoved(r.m_device) || deviceRemoved(device::nativeDevice(device::uploadRhi())))) {
            qCDebug(lcImg, "upload on a removed device (%s), waiting for the rebuild", error.c_str());
            error.clear();
            done = false;
            return 0;
        }
#endif
        return used;
    }

    void destroy(Work& work) override
    {
        releaseDevice(work);
    }

    // Device lost (#367): the textures, the staging texture and a running copy go, the source stays.
    void releaseDevice(Work& work) override
    {
        auto& r = static_cast<ImageResource&>(work);
        if (r.m_texture) {
            r.m_texture->destroy();
            delete r.m_texture;
            r.m_texture = nullptr;
        }
#ifdef Q_OS_WIN
        releaseStaging(r);
        if (r.m_workerSrv) {
            static_cast<ID3D11ShaderResourceView*>(r.m_workerSrv)->Release();
            r.m_workerSrv = nullptr;
        }
        if (r.m_workerTexture) {
            static_cast<ID3D11Texture2D*>(r.m_workerTexture)->Release();
            r.m_workerTexture = nullptr;
        }
#endif
        r.m_uploadedRows = 0;
    }

    // Files: the path again (decoded anew, the CPU copy is gone after the upload); pixels: the converted copy (and its
    // raw buffer, the copy may point into it), only uploaded again.
    std::shared_ptr<Work> restore(const Work& work) override
    {
        const auto& old = static_cast<const ImageResource&>(work);
        auto r = std::make_shared<ImageResource>();
        r->m_path = old.m_path;
        r->m_flags = old.m_flags;
        r->m_preview = old.m_preview; // same content: no new scaling (implicitly shared, read-only after Ready, #546)
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

    bool stale(const Work& work) const override
    {
        return static_cast<const ImageResource&>(work).m_generation != device::generation();
    }

    Info info(const Work& work) const override
    {
        const auto& r = static_cast<const ImageResource&>(work);
        return {uint32_t(r.m_size.width()), uint32_t(r.m_size.height()), r.format()};
    }

    // F5 Resources page (#364): rows not yet uploaded (a worker texture goes up in one call: all of it until then).
    int64_t pendingBytes(const Work& work) const override
    {
        const auto& r = static_cast<const ImageResource&>(work);
        if (r.m_workerTexture)
            return bytes(r);
        const int rows = (std::max)(0, r.m_image.height() - r.m_uploadedRows); // windows.h max macro
        return int64_t(r.m_image.bytesPerLine()) * rows;
    }

    std::string takeWarning(Work& work) override
    {
        return std::move(static_cast<ImageResource&>(work).m_warning);
    }

private:
    static int64_t uploadNow(ImageResource& r, int64_t budget, bool& done, std::string& error)
    {
        const int64_t total = bytes(r);
        QRhi* rhi = device::uploadRhi();
        if (!rhi) { // no shared device: the CPU copy is the image (image())
            done = true;
            return total;
        }
        const QRhiTexture::Format format = r.m_bgra ? QRhiTexture::BGRA8 : QRhiTexture::RGBA8;
        QElapsedTimer timer;
        timer.start();
        qint64 createNs = 0;
#ifdef Q_OS_WIN
        // Within the budget (vlqt_res_wait: -1, or a small image) the copy runs here, like a stripe would, else in a task.
        if (r.m_staging && !fillFromStaging(r, budget < 0 || total <= budget, error))
            return 0; // the copy task still runs (next pump), or error
#endif
        if (!r.m_texture) {
            // Worker texture: it has its mip chain already (#562, the same level count as QRhi's mipLevelsForSize), the
            // flag lets QRhi and the windows' wrappers (qtimageitem.cpp) see all levels.
            QRhiTexture::Flags flags;
            if (r.m_flags & VLQT_IMG_MIPMAPS)
                flags = r.m_workerTexture ? QRhiTexture::MipMapped : QRhiTexture::MipMapped | QRhiTexture::UsedWithGenerateMips;
            r.m_texture = rhi->newTexture(format, r.m_size, 1, flags);
            const bool ok = r.m_workerTexture ? r.m_texture->createFrom({quint64(r.m_workerTexture), 0})
                                              : r.m_texture->create();
            if (!ok) {
                error = "texture " + std::to_string(r.m_size.width()) + "x" + std::to_string(r.m_size.height())
                      + " could not be created";
                return 0;
            }
            createNs = timer.nsecsElapsed();
        }
        if (r.m_workerTexture) { // complete already
            done = true;
            finishUpload(r);
            return total;
        }

        const int h = r.m_size.height();
        const qsizetype bpl = r.m_image.bytesPerLine();
        const int left = h - r.m_uploadedRows;
        const int rows = budget < 0 ? left : int(std::clamp<int64_t>(budget / bpl, 1, left));
        {
            // The upload QRhi's frame executes its commands on the context shared with the render threads (device.h).
            device::ContextLock lock;
            QRhiCommandBuffer* cb = nullptr;
            if (rhi->beginOffscreenFrame(&cb) != QRhi::FrameOpSuccess) {
                error = "upload: offscreen frame failed (device lost?)";
                return 0;
            }
            QRhiResourceUpdateBatch* batch = rhi->nextResourceUpdateBatch();
            const auto* bits = reinterpret_cast<const char*>(r.m_image.constBits()) + bpl * r.m_uploadedRows;
            QRhiTextureSubresourceUploadDescription sub(QByteArray::fromRawData(bits, bpl * rows));
            sub.setDataStride(quint32(bpl));
            sub.setSourceSize(QSize(r.m_size.width(), rows));
            sub.setDestinationTopLeft(QPoint(0, r.m_uploadedRows));
            batch->uploadTexture(r.m_texture, QRhiTextureUploadDescription({0, 0, sub}));
            r.m_uploadedRows += rows;
            if (r.m_uploadedRows == h && (r.m_texture->flags() & QRhiTexture::MipMapped))
                batch->generateMips(r.m_texture);
            cb->resourceUpdate(batch);
            rhi->endOffscreenFrame();
        }
        qCDebug(lcImg, "upload %dx%d rows %d-%d: %.2f ms (create %.2f ms)", r.m_size.width(), h, r.m_uploadedRows - rows,
            r.m_uploadedRows, timer.nsecsElapsed() / 1e6, createNs / 1e6);
        if (r.m_uploadedRows == h) {
            done = true;
            finishUpload(r);
        }
        return int64_t(bpl) * rows;
    }

#ifdef Q_OS_WIN
    // Worker upload path, worker side: an empty texture and an empty staging texture (CPU write) of the image size.
    // No initial data (#390): CreateTexture2D with initial data blocks the main thread's context calls for as long as
    // it runs (arm64 Adreno: 4K 5-45 ms, every pump that overlaps it is a hitch, 4 MB stripes as well); empty creates
    // take < 1 ms and block nothing. Mipmaps (#562): the texture has the full chain (render target + GENERATE_MIPS,
    // like QRhi's UsedWithGenerateMips) and a shader resource view over it for GenerateMips; the staging texture holds
    // level 0 only. Returns false (all released) on an error.
    static bool createWorkerTextures(ImageResource& r)
    {
        const bool mips = (r.m_flags & VLQT_IMG_MIPMAPS) != 0;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = UINT(r.m_image.width());
        desc.Height = UINT(r.m_image.height());
        desc.MipLevels = mips ? mipLevels(r.m_image.size()) : 1;
        desc.ArraySize = 1;
        desc.Format = r.m_bgra ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (mips ? D3D11_BIND_RENDER_TARGET : 0);
        desc.MiscFlags = mips ? D3D11_RESOURCE_MISC_GENERATE_MIPS : 0;
        auto* device = static_cast<ID3D11Device*>(r.m_device);
        ID3D11Texture2D* tex = nullptr;
        ID3D11Texture2D* staging = nullptr;
        ID3D11ShaderResourceView* srv = nullptr;
        if (SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &tex))) {
            if (!mips || SUCCEEDED(device->CreateShaderResourceView(tex, nullptr, &srv))) {
                desc.MipLevels = 1;
                desc.Usage = D3D11_USAGE_STAGING;
                desc.BindFlags = 0;
                desc.MiscFlags = 0;
                desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
                if (SUCCEEDED(device->CreateTexture2D(&desc, nullptr, &staging))) {
                    r.m_workerTexture = tex;
                    r.m_workerSrv = srv;
                    r.m_staging = staging;
                    return true;
                }
                if (srv)
                    srv->Release();
            }
            tex->Release();
        }
        return false;
    }

    // Levels of a full mip chain, as QRhi::mipLevelsForSize (QRhiTexture::MipMapped on the wrapper must match).
    static UINT mipLevels(QSize size)
    {
        UINT levels = 1;
        for (int edge = (std::max)(size.width(), size.height()); edge > 1; edge >>= 1) // windows.h max macro
            levels++;
        return levels;
    }

    // Copy task (thread pool): the pixels into the mapped staging texture, row by row (the row pitches differ).
    static void copyToStaging(const QImage& img, D3D11_MAPPED_SUBRESOURCE map, const std::atomic<bool>& cancel)
    {
        const auto* src = img.constBits();
        const qsizetype bpl = img.bytesPerLine();
        const size_t row = size_t(img.width()) * 4;
        for (int y = 0; y < img.height(); y++) {
            if ((y & 63) == 0 && cancel.load())
                return;
            std::memcpy(static_cast<uchar*>(map.pData) + size_t(y) * map.RowPitch, src + y * bpl, row);
        }
    }

    // Main thread: maps the staging texture and starts the copy task (sync: runs it here), then, once it is done, Unmap
    // + CopyResource into the texture. True when the texture holds the pixels; false while the copy task runs or on an
    // error (error set).
    static bool fillFromStaging(ImageResource& r, bool sync, std::string& error)
    {
        auto* staging = static_cast<ID3D11Texture2D*>(r.m_staging);
        ID3D11DeviceContext* ctx = nullptr;
        static_cast<ID3D11Device*>(r.m_device)->GetImmediateContext(&ctx);
        if (!r.m_mapped) {
            D3D11_MAPPED_SUBRESOURCE map{};
            const HRESULT hr = ctx->Map(staging, 0, D3D11_MAP_WRITE, 0, &map);
            if (FAILED(hr)) {
                ctx->Release();
                error = "upload: Map of the staging texture failed (hr " + std::to_string(long(hr)) + ")";
                return false;
            }
            r.m_mapped = true;
            // Files drop the CPU copy after the upload (finishUpload): the task takes it and frees it there (4K: 2-9 ms
            // on the main thread on arm64). Pixels keep theirs (shared, read only).
            QImage img = r.m_path.isEmpty() ? r.m_image : std::move(r.m_image);
            r.m_copy = std::async(sync ? std::launch::deferred : std::launch::async, [&r, img = std::move(img), map]() mutable {
                copyToStaging(img, map, r.cancel);
                img = QImage();
            });
        }
        if (!sync && r.m_copy.wait_for(std::chrono::seconds(0)) != std::future_status::ready) {
            ctx->Release();
            return false;
        }
        r.m_copy.get();
        // Unmap and the GPU copy (+ GenerateMips) as one sequence on the context shared with the render threads.
        device::ContextLock lock(ctx);
        ctx->Unmap(staging, 0);
        r.m_mapped = false;
        auto* tex = static_cast<ID3D11Texture2D*>(r.m_workerTexture);
        if (auto* srv = static_cast<ID3D11ShaderResourceView*>(r.m_workerSrv)) {
            // Mipmaps (#562): level 0 from the staging texture, the rest on the GPU. Both are only recorded on the
            // immediate context (no wait, unlike the offscreen frame of the stripes); the windows draw on the same
            // context afterwards, so they see the finished chain.
            ctx->CopySubresourceRegion(tex, 0, 0, 0, 0, staging, 0, nullptr);
            ctx->GenerateMips(srv);
            srv->Release(); // the runtime keeps it alive for the recorded GenerateMips
            r.m_workerSrv = nullptr;
        } else {
            ctx->CopyResource(tex, staging);
        }
        ctx->Release();
        releaseInPool(staging); // the runtime keeps it alive until the GPU copy has run
        r.m_staging = nullptr;
        return true;
    }

    // Releases a D3D object in the thread pool: freeing a 4K staging texture takes 1-9 ms on arm64.
    static void releaseInPool(IUnknown* object)
    {
        const auto release = [](PTP_CALLBACK_INSTANCE, void* p) { static_cast<IUnknown*>(p)->Release(); };
        if (!TrySubmitThreadpoolCallback(release, object, nullptr))
            object->Release();
    }

    // Release on the main thread: waits for a running copy task (it writes into the mapped staging texture).
    static void releaseStaging(ImageResource& r)
    {
        if (r.m_copy.valid())
            r.m_copy.wait();
        auto* staging = static_cast<ID3D11Texture2D*>(r.m_staging);
        if (!staging)
            return;
        if (r.m_mapped) {
            ID3D11DeviceContext* ctx = nullptr;
            static_cast<ID3D11Device*>(r.m_device)->GetImmediateContext(&ctx);
            ctx->Unmap(staging, 0);
            ctx->Release();
            r.m_mapped = false;
        }
        staging->Release();
        r.m_staging = nullptr;
    }
#endif

    // Bytes of the texture (8 bit, 4 channels); m_image may be gone already (worker path, files).
    static int64_t bytes(const ImageResource& r) { return int64_t(r.m_size.width()) * 4 * r.m_size.height(); }

    // Files keep only the path (re-upload after a device loss reads the file again); pixels keep the converted copy.
    static void finishUpload(ImageResource& r)
    {
        if (!r.m_path.isEmpty())
            r.m_image = QImage();
    }

    // Tooltip preview (#546): longest edge <= 256 px (never upscaled), BGRA8 premultiplied. Made right after m_image is
    // set (files: readFile, pixels: convertPixels, restored pixels: the copied m_image), before the upload drops m_image;
    // m_preview lives until the Work is destroyed. A deep copy: m_image may share its data with m_raw / the file buffer.
    // A restore (#367) takes the preview of the old Work over and skips this. Large images first go down to 4 x 256 px
    // with the fast filter (a single smooth step from 8k costs tens of ms in the worker, which a synchronous LoadImage
    // waits for), then smooth to 256.
    static void makePreview(ImageResource& r)
    {
        if (!(r.m_flags & VLQT_IMG_PREVIEW) || r.m_image.isNull() || !r.m_preview.isNull())
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

    static std::string readFile(ImageResource& r)
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

    static std::string convertPixels(ImageResource& r)
    {
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
        switch (fmt) {
        case R8G8B8X8:
            forceAlpha(r.m_raw);
            img = QImage(d, w, h, bpl, QImage::Format_RGBA8888_Premultiplied);
            keepRaw = true;
            break;
        case R8G8B8A8:
            img = QImage(d, w, h, bpl, pm ? QImage::Format_RGBA8888_Premultiplied : QImage::Format_RGBA8888);
            keepRaw = pm;
            if (!pm)
                img = img.convertToFormat(QImage::Format_RGBA8888_Premultiplied);
            break;
        case B8G8R8X8:
            forceAlpha(r.m_raw);
            img = QImage(d, w, h, bpl, QImage::Format_ARGB32_Premultiplied);
            keepRaw = true;
            r.m_bgra = true;
            break;
        case B8G8R8A8:
            img = QImage(d, w, h, bpl, pm ? QImage::Format_ARGB32_Premultiplied : QImage::Format_ARGB32);
            keepRaw = pm;
            if (!pm)
                img = img.convertToFormat(QImage::Format_ARGB32_Premultiplied);
            r.m_bgra = true;
            break;
        case R8G8B8:
            img = QImage(d, w, h, bpl, QImage::Format_RGB888).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
            break;
        case B8G8R8:
            img = QImage(d, w, h, bpl, QImage::Format_BGR888).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
            break;
        case R8:
            img = QImage(d, w, h, bpl, QImage::Format_Grayscale8).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
            break;
        case R16:
            reduced("R16");
            img = QImage(d, w, h, bpl, QImage::Format_Grayscale16).convertToFormat(QImage::Format_RGBA8888_Premultiplied);
            break;
        case R16G16B16A16F:
            reduced("R16G16B16A16F");
            img = QImage(d, w, h, bpl, pm ? QImage::Format_RGBA16FPx4_Premultiplied : QImage::Format_RGBA16FPx4)
                      .convertToFormat(QImage::Format_RGBA8888_Premultiplied);
            break;
        case R32G32B32A32F:
            reduced("R32G32B32A32F");
            img = QImage(d, w, h, bpl, pm ? QImage::Format_RGBA32FPx4_Premultiplied : QImage::Format_RGBA32FPx4)
                      .convertToFormat(QImage::Format_RGBA8888_Premultiplied);
            break;
        case R32F:
        case R32G32F: {
            reduced(fmt == R32F ? "R32F" : "R32G32F");
            img = QImage(w, h, QImage::Format_RGBA8888_Premultiplied);
            for (int y = 0; y < h; y++) {
                const auto* src = reinterpret_cast<const float*>(d + bpl * y);
                uchar* dst = img.scanLine(y);
                for (int x = 0; x < w; x++, dst += 4) {
                    if (fmt == R32F) {
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
            r.m_raw = QByteArray(); // the converted image is the CPU copy now
        r.m_image = std::move(img);
        return {};
    }
};

Kind* kind()
{
    static ImageKind s_kind;
    return &s_kind;
}

int load(uint64_t id, const char* utf8Path, unsigned flags)
{
    if (!utf8Path || !*utf8Path) {
        qCWarning(lcImg, "load_image: empty path");
        return badValueRc;
    }
    auto r = std::make_shared<ImageResource>();
    r->m_path = QString::fromUtf8(utf8Path);
    r->m_flags = flags;
    r->m_device = device::nativeDevice(device::uploadRhi());
    r->m_generation = device::generation();
    return start(id, std::move(r), VLQT_RES_KIND_IMAGE);
}

int setPixels(uint64_t id, const vlqt_pixel_desc* desc, const void* data, int len)
{
    if (!desc || !data || desc->width <= 0 || desc->height <= 0) {
        qCWarning(lcImg, "set_pixels: %s", !desc ? "null desc" : !data ? "null data" : "size <= 0");
        return badValueRc;
    }
    auto r = std::make_shared<ImageResource>();
    r->m_flags = desc->flags;
    r->m_rawWidth = desc->width;
    r->m_rawHeight = desc->height;
    r->m_rawFormat = desc->format;
    r->m_rawPremultiplied = desc->premultiplied != 0;
    r->m_device = device::nativeDevice(device::uploadRhi());
    r->m_generation = device::generation();
    // Unknown formats are copied not at all and fail in the decode (Failed with text, like a broken file).
    const int bpp = bytesPerPixel(desc->format);
    if (bpp > 0) {
        const qsizetype row = qsizetype(desc->width) * bpp;
        const qsizetype stride = desc->row_bytes > 0 ? desc->row_bytes : row;
        if (stride < row || qsizetype(len) < stride * (desc->height - 1) + row) {
            qCWarning(lcImg, "set_pixels: %d bytes do not hold %dx%d pixels of format %d with %d row bytes", len,
                desc->width, desc->height, desc->format, desc->row_bytes);
            return badValueRc;
        }
        r->m_raw.resize(row * desc->height);
        const auto* src = static_cast<const char*>(data);
        if (stride == row) {
            std::memcpy(r->m_raw.data(), src, size_t(row) * desc->height);
        } else {
            for (int y = 0; y < desc->height; y++)
                std::memcpy(r->m_raw.data() + row * y, src + stride * y, size_t(row));
        }
    }
    return start(id, std::move(r), VLQT_RES_KIND_IMAGE);
}

std::shared_ptr<ImageResource> ready(uint64_t id)
{
    return std::static_pointer_cast<ImageResource>(readyWork(id, VLQT_RES_KIND_IMAGE));
}

int readPreview(uint64_t id, unsigned char* dst, int cap, vlqt_pixel_desc* desc)
{
    const int k = kindOfId(id, "read_preview");
    if (k < 0)
        return k;
    if (k != VLQT_RES_KIND_IMAGE) {
        qCWarning(lcImg, "read_preview: id %llu is not an image resource", static_cast<unsigned long long>(id));
        return badValueRc;
    }
    // Replacement of a Ready resource: readyWork is the visible content (the replacement is the separate Slot::next
    // until its swap), so the preview always belongs to what ready() shows.
    const auto r = ready(id);
    if (!r || r->m_preview.isNull())
        return badValueRc; // not Ready yet / Failed / loaded without VLQT_IMG_PREVIEW
    // No lock: the worker wrote m_preview in decode, before the Ready transition (made under the manager's mutex,
    // which readyWork takes as well); afterwards nothing touches it until the Work is destroyed.
    const QImage& img = r->m_preview;
    const int rowBytes = img.width() * 4;
    const int64_t required = int64_t(rowBytes) * img.height();
    if (desc) {
        desc->width = img.width();
        desc->height = img.height();
        desc->row_bytes = rowBytes;
        desc->format = 9; // B8G8R8A8
        desc->premultiplied = 1;
        desc->flags = 0;
    }
    if (!dst || cap < required)
        return -int(required);
    for (int y = 0; y < img.height(); y++)
        std::memcpy(dst + size_t(y) * rowBytes, img.constScanLine(y), size_t(rowBytes));
    return int(required);
}

int testRead(uint64_t id, unsigned char* out, int cap, int* width, int* height)
{
    if (kindOfId(id, "test read_pixels") < 0) // unknown, stale or released by the host (#365); ready() does not check
        return staleIdRc;
    auto r = ready(id);
    if (!r)
        return staleIdRc;
    const int w = r->m_size.width(), h = r->m_size.height();
    if (width)
        *width = w;
    if (height)
        *height = h;
    const int n = w * h * 4;
    if (!out || cap < n)
        return -n;
    // rb outlives result (#387): convertToFormat returns a shallow copy when the format already matches (RGBA8
    // readback), so result may point into rb.data; freed at the end of the else block, the copy below read freed memory.
    QRhiReadbackResult rb;
    QImage result;
    if (!r->m_texture) {
        if (r->m_image.isNull()) // file content whose texture went with a lost device, until its replacement (#367)
            return errorRc;
        result = r->m_image.convertToFormat(QImage::Format_RGBA8888_Premultiplied);
    } else {
        QRhi* rhi = device::uploadRhi();
        {
            device::ContextLock lock; // as in uploadNow
            QRhiCommandBuffer* cb = nullptr;
            if (rhi->beginOffscreenFrame(&cb) != QRhi::FrameOpSuccess)
                return errorRc;
            QRhiResourceUpdateBatch* batch = rhi->nextResourceUpdateBatch();
            batch->readBackTexture(QRhiReadbackDescription(r->m_texture), &rb);
            cb->resourceUpdate(batch);
            rhi->endOffscreenFrame(); // waits for the readback
        }
        if (rb.data.size() < qsizetype(n))
            return errorRc;
        const qsizetype bpl = rb.data.size() / h;
        const auto* src = reinterpret_cast<const uchar*>(rb.data.constData());
        result = QImage(src, w, h, bpl, rb.format == QRhiTexture::BGRA8 ? QImage::Format_ARGB32_Premultiplied
                                                                       : QImage::Format_RGBA8888_Premultiplied)
                     .convertToFormat(QImage::Format_RGBA8888_Premultiplied);
    }
    for (int y = 0; y < h; y++)
        std::memcpy(out + size_t(w) * 4 * y, result.constScanLine(y), size_t(w) * 4);
    return n;
}

int testWrite(const char* utf8Path, int width, int height, int quality)
{
    if (!utf8Path || width <= 0 || height <= 0)
        return badValueRc;
    QImage img(width, height, QImage::Format_RGB32);
    QPainter p(&img);
    const QColor colors[] = {Qt::red, Qt::green, Qt::blue, Qt::white};
    for (int i = 0; i < 4; i++)
        p.fillRect(QRect(width * i / 4, 0, width * (i + 1) / 4 - width * i / 4, height), colors[i]);
    p.end();
    return img.save(QString::fromUtf8(utf8Path), nullptr, quality) ? okRc : errorRc;
}

} // namespace vlqt::res::image
