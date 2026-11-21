// Private Qt API: QQuickShaderEffectSource (the recursive layer); check on Qt updates (list: elements/elements.h).
#include "scene/previous_frame_layer.h"

#include <QtCore/QObject>
#include <QtCore/QSize>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRectangleNode>
#include <QtQuick/QSGSimpleTextureNode>
#include <QtQuick/QSGTextureProvider>
#include <QtQuick/private/qquickshadereffectsource_p.h>

#include <utility>

namespace vlqt {

// Draws the source's current texture, which while the source renders is the previous frame (recursive layers render
// into a second texture and swap), and the fade fill over it. Draws no texture in the first frame after the texture
// got a new size (switch off, resize): that texture was never rendered, its contents are undefined.
class PreviousFrameView : public QQuickItem
{
public:
    PreviousFrameView(QQuickItem* parent, QQuickShaderEffectSource* source) : QQuickItem(parent), m_source(source)
    {
        setFlag(ItemHasContents);
        // Below every element of the root (they stay at z 0); the view is untracked, REPARENT does not count it.
        setZ(-1e9);
    }

    void setFill(const QColor& color)
    {
        if (color == m_fill)
            return;
        m_fill = color;
        update();
    }

protected:
    // Texture first, fill over it; each child exists only while it draws.
    struct Node : QSGNode {
        QSGSimpleTextureNode* texture = nullptr;
        QSGRectangleNode* fill = nullptr;
    };

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
        // No texture while the previous one is undefined: no texture yet (first frame after switching off), a new item
        // size (resize: the source gets a new texture in this frame) or a new texture size at the same item size
        // (device pixel ratio). The frame then starts from the clear colour.
        const bool skip = !texture || textureSize.isEmpty() || size() != m_size
                          || (!m_textureSize.isEmpty() && textureSize != m_textureSize);
        m_textureSize = textureSize;
        m_size = size();

        auto* node = old ? static_cast<Node*>(old) : new Node();
        if (skip) {
            drop(node, node->texture);
            update();
        } else {
            if (!node->texture) {
                node->texture = new QSGSimpleTextureNode();
                node->texture->setOwnsTexture(false);
                node->texture->setFiltering(QSGTexture::Nearest);
                node->prependChildNode(node->texture);
            }
            node->texture->setTexture(texture);
            node->texture->setRect(boundingRect());
            node->texture->markDirty(QSGNode::DirtyMaterial); // the layer swapped its textures
        }
        if (m_fill.alphaF() > 0) {
            if (!node->fill) {
                node->fill = window()->createRectangleNode();
                node->appendChildNode(node->fill);
            }
            node->fill->setRect(boundingRect());
            node->fill->setColor(m_fill); // straight alpha; Qt's flat colour material blends premultiplied "over"
        } else {
            drop(node, node->fill);
        }
        return node;
    }

private:
    template <typename T> static void drop(QSGNode* parent, T*& child)
    {
        if (!child)
            return;
        parent->removeChildNode(child);
        delete std::exchange(child, nullptr);
    }

    QPointer<QQuickShaderEffectSource> m_source;
    QSize m_textureSize; // of the last sync, empty until the first one
    QSizeF m_size;
    QColor m_fill = Qt::transparent; // GUI thread; read in the sync
    bool m_connected = false;
};

PreviousFrameLayer::PreviousFrameLayer(QQuickWindow* window, QQuickItem* root)
{
    QQuickItem* content = window->contentItem();
    auto* source = new QQuickShaderEffectSource();
    // In the window before the source item is set: a source without a window takes a window reference on the root
    // in setSourceItem and another one when it enters the window, but gives back only one (setSourceItem(nullptr)).
    // The root then keeps the old window and cannot move to a new one (window swap).
    source->setParentItem(content);
    source->setSourceItem(root);
    source->setRecursive(true);
    source->setLive(true);
    source->setHideSource(true); // the root is drawn only into the source, the window draws the source
    // The view samples the texture as it is: no flip for ShaderEffect texture coordinates.
    source->setTextureMirroring(QQuickShaderEffectSource::NoMirroring);
    m_source = source;

    auto* view = new PreviousFrameView(root, source);
    m_view = view;

    // Sized to the root, which Scene sizes to the window (also offscreen, where the content item stays 0 x 0). The root
    // may sit at the centre of the scene (Space): the window part of it is the rect at -root position in the
    // root's coordinates, grabbed by the source (sourceRect) and drawn by the view at the same place. One connection
    // per item, each with the item as context: the view is deleted at once, the source later.
    const auto fitSource = [source, root] {
        source->setSize(root->size());
        source->setSourceRect(QRectF(-root->position(), root->size()));
    };
    const auto fitView = [view, root] {
        view->setSize(root->size());
        view->setPosition(-root->position());
    };
    fitSource();
    fitView();
    for (auto signal : {&QQuickItem::widthChanged, &QQuickItem::heightChanged, &QQuickItem::xChanged, &QQuickItem::yChanged}) {
        QObject::connect(root, signal, source, fitSource);
        QObject::connect(root, signal, view, fitView);
    }
}

void PreviousFrameLayer::setFill(const QColor& color)
{
    if (auto* view = static_cast<PreviousFrameView*>(m_view.data()))
        view->setFill(color);
}

PreviousFrameLayer::~PreviousFrameLayer()
{
    // The view first: it references the source's texture provider.
    if (m_view) {
        m_view->setParentItem(nullptr);
        delete m_view.data();
    }
    if (auto* source = static_cast<QQuickShaderEffectSource*>(m_source.data())) {
        if (QQuickItem* root = source->sourceItem())
            QObject::disconnect(root, nullptr, source, nullptr); // no resize until deleteLater
        source->setSourceItem(nullptr); // gives the root back to the main pass (hideSource)
        source->setParentItem(nullptr);
        source->deleteLater(); // its layer is released by the scene graph (render thread)
    }
}

} // namespace vlqt
