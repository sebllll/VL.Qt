/* image_texture_cache.h - the texture of an image resource (image_resource.h) in a window, for the Image element
 * (qt_image_item.h) and the image samplers of the Shader item (effects/shader_item.h).
 *
 * One texture per window, QRhi and source, shared by every item of that window; the cache holds weak references, the
 * items drop theirs in releaseResources/invalidateSceneGraph before the window's QRhi goes. A texture of another QRhi
 * than window->rhi() is never returned. Shared device (Windows/D3D11): a QRhiTexture created from the native texture of
 * the resource, no copy. Otherwise createTextureFromImage of the CPU copy. Content of an older device generation gets
 * no texture until its replacement is Ready. Threads: the thread that renders the window (see qt_image_item.h).
 * Why the QRhi is in the key, who owns the native texture and what happens on a device loss: docs/design/resources.md,
 * "Images" and "Device loss".
 */
#pragma once

#include <memory>

class QQuickWindow;
class QRhi;
class QSGTexture;
class QThread;

namespace vlqt {

namespace res::image {
class GpuTexture;
class ImageContent;
}

// Texture of one content in one window and QRhi, shared by every item there.
struct ImageTexture
{
    QQuickWindow* window = nullptr;
    QRhi* rhi = nullptr; // window->rhi() when made: a wrapper belongs to it
    const void* source = nullptr; // the GpuTexture (shared device) or the content (CPU copy); held below, so unique
    std::shared_ptr<res::image::GpuTexture> gpu;       // shared device: the texture the wrapper reads, released after it
    std::shared_ptr<res::image::ImageContent> content; // CPU copy: its pixels, released after the texture
    QSGTexture* texture = nullptr;
    bool mipmaps = false;
    QThread* thread = nullptr; // the window's render thread (made in updatePaintNode), frees it

    ImageTexture() = default;
    ImageTexture(const ImageTexture&) = delete;
    ImageTexture& operator=(const ImageTexture&) = delete;
    ~ImageTexture();

    // Returns true if this texture draws content in window now (same window, its current QRhi, same source).
    bool fits(const QQuickWindow* w, const std::shared_ptr<res::image::ImageContent>& c) const;
};

// Texture of content in window from the cache, made on a miss; nullptr if it cannot be drawn there (see above).
std::shared_ptr<ImageTexture> imageTextureFor(QQuickWindow* window, const std::shared_ptr<res::image::ImageContent>& content);

// Drops the holder's reference where the texture's window renders (render_thread::retire), and leaves t empty. Main
// thread; on the window's render thread (updatePaintNode, invalidateSceneGraph) a plain reset is right.
void retireImageTexture(std::shared_ptr<ImageTexture>& t);

} // namespace vlqt
