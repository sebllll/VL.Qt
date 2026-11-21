/* image_texture_cache.h - the texture of an image resource (image_resource.h) in a window, for the Image element
 * (qt_image_item.h) and the image samplers of the Shader item (effects/shader_item.h).
 *
 * One texture per window, QRhi and content, shared by every item of that window. The cache holds weak references:
 * an entry lives as long as an item holds it, and the items drop theirs in releaseResources/invalidateSceneGraph,
 * before the window's QRhi goes. The QRhi is part of the key because Qt replaces a window's QRhi after a device loss
 * while the QQuickWindow stays: a texture of another QRhi than window->rhi() is never returned.
 *   - shared device (Windows/D3D11): a QRhiTexture of the window's QRhi created from the resource's native texture
 *     (createFrom, same format and mip flags), no copy. The native texture gets an own reference (AddRef) for as long
 *     as the wrapper lives, so a resource freed or replaced meanwhile never leaves a dangling texture.
 *   - no shared device (Linux, VLQT_SHARED_DEVICE=0) or no QRhi (software backend): createTextureFromImage of the
 *     resource's CPU copy, once per window.
 *
 * Device lost: content of an older device generation (ImageResource::generation) gets no texture until its
 * replacement is Ready; neither does a window on the shared device that still renders with its old QRhi right after
 * device::rebuild. A window that is not on the shared device draws no image resources (warning once per window).
 *
 * Threads: the thread that renders the window, the main thread waiting meanwhile (updatePaintNode, see
 * qt_image_item.h). A texture belongs to the thread that made it; a holder on the main thread lets it go through
 * render_thread::retire.
 */
#pragma once

#include <memory>

class QQuickWindow;
class QRhi;
class QSGTexture;
class QThread;

namespace vlqt {

namespace res::image {
class ImageResource;
}

// Texture of one content in one window and QRhi, shared by every item there.
struct ImageTexture
{
    QQuickWindow* window = nullptr;
    QRhi* rhi = nullptr; // window->rhi() when made: a wrapper belongs to it
    std::shared_ptr<res::image::ImageResource> content; // keeps the address unique while the entry lives
    QSGTexture* texture = nullptr;
    bool mipmaps = false;
    void* native = nullptr; // own reference on the native texture (shared device), released after the wrapper
    QThread* thread = nullptr; // the window's render thread (made in updatePaintNode), frees it

    ImageTexture() = default;
    ImageTexture(const ImageTexture&) = delete;
    ImageTexture& operator=(const ImageTexture&) = delete;
    ~ImageTexture();

    // Returns true if this texture draws content in window now (same window, its current QRhi, same content).
    bool fits(const QQuickWindow* w, const std::shared_ptr<res::image::ImageResource>& c) const;
};

// Texture of content in window from the cache, made on a miss; nullptr if it cannot be drawn there (see above).
std::shared_ptr<ImageTexture> imageTextureFor(QQuickWindow* window, const std::shared_ptr<res::image::ImageResource>& content);

} // namespace vlqt
