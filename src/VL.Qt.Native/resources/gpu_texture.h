/* gpu_texture.h - a texture on the process device (device/device.h) shared by the image content that filled it, the
 * texture pool and the window wrappers (image_texture_cache.h), and the pool that fills it again for content replaced
 * every frame (set_pixels of a video): no texture created and destroyed per frame, no first write into fresh GPU memory.
 *
 * Ownership: the pool owns every GpuTexture (shared_ptr), also the ones of the worker path (adopt). An ImageContent
 * holds the one it filled, a window wrapper (ImageTexture) the one it wraps. A texture is free when only the pool holds
 * it (use_count 1): no content and no window shows it any more, so filling it again cannot change a picture on screen.
 * The pool keeps at most two free textures per size, format and flags, none longer than about a second (endFrame).
 *
 * Threads: the pool, take, adopt and the QRhiTexture of the upload QRhi: main thread. A window wrapper lets its
 * reference go on the window's render thread; while the pool holds one that is only a count, so the QRhiTexture is
 * deleted only by the pool, on the main thread. The native texture has an own reference (Windows: AddRef), released by
 * the last holder on any thread (a COM Release is thread safe).
 *
 * Device lost: the pool deletes the QRhiTexture of every texture (device::rebuild, before the upload QRhi goes) and
 * forgets them all. A wrapper may still hold one: it keeps only the native reference until it lets it go.
 */
#pragma once

#include <QtCore/QSize>
#include <rhi/qrhi.h>

#include <memory>

namespace vlqt::res::image {

class GpuTexture final
{
public:
    // Takes rhiTexture (created on the upload QRhi) over and holds an own reference on its native texture.
    explicit GpuTexture(QRhiTexture* rhiTexture);
    // The pool releases the QRhiTexture before (main thread); afterwards only the native reference goes.
    ~GpuTexture();
    GpuTexture(const GpuTexture&) = delete;
    GpuTexture& operator=(const GpuTexture&) = delete;

    // The texture on the upload QRhi; nullptr after a device loss.
    QRhiTexture* rhiTexture() const { return m_rhiTexture; }
    // ID3D11Texture2D* for D3D11 (own reference, valid as long as this object), nullptr on other backends.
    void* native() const { return m_native; }

    // Main thread: deletes the QRhiTexture (the pool, also at a device loss).
    void releaseRhiTexture();

private:
    QRhiTexture* m_rhiTexture = nullptr;
    void* m_native = nullptr;
};

namespace texture_pool {

// Main thread: a free texture of this format, size and flags on rhi (reused = true), else a new one; nullptr if it
// cannot be created. Its content is undefined: the caller fills all of it before anyone shows it.
std::shared_ptr<GpuTexture> take(QRhi* rhi, QRhiTexture::Format format, QSize size, QRhiTexture::Flags flags, bool& reused);

// Main thread: a texture created elsewhere (createFrom around the texture of the worker path) joins the pool.
std::shared_ptr<GpuTexture> adopt(QRhiTexture* created);

// Main thread, once per pump: frees free textures beyond two per key and the ones free for about a second.
void endFrame();

} // namespace texture_pool

} // namespace vlqt::res::image
