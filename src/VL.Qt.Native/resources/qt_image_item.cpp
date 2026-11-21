#include "resources/qt_image_item.h"

#include "device.h"
#include "image_resource.h"
#include "render_thread.h"
#include "resources/resources.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QThread>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGImageNode>
#include <QtQuick/private/qsgplaintexture_p.h>
#include <rhi/qrhi.h>

#include <algorithm>
#include <cmath>
#include <unordered_set>
#include <vector>

#ifdef Q_OS_WIN
#include <unknwn.h>
#endif

Q_LOGGING_CATEGORY(lcImageItem, "VL.Qt.Image.Item")

namespace vlqt {

// Texture of one content in one window, shared by every item there (see qt_image_item.h).
struct ImageTexture
{
    QQuickWindow* window = nullptr;
    std::shared_ptr<res::image::ImageResource> content; // keeps the address unique while the entry lives
    QSGTexture* texture = nullptr;
    void* native = nullptr; // own reference on the native texture (shared device), released after the wrapper
    bool mipmaps = false;
    QThread* thread = QThread::currentThread(); // the window's render thread (made in updatePaintNode), frees it

    ImageTexture() = default;
    ImageTexture(const ImageTexture&) = delete;
    ImageTexture& operator=(const ImageTexture&) = delete;
    ~ImageTexture()
    {
        // Another thread may run a frame with it on the window's QRhi right now (render_thread::retire).
        if (texture && QThread::currentThread() != thread)
            qCWarning(lcImageItem, "texture of window %p freed off its render thread", static_cast<void*>(window));
        delete texture; // owns its QRhiTexture (wrapper only, the native texture is not destroyed by it)
#ifdef Q_OS_WIN
        if (native)
            static_cast<IUnknown*>(native)->Release();
#endif
    }
};

namespace {

constexpr int kMaxTiles = 4096; // Tile: one image node per tile

std::vector<std::weak_ptr<ImageTexture>> s_textures; // updatePaintNode: one window syncs at a time, the main thread waits
std::unordered_set<QtImageItem*> s_items;            // every live item, main thread
uint64_t s_epoch = 0;                                // res::contentEpoch seen by afterResourceFrame

std::shared_ptr<ImageTexture> createTexture(QQuickWindow* window, const std::shared_ptr<res::image::ImageResource>& content)
{
    auto t = std::make_shared<ImageTexture>();
    t->window = window;
    t->content = content;
    QRhi* rhi = window->rhi();
    // Content of an older device generation (device lost): its texture is gone, the replacement follows (the
    // item gets it through contentEpoch). Never wrap it, never fall back to the CPU copy.
    if (content->generation() != device::generation())
        return nullptr;
    if (QRhiTexture* src = content->texture()) {
        // Shared device: a wrapper of the native texture in the window's QRhi (same device, see device.h).
        void* native = content->nativeTexture();
        void* dev = device::nativeDevice(rhi);
        if (!native || !dev || dev != device::nativeDevice(device::uploadRhi())) {
            // A window moved to the new device renders once more with its old QRhi before Qt notices the loss
            // (device::rebuild): skip silently, Qt rebuilds the scene graph afterwards. Otherwise warn once per window.
            if (window->property("_vlqt_shared_device").toBool() || window->property("_vlqt_device_warned").toBool())
                return nullptr;
            window->setProperty("_vlqt_device_warned", true);
            qCWarning(lcImageItem, "window %p is not on the shared device: image resources are not drawn there",
                static_cast<void*>(window));
            return nullptr;
        }
        const auto flags = src->flags() & (QRhiTexture::MipMapped | QRhiTexture::UsedWithGenerateMips);
        QRhiTexture* wrapper = rhi->newTexture(src->format(), src->pixelSize(), 1, flags);
        if (!wrapper->createFrom({quint64(native), 0})) {
            delete wrapper;
            qCWarning(lcImageItem, "wrapping texture %p for window %p failed", native, static_cast<void*>(window));
            return nullptr;
        }
#ifdef Q_OS_WIN
        static_cast<IUnknown*>(native)->AddRef();
        t->native = native;
#endif
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
    }
    if (!t->texture)
        return nullptr;
    qCDebug(lcImageItem, "texture %dx%d for window %p (%s)", t->texture->textureSize().width(),
        t->texture->textureSize().height(), static_cast<void*>(window), t->native ? "shared" : "copy");
    return t;
}

std::shared_ptr<ImageTexture> textureFor(QQuickWindow* window, const std::shared_ptr<res::image::ImageResource>& content)
{
    for (auto it = s_textures.begin(); it != s_textures.end();) {
        auto t = it->lock();
        if (!t) {
            it = s_textures.erase(it);
            continue;
        }
        if (t->window == window && t->content == content)
            return t;
        ++it;
    }
    auto t = createTexture(window, content);
    if (t)
        s_textures.push_back(t);
    return t;
}

struct Quad
{
    QRectF rect, source; // item coordinates, texture pixels
};

// Quads like QML Image (alignment centered) for fillMode 0..3.
void layout(int fillMode, float w, float h, float iw, float ih, std::vector<Quad>& out)
{
    out.clear();
    switch (fillMode) {
    case 1: { // PreserveAspectFit
        const float s = std::min(w / iw, h / ih), dw = iw * s, dh = ih * s;
        out.push_back({QRectF((w - dw) / 2, (h - dh) / 2, dw, dh), QRectF(0, 0, iw, ih)});
        break;
    }
    case 2: { // PreserveAspectCrop
        const float s = std::max(w / iw, h / ih), sw = w / s, sh = h / s;
        out.push_back({QRectF(0, 0, w, h), QRectF((iw - sw) / 2, (ih - sh) / 2, sw, sh)});
        break;
    }
    case 3: { // Tile: natural size, one tile centered
        float x0 = std::fmod((w - iw) / 2, iw), y0 = std::fmod((h - ih) / 2, ih);
        if (x0 > 0)
            x0 -= iw;
        if (y0 > 0)
            y0 -= ih;
        for (float y = y0; y < h; y += ih) {
            for (float x = x0; x < w; x += iw) {
                if (int(out.size()) == kMaxTiles) {
                    static bool warned = false;
                    if (!warned) {
                        warned = true;
                        qCWarning(lcImageItem, "Tile: more than %d tiles, the rest is not drawn", kMaxTiles);
                    }
                    return;
                }
                const float l = std::max(x, 0.f), t = std::max(y, 0.f);
                const float r = std::min(x + iw, w), b = std::min(y + ih, h);
                out.push_back({QRectF(l, t, r - l, b - t), QRectF(l - x, t - y, r - l, b - t)});
            }
        }
        break;
    }
    default: // Stretch
        out.push_back({QRectF(0, 0, w, h), QRectF(0, 0, iw, ih)});
        break;
    }
}

} // namespace

std::shared_ptr<ImageTexture> imageTextureFor(QQuickWindow* window, const std::shared_ptr<res::image::ImageResource>& content)
{
    return window && content ? textureFor(window, content) : nullptr;
}

QSGTexture* textureOf(const ImageTexture& t) { return t.texture; }

QtImageItem::QtImageItem(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
    connect(this, &QQuickItem::smoothChanged, this, &QQuickItem::update);
    s_items.insert(this);
}

namespace {

// Drops the item's reference on its texture where the texture's window renders.
void retireTexture(std::shared_ptr<ImageTexture>& t)
{
    if (!t)
        return;
    QQuickWindow* window = t->window;
    render_thread::retire(window, std::move(t));
    t.reset();
}

} // namespace

QtImageItem::~QtImageItem()
{
    retireTexture(m_texture);
    s_items.erase(this);
    if (m_wantId && m_wantId != m_shownId)
        res::releaseElement(m_wantId);
    if (m_shownId)
        res::releaseElement(m_shownId);
}

void QtImageItem::setFillMode(int mode)
{
    if (mode == m_fillMode)
        return;
    m_fillMode = mode;
    update();
    emit fillModeChanged();
}

bool QtImageItem::setResource(uint64_t id, std::string& err)
{
    if (id == m_wantId)
        return true;
    if (id && id != m_shownId && res::acquire(id) != res::okRc) {
        err = "unknown or released image resource " + std::string(res::idText(id).constData());
        return false;
    }
    if (m_wantId && m_wantId != m_shownId)
        res::releaseElement(m_wantId); // never shown: the one before stays
    m_wantId = id;
    if (!id) {
        if (m_shownId)
            res::releaseElement(m_shownId);
        m_shownId = 0;
        m_content.reset();
        setImplicitSize(0, 0);
        update();
        return true;
    }
    advance();
    return true;
}

void QtImageItem::advance()
{
    auto ready = res::image::ready(m_wantId);
    if (!ready || ready == m_content)
        return; // not Ready yet (or failed): the old image stays
    if (m_shownId && m_shownId != m_wantId)
        res::releaseElement(m_shownId);
    m_shownId = m_wantId;
    m_content = std::move(ready);
    setImplicitSize(m_content->size().width(), m_content->size().height());
    update();
}

void QtImageItem::afterResourceFrame()
{
    const uint64_t epoch = res::contentEpoch();
    if (epoch == s_epoch)
        return;
    s_epoch = epoch;
    for (QtImageItem* item : s_items)
        if (item->m_wantId)
            item->advance();
}

// The window's render thread, its QRhi still alive.
void QtImageItem::invalidateSceneGraph()
{
    m_texture.reset();
}

// Main thread.
void QtImageItem::releaseResources()
{
    retireTexture(m_texture);
}

void QtImageItem::geometryChange(const QRectF& newGeometry, const QRectF& oldGeometry)
{
    QQuickItem::geometryChange(newGeometry, oldGeometry);
    if (newGeometry.size() != oldGeometry.size())
        update();
}

QSGNode* QtImageItem::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    // The old texture lives until the nodes below point to the new one.
    const std::shared_ptr<ImageTexture> previous = m_texture;
    if (!m_content)
        m_texture.reset();
    else if (!m_texture || m_texture->window != window() || m_texture->content != m_content)
        m_texture = textureFor(window(), m_content);

