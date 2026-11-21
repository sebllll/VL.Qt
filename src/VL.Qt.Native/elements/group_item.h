/* elements/group_item.h - GroupItem, the item of a Group: its frame is the bounding box of its children. */
#pragma once

#include <QtCore/QPointer>
#include <QtQuick/QQuickItem>

#include <functional>
#include <memory>

class QQuickShaderEffectSource;

namespace vlqt {

struct TransformState; // elements/geometry.h

// Group ("Group frame" in protocol.h): an Item whose implicit size is its frame = bounding box of the children
// (mapped through their x/y AND transform, unlike Qt's childrenRect) and of its own origin. With Auto Size on the frame
// may start left of / above the origin (xf->ox/oy < 0): width/height = frame size, hit area (contains) and clip follow
// the frame, the anchor resolves against it (Left/Top stay at the origin, see anchorPoint). Recomputed in
// updatePolish (once per frame, before sync) after a child moved, resized, was shown/hidden, added/removed or got a
// new matrix (Elements::writeTransform pokes the parent, Qt has no public signal for transform changes).
class GroupItem final : public QQuickItem
{
public:
    std::shared_ptr<TransformState> xf; // Elements::initGeometry
    std::function<void()> placed;       // re-places the group after a frame origin change (initGeometry)
    QPointer<QObject> effectHolder;     // first stage of the effect chain (Elements::setEffectChain): frameLeft/frameTop follow the frame
    QPointer<QQuickShaderEffectSource> proxy; // texture of the whole frame for ShaderEffects (Elements::acquireGroupProxy)
    int proxyRefs = 0;                  // Input/texture uniform uses of proxy
    QMetaObject::Connection proxyRetire; // destroyed -> retire proxy (acquireGroupProxy), dropped on release

    // The effect chain's padding covers the part of the frame left of / above the origin (effects/effect_chain.cpp); the
    // ShaderEffect proxy samples exactly the frame. Called on every frame change (origin, size, anchor, Auto Size).
    void syncEffectFrame();

    ~GroupItem() override;

    void setAutoSize(bool on);

    // The frame has to be recomputed (next polish).
    void invalidate()
    {
        dirty_ = true;
        polish();
    }

protected:
    void itemChange(ItemChange change, const ItemChangeData& data) override;
    void updatePolish() override;
    bool contains(const QPointF& point) const override;
    QRectF clipRect() const override;

private:
    bool autoSize_ = true;
    bool dirty_ = true;
};

/* The item is a Group (GROUP, GroupItem). */
bool isGroup(const QQuickItem* item);
/* Texture source of a Group used as ShaderEffect Input or texture uniform: a hidden QQuickShaderEffectSource
 * whose sourceRect is the Group's frame (Elements::acquireGroupProxy); nullptr for other items or a Group without one.
 * Read by ShaderItem on the render thread during sync (GUI thread blocked). */
QQuickItem* groupTextureSource(const QQuickItem* item);
/* Group: frame kind of AUTO_SIZE (applySize); no-op for other items. */
void setGroupAutoSize(QQuickItem* item, bool on);
// A child's matrix changed: its parent Group recomputes the frame (no-op for other parents).
void pokeGroup(const QQuickItem* item);

} // namespace vlqt
