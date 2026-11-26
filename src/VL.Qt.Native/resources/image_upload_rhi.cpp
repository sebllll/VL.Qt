// The upload of an image on the main thread: the texture on the upload QRhi, filled in stripes within the frame budget
// (main-thread path) or wrapped around the texture of the worker path (image_upload_d3d11.h).

#include "resources/image_kind.h"
#include "resources/gpu_texture.h"
#include "resources/image_upload_d3d11.h"

#include "device/context_lock.h"
#include "device/device.h"
#include "protocol.h"

#include <QtCore/QElapsedTimer>
#include <rhi/qrhi.h>

#include <algorithm>

namespace vlqt::res::image {

int64_t ImageKind::upload(Content& work, int64_t budget, UploadStatus& status, std::string& error)
{
    auto& r = static_cast<ImageContent&>(work);
    const int64_t used = uploadNow(r, budget, status, error);
#ifdef Q_OS_WIN
    // Removed device: no Failed, the rebuild after checkLost uploads again on the new device.
    if (!error.empty()
        && (D3D11Upload::deviceRemoved(r.m_device) || D3D11Upload::deviceRemoved(device::nativeDevice(device::uploadRhi())))) {
        qCDebug(lcRes, "upload on a removed device (%s), waiting for the rebuild", error.c_str());
        error.clear();
        status = UploadStatus::Waiting;
        return 0;
    }
#endif
    return used;
}

int64_t ImageKind::uploadNow(ImageContent& r, int64_t budget, UploadStatus& status, std::string& error)
{
    const int64_t total = bytes(r);
    QRhi* rhi = device::uploadRhi();
    if (!rhi) { // no shared device: the CPU copy is the image (image())
        status = UploadStatus::Done;
        return total;
    }
    const QRhiTexture::Format format = r.m_bgra ? QRhiTexture::BGRA8 : QRhiTexture::RGBA8;
    QElapsedTimer timer;
    timer.start();
    qint64 createNs = 0;
    bool reused = false;
#ifdef Q_OS_WIN
    // Within the budget (vlqt_res_wait: -1, or a small image) the copy runs here, like a stripe would, else in a task.
    if (r.m_d3d11 && r.m_d3d11->filling()
        && !r.m_d3d11->fill(r.m_image, r.m_path.isEmpty(), budget < 0 || total <= budget, r.cancel, error)) {
        status = UploadStatus::Waiting; // the copy task still runs (other uploads go first), or error
        return 0;
    }
#endif
    if (!r.m_gpu) {
        // Worker texture: it has its mip chain already (the same level count as QRhi's mipLevelsForSize), the
        // flag lets QRhi and the windows' wrappers (resources/image_texture_cache.cpp) see all levels.
        QRhiTexture::Flags flags;
        if (r.m_flags & VLQT_IMAGE_LOAD_MIPMAPS)
            flags = r.m_d3d11 ? QRhiTexture::MipMapped : QRhiTexture::MipMapped | QRhiTexture::UsedWithGenerateMips;
#ifdef Q_OS_WIN
        if (r.m_d3d11) {
            // The worker made the texture: a wrapper around it joins the pool.
            QRhiTexture* wrapper = rhi->newTexture(format, r.m_size, 1, flags);
            if (wrapper->createFrom({quint64(r.m_d3d11->texture()), 0}))
                r.m_gpu = texture_pool::adopt(wrapper);
            else
                delete wrapper;
        }
#endif
        // Stripes: a texture of the same size no window shows any more (content replaced every frame), else a new one.
        if (!r.m_d3d11)
            r.m_gpu = texture_pool::take(rhi, format, r.m_size, flags, reused);
        if (!r.m_gpu) {
            error = "texture " + std::to_string(r.m_size.width()) + "x" + std::to_string(r.m_size.height())
                  + " could not be created";
            return 0;
        }
        createNs = timer.nsecsElapsed();
    }
    QRhiTexture* texture = r.m_gpu->rhiTexture();
    if (r.m_d3d11) { // complete already
        status = UploadStatus::Done;
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
        batch->uploadTexture(texture, QRhiTextureUploadDescription({0, 0, sub}));
        r.m_uploadedRows += rows;
        if (r.m_uploadedRows == h && (texture->flags() & QRhiTexture::MipMapped))
            batch->generateMips(texture);
        cb->resourceUpdate(batch);
        rhi->endOffscreenFrame();
    }
    qCDebug(lcRes, "upload %dx%d rows %d-%d: %.2f ms (%s %.2f ms)", r.m_size.width(), h, r.m_uploadedRows - rows,
        r.m_uploadedRows, timer.nsecsElapsed() / 1e6, reused ? "pooled texture" : "create", createNs / 1e6);
    status = r.m_uploadedRows == h ? UploadStatus::Done : UploadStatus::Progress;
    if (status == UploadStatus::Done)
        finishUpload(r);
    return int64_t(bpl) * rows;
}

void ImageKind::finishUpload(ImageContent& r)
{
    if (!r.m_path.isEmpty())
        r.m_image = QImage();
}

void ImageKind::releaseGpu(ImageContent& r)
{
    // Back to the pool (gpu_texture.h): it is filled again or freed once no window wrapper holds it any more. It holds
    // an own reference on the native texture, so the order against the worker path does not matter.
    r.m_gpu.reset();
    r.m_d3d11.reset();
    r.m_uploadedRows = 0;
}

} // namespace vlqt::res::image