    const float w = float(width()), h = float(height());
    if (!m_texture || w <= 0 || h <= 0) {
        delete old;
        return nullptr;
    }
    QSGTexture* tex = m_texture->texture;
    const QSize ts = tex->textureSize();
    thread_local std::vector<Quad> quads; // one per render thread, no allocation per update
    layout(m_fillMode, w, h, float(ts.width()), float(ts.height()), quads);

    QSGNode* root = old ? old : new QSGNode;
    while (root->childCount() > int(quads.size())) {
        QSGNode* last = root->lastChild();
        root->removeChildNode(last);
        delete last;
    }
    const auto filter = smooth() ? QSGTexture::Linear : QSGTexture::Nearest;
    const auto mipFilter = smooth() && m_texture->mipmaps ? QSGTexture::Linear : QSGTexture::None;
    auto* node = static_cast<QSGImageNode*>(root->firstChild());
    for (const Quad& q : quads) {
        if (!node) {
            node = window()->createImageNode();
            node->setOwnsTexture(false);
            root->appendChildNode(node);
        }
        if (node->texture() != tex)
            node->setTexture(tex);
        node->setRect(q.rect);
        node->setSourceRect(q.source);
        node->setFiltering(filter);
        node->setMipmapFiltering(mipFilter);
        node = static_cast<QSGImageNode*>(node->nextSibling());
    }
    return root;
}

} // namespace vlqt
