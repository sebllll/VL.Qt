#include "feedback.h"

#include <QtCore/QObject>
#include <QtCore/QSize>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGSimpleTextureNode>
#include <QtQuick/QSGTextureProvider>
#include <QtQuick/private/qquickshadereffectsource_p.h>

namespace vlqt {

// Draws the source's current texture, which while the source renders is the previous frame (recursive layers render
// into a second texture and swap). Draws nothing in the first frame after the texture got a new size (switch off,
// resize): that texture was never rendered, its contents are undefined.
class FeedbackView : public QQuickItem
{
public:
    FeedbackView(QQuickItem* parent, QQuickShaderEffectSource* source) : QQuickItem(parent), m_source(source)
    {
        setFlag(ItemHasContents);
        // Below every element of the root (they stay at z 0); the view is untracked, REPARENT does not count it.
        setZ(-1e9);
    }

protected:
    QSGNode* updatePaintNode(QSGNode* old, UpdatePaintNodeData*) override
    {
        // Render thread (sync). The texture provider can only be queried here. Every finished grab needs a sync of
        // this item, so the next frame samples the texture just rendered. Queued: an offscreen scene renders on a
        // thread of its own.
        QSGTextureProvider* provider = m_source ? m_source->textureProvider() : nullptr;
        if (provider && !m_connected) {
            QObject::connect(provider, &QSGTextureProvider::textureChanged, this, [this] { update(); }, Qt::QueuedConnection);
            m_connected = true;
        }
        QSGTexture* texture = provider ? provider->texture() : nullptr;
        const QSize textureSize = texture ? texture->textureSize() : QSize();
        // Draw nothing while the previous texture is undefined: no texture yet (first frame after switching off), a
        // new item size (resize: the source gets a new texture in this frame) or a new texture size at the same item
        // size (device pixel ratio). The frame then starts from the clear colour.
        const bool skip = !texture || textureSize.isEmpty() || size() != m_size
                          || (!m_textureSize.isEmpty() && textureSize != m_textureSize);
        m_textureSize = textureSize;
        m_size = size();
        if (skip) {
            delete old;
            update();
            return nullptr;
        }
        auto* node = static_cast<QSGSimpleTextureNode*>(old);
        if (!node) {
            node = new QSGSimpleTextureNode();
            node->setOwnsTexture(false);
            node->setFiltering(QSGTexture::Nearest);
        }
        node->setTexture(texture);
        node->setRect(boundingRect());
        node->markDirty(QSGNode::DirtyMaterial); // the layer swapped its textures
        return node;
    }

private:
    QPointer<QQuickShaderEffectSource> m_source;
    QSize m_textureSize; // of the last sync, empty until the first one
    QSizeF m_size;
    bool m_connected = false;
};

FeedbackLayer::FeedbackLayer(QQuickWindow* window, QQuickItem* root)
{
    // Sized to the window, not to the content item: the content item of an offscreen scene (never shown) stays 0 x 0.
    QQuickItem* content = window->contentItem();
    auto* source = new QQuickShaderEffectSource();
    source->setSourceItem(root);
    source->setRecursive(true);
    source->setLive(true);
    source->setHideSource(true); // the root is drawn only into the source, the window draws the source
    // The view samples the texture as it is: no flip for ShaderEffect texture coordinates.
    source->setTextureMirroring(QQuickShaderEffectSource::NoMirroring);
    source->setParentItem(content);
    m_source = source;

    auto* view = new FeedbackView(root, source);
    m_view = view;

    // The root of an offscreen scene is 0 x 0 too (its elements are not clipped), and a ShaderEffectSource draws
    // nothing of an empty source item. A window sizes its root to the content item (same size as the window).
    // TODO: task 3: size the root of an offscreen scene in Scene (like a window), not here.
    // One connection per item, each with the item as context: the view is deleted at once, the source later.
    const auto fitSource = [window, source, root] {
        root->setSize(window->size());
        source->setSize(window->size());
    };
    const auto fitView = [window, view] { view->setSize(window->size()); };
    fitSource();
    fitView();
    QObject::connect(window, &QWindow::widthChanged, source, fitSource);
    QObject::connect(window, &QWindow::heightChanged, source, fitSource);
    QObject::connect(window, &QWindow::widthChanged, view, fitView);
    QObject::connect(window, &QWindow::heightChanged, view, fitView);
}

FeedbackLayer::~FeedbackLayer()
{
    // The view first: it references the source's texture provider.
    if (m_view) {
        m_view->setParentItem(nullptr);
        delete m_view.data();
    }
    if (auto* source = static_cast<QQuickShaderEffectSource*>(m_source.data())) {
        if (source->window())
            QObject::disconnect(source->window(), nullptr, source, nullptr); // no root resize until deleteLater
        source->setSourceItem(nullptr); // gives the root back to the main pass (hideSource)
        source->setParentItem(nullptr);
        source->deleteLater(); // its layer is released by the scene graph (render thread)
    }
}

} // namespace vlqt
