/* elements/group_item.h - GroupItem, the item of a Group: its group bounds are the bounding box of its children. */
#pragma once

#include <QtCore/QPointer>
#include <QtQuick/QQuickItem>

#include <functional>
#include <memory>

class QQuickShaderEffectSource;

namespace vlqt {

struct TransformState; // elements/geometry.h

// Group ("Group frame" in protocol.h): an Item whose implicit size is its group bounds = bounding box of the children
// (mapped through their x/y AND transform, unlike Qt's childrenRect; a child Group with its own group bounds) and of its
// own origin. With Auto Size on the bounds may start left of / above the origin (xf->ox/oy < 0): width/height = size of
// the bounds, hit area (contains) and clip follow them, the anchor resolves against them (Left/Top stay at the origin,
// see anchorPoint). Recomputed in updatePolish (once per frame, before sync) after a child moved, resized, was
// shown/hidden, added/removed, got a new matrix (Elements::writeTransform pokes the parent, Qt has no public signal for
// transform changes) or, as a Group, moved its bounds inside its item (updatePolish and setAutoSize poke the parent).
class GroupItem final : public QQuickItem
{
public:
    std::shared_ptr<TransformState> xf; // Elements::initGeometry
    std::function<void()> placed;       // re-places the group after the origin of its bounds changed (initGeometry)
    QPointer<QObject> effectHolder;     // first stage of the effect chain (Elements::setEffectChain): frameLeft/frameTop follow the bounds

    // Takes a proxy that the effects may still sample in this frame; its owner deletes it after the next frame.
    using RetireProxy = std::function<void(QQuickItem*)>;
    // Group as texture (ShaderEffect Input or texture uniform): one hidden QQuickShaderEffectSource whose sourceRect is the
    // group bounds, shared by all uses and refcounted. The first acquire creates it under proxyParent; a destroyed Group
    // hands it to retire. Returns the proxy.
    QQuickItem* acquireProxy(QQuickItem* proxyParent, const RetireProxy& retire);
    // Drops one use; the last one hands the proxy to retire. A release without an acquire is a programming error.
    void releaseProxy(const RetireProxy& retire);
    // The proxy while it is acquired, else nullptr.
    QQuickItem* proxy() const;

    // The effect chain's padding covers the part of the bounds left of / above the origin (effects/effect_chain.cpp); the
    // ShaderEffect proxy samples exactly the bounds. Called on every change of the bounds (origin, size, anchor, Auto Size).
    void syncEffectBounds();

    ~GroupItem() override;

    void setAutoSize(bool on);

    // The group bounds have to be recomputed (next polish).
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
    // Kept here alone: proxyRefs_ >= 0, proxy_ only while proxyRefs_ > 0, proxyRetire_ connected while proxy_ exists.
    QPointer<QQuickShaderEffectSource> proxy_; // texture of the whole group bounds for ShaderEffects
    int proxyRefs_ = 0;                        // Input/texture uniform uses of proxy_
    QMetaObject::Connection proxyRetire_;      // destroyed -> retire proxy_, dropped on the last release
};

/* The item is a Group (GROUP, GroupItem). */
bool isGroup(const QQuickItem* item);
/* Texture source of a Group used as ShaderEffect Input or texture uniform: a hidden QQuickShaderEffectSource
 * whose sourceRect is the group bounds (GroupItem::acquireProxy); nullptr for other items or a Group without one.
 * Read by ShaderItem on the render thread during sync (GUI thread blocked). */
QQuickItem* groupTextureSource(const QQuickItem* item);
/* Group: kind of bounds of AUTO_SIZE (applySize); no-op for other items. */
void setGroupAutoSize(QQuickItem* item, bool on);
// A child's matrix or own bounds changed: its parent Group recomputes its bounds (no-op for other parents).
void pokeGroup(const QQuickItem* item);

} // namespace vlqt
