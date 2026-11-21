#include "resources/qt_image_item.h"

#include "image_resource.h"
#include "render_thread.h"
#include "resources/change_watch.h"
#include "resources/image_texture_cache.h"
#include "resources/resources.h"

#include <QtCore/QLoggingCategory>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGImageNode>

#include <algorithm>
#include <cmath>
#include <vector>

Q_LOGGING_CATEGORY(lcImageItem, "VL.Qt.Image.Item")

namespace vlqt {

namespace {

constexpr int kMaxTiles = 4096; // Tile: one image node per tile

res::ChangeWatch<QtImageItem> s_watch; // items by the id they wait for, main thread

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

QtImageItem::QtImageItem(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
    connect(this, &QQuickItem::smoothChanged, this, &QQuickItem::update);
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
    s_watch.remove(m_wantId, this);
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
    s_watch.remove(m_wantId, this);
    m_wantId = id;
    s_watch.add(id, this);
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
    s_watch.forChanged([](QtImageItem* item) { item->advance(); });
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
    else if (!m_texture || !m_texture->fits(window(), m_content))
        m_texture = imageTextureFor(window(), m_content);

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
