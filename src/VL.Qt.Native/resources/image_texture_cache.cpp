// Private Qt API: QSGPlainTexture (around the QRhiTexture wrapping a shared texture); check on Qt updates (list: elements/elements.h).
#include "resources/image_texture_cache.h"

#include "device/device.h"
#include "device/render_thread.h"
#include "resources/gpu_texture.h"
#include "resources/image_resource.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QThread>
#include <QtQuick/QQuickWindow>
#include <QtQuick/private/qsgplaintexture_p.h>
#include <rhi/qrhi.h>

#include <unordered_set>
#include <vector>

Q_LOGGING_CATEGORY(lcImageTexture, "VL.Qt.Image.Item")

namespace vlqt {

namespace {

std::vector<std::weak_ptr<ImageTexture>> s_textures; // updatePaintNode: one window syncs at a time, the main thread waits
std::unordered_set<const QQuickWindow*> s_warned;     // windows told they are not on the shared device

// What a window texture of content is made from: its shared texture, else the content itself (CPU copy).
const void* sourceOf(const res::image::ImageContent& content)
{
    return content.gpuTexture() ? static_cast<const void*>(content.gpuTexture().get()) : &content;
}

// Warns once per window that it draws no image resources.
void warnNotShared(QQuickWindow* window)
{
    if (!s_warned.insert(window).second)
        return;
    QObject::connect(window, &QObject::destroyed, [window] { s_warned.erase(window); });
    qCWarning(lcImageTexture, "window %p is not on the shared device: image resources are not drawn there",
        static_cast<void*>(window));
}

std::shared_ptr<ImageTexture> createTexture(QQuickWindow* window, const std::shared_ptr<res::image::ImageContent>& content)
{
    // Content of an older device generation (device lost): its texture is gone, the replacement follows (the
    // item gets it through res::changedThisFrame). Never wrap it, never fall back to the CPU copy.
    if (content->generation() != device::generation())
        return nullptr;
    QRhi* rhi = window->rhi();
    auto t = std::make_shared<ImageTexture>();
    t->window = window;
    t->rhi = rhi;
    t->thread = QThread::currentThread();
    t->source = sourceOf(*content);
    t->gpu = content->gpuTexture(); // keeps the source unique (CPU copy: content below)
    if (QRhiTexture* src = content->texture()) {
        // Shared device: a wrapper of the native texture in the window's QRhi (same device, see device.h).
        void* native = content->nativeTexture();
        void* dev = device::nativeDevice(rhi);
        if (!native || !dev || dev != device::nativeDevice(device::uploadRhi())) {
            // A window moved to the new device renders once more with its old QRhi before Qt notices the loss
            // (device::rebuild): skip silently, Qt rebuilds the scene graph afterwards.
            if (!device::isOnSharedDevice(window))
                warnNotShared(window);
            return nullptr;
        }
        const auto flags = src->flags() & (QRhiTexture::MipMapped | QRhiTexture::UsedWithGenerateMips);
        QRhiTexture* wrapper = rhi->newTexture(src->format(), src->pixelSize(), 1, flags);
        if (!wrapper->createFrom({quint64(native), 0})) {
            delete wrapper;
            qCWarning(lcImageTexture, "wrapping texture %p for window %p failed", native, static_cast<void*>(window));
            return nullptr;
        }
        auto* plain = new QSGPlainTexture;
        plain->setOwnsTexture(true);
        plain->setTexture(wrapper);
        plain->setTextureSize(src->pixelSize());
        plain->setHasAlphaChannel(true);
        t->texture = plain;
        t->mipmaps = (flags & QRhiTexture::MipMapped) != 0;
    } else if (!content->image().isNull()) {
        // No shared device (Linux, VLQT_SHARED_DEVICE=0, software backend): the CPU copy, once per window.
        t->texture = window->createTextureFromImage(content->image(), QQuickWindow::TextureHasAlphaChannel);
        t->content = content;
    }
    if (!t->texture)
        return nullptr;
    qCDebug(lcImageTexture, "texture %dx%d for window %p, QRhi %p (%s)", t->texture->textureSize().width(),
        t->texture->textureSize().height(), static_cast<void*>(window), static_cast<void*>(rhi),
        t->gpu ? "shared" : "copy");
    return t;
}

} // namespace

ImageTexture::~ImageTexture()
{
    // Another thread may run a frame with it on the window's QRhi right now (render_thread::retire).
    if (texture && QThread::currentThread() != thread)
        qCWarning(lcImageTexture, "texture of window %p freed off its render thread", static_cast<void*>(window));
    // Owns its QRhiTexture (wrapper only, the native texture is not destroyed by it); gpu and content go after it.
    delete texture;
}

bool ImageTexture::fits(const QQuickWindow* w, const std::shared_ptr<res::image::ImageContent>& c) const
{
    return window == w && c && source == sourceOf(*c) && rhi == w->rhi();
}

std::shared_ptr<ImageTexture> imageTextureFor(QQuickWindow* window, const std::shared_ptr<res::image::ImageContent>& content)
{
    if (!window || !content)
        return nullptr;
    for (auto it = s_textures.begin(); it != s_textures.end();) {
        auto t = it->lock();
        if (!t) {
            it = s_textures.erase(it);
            continue;
        }
        if (t->fits(window, content))
            return t;
        ++it;
    }
    auto t = createTexture(window, content);
    if (t)
        s_textures.push_back(t);
    return t;
}

void retireImageTexture(std::shared_ptr<ImageTexture>& t)
{
    if (!t)
        return;
    QQuickWindow* window = t->window;
    render_thread::retire(window, std::move(t));
    t.reset();
}

} // namespace vlqt
