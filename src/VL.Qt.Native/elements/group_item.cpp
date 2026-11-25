// Private Qt API: QQuickItemPrivate::explicitVisible, QQuickShaderEffectSource (group bounds proxy); check on Qt updates (list: elements/elements.h).
#include "elements/group_item.h"
#include "elements/element_types.h"
#include "elements/geometry.h"

#include <QtQuick/private/qquickitem_p.h>               // explicitVisible of Group children
#include <QtQuick/private/qquickshadereffectsource_p.h> // group bounds proxy

#include <algorithm>
#include <typeinfo>

namespace vlqt {

void GroupItem::syncEffectBounds()
{
    if ((!effectHolder && !proxy_) || !xf)
        return;
    const QRectF b = itemBounds(*xf, this);
    if (effectHolder) {
        effectHolder->setProperty("frameLeft", std::max<qreal>(0, -b.left()));
        effectHolder->setProperty("frameTop", std::max<qreal>(0, -b.top()));
    }
    if (proxy_) {
        proxy_->setSourceRect(b);
        proxy_->setSize(b.size()); // = implicit size of the ShaderEffect (Auto Size)
    }
}

QQuickItem* GroupItem::acquireProxy(QQuickItem* proxyParent, const RetireProxy& retire)
{
    ++proxyRefs_;
    if (proxy_)
        return proxy_;
    // A ShaderEffectSource renders its source item (also a hidden one: it refs it, refFromEffectItem) into its own
    // texture with sourceRect = the group bounds (like the snapshot's frameTarget). Hidden itself, so it never draws on
    // screen; it still gets updatePaintNode and its texture is rendered by the consuming ShaderItem's preprocess
    // without an extra refFromEffectItem on the proxy (checked by grab in the smoke test group-shader-source, also
    // live changes). Under the root, never under the Group (it would be part of its own texture) or an element (layout,
    // REPARENT indices count tracked items only). hideSource off: the host hides the Group already (REPARENT under the
    // ShaderEffect, Elements::setEffectSource). Not smooth, like the item layers used before (layer.smooth default false).
    auto* proxy = new QQuickShaderEffectSource();
    proxy->setParent(proxyParent); // deleted with the root at the latest
    proxy->setVisible(false);
    proxy->setLive(true);
    proxy->setHideSource(false);
    proxy->setSmooth(false);
    proxy->setParentItem(proxyParent);
    proxy->setSourceItem(this);
    proxy_ = proxy;
    syncEffectBounds();
    // A destroyed Group (DESTROY of a texture element, RESET) retires its proxy: the effects still sample it this frame.
    proxyRetire_ = QObject::connect(this, &QObject::destroyed, proxy, [retire, proxy] { retire(proxy); });
    return proxy;
}

void GroupItem::releaseProxy(const RetireProxy& retire)
{
    Q_ASSERT(proxyRefs_ > 0);
    if (proxyRefs_ <= 0 || --proxyRefs_ > 0)
        return;
    // The proxy goes earlier only with its QObject parent, the root, which ~Scene deletes after every element; the
    // connection went with it.
    if (!proxy_)
        return;
    QQuickShaderEffectSource* proxy = proxy_;
    proxy_ = nullptr;
    // Only our own connection: disconnecting every destroyed -> proxy connection would also drop the proxy's
    // sourceItemDestroyed, and ~QQuickShaderEffectSource would then use the deleted Group (window_destroy, or the Input
    // released and the Group destroyed in one apply).
    QObject::disconnect(proxyRetire_);
    retire(proxy); // the effect may sample it until it syncs onto the new texture
}

QQuickItem* GroupItem::proxy() const { return proxy_.data(); }

GroupItem::~GroupItem()
{
    // ~QQuickItem detaches the children after this part is gone: no polish() from their signals any more.
    for (QQuickItem* c : childItems())
        QObject::disconnect(c, nullptr, this, nullptr);
}

void GroupItem::setAutoSize(bool on)
{
    if (autoSize_ == on)
        return;
    autoSize_ = on;
    if (xf) {
        xf->sizedBounds = !on;
        if (placed)
            placed(); // anchor placement and pivot change with the kind of bounds
        syncEffectBounds(); // the kind of bounds changes even if origin and size stay
        pokeGroup(this);    // a parent Group counts these bounds, maybe without an x/y/size signal of this item
        markGeometry(*xf);
    }
    invalidate();
}

void GroupItem::itemChange(ItemChange change, const ItemChangeData& data)
{
    if (change == ItemChildAddedChange) {
        QQuickItem* c = data.item;
        auto again = [this] { invalidate(); };
        QObject::connect(c, &QQuickItem::xChanged, this, again);
        QObject::connect(c, &QQuickItem::yChanged, this, again);
        QObject::connect(c, &QQuickItem::widthChanged, this, again);
        QObject::connect(c, &QQuickItem::heightChanged, this, again);
        QObject::connect(c, &QQuickItem::visibleChanged, this, again);
        invalidate();
    } else if (change == ItemChildRemovedChange) {
        QObject::disconnect(data.item, nullptr, this, nullptr);
        invalidate();
    }
    QQuickItem::itemChange(change, data);
}

void GroupItem::updatePolish()
{
    // Only after a change: Scene::polish (poll without a render) calls ensurePolished, i.e. updatePolish, on every
    // item twice per poll, changed or not (perf).
    if (!dirty_)
        return;
    dirty_ = false;
    // Children with an empty rect (size 0 = not drawn) and hidden ones do not count. Hidden = the child's own flag:
    // isVisible() is false for every child of a hidden Group, e.g. the Input of a ShaderEffect. A child Group counts
    // with its own group bounds, which may start left of / above its origin; every other child with (0, 0, w, h).
    QRectF e;
    bool any = false;
    for (QQuickItem* c : childItems()) {
        if (!QQuickItemPrivate::get(c)->explicitVisible || c->width() <= 0 || c->height() <= 0)
            continue;
        const auto* g = isGroup(c) ? static_cast<const GroupItem*>(c) : nullptr;
        const QRectF local = g && g->xf ? itemBounds(*g->xf, c) : QRectF(0, 0, c->width(), c->height());
        const QRectF r = c->mapRectToItem(this, local);
        e = any ? e.united(r) : r;
        any = true;
    }
    const qreal l = any ? std::min<qreal>(0, e.left()) : 0, t = any ? std::min<qreal>(0, e.top()) : 0;
    const qreal r = any ? std::max<qreal>(0, e.right()) : 0, b = any ? std::max<qreal>(0, e.bottom()) : 0;
    // Origin first: the width/height connection of initGeometry re-places with the new bounds.
    bool moved = false;
    if (xf) {
        const float ox = autoSize_ ? float(l) : 0.f, oy = autoSize_ ? float(t) : 0.f;
        moved = xf->ox != ox || xf->oy != oy;
        xf->ox = ox;
        xf->oy = oy;
    }
    setImplicitSize(r - l, b - t);
    if (moved) {
        syncEffectBounds();
        if (placed)
            placed();
        // The bounds moved inside the item: with the anchor Left/Top no x/y/size signal tells a parent Group or the
        // measure for that.
        pokeGroup(this);
        markGeometry(*xf);
    }
}

bool GroupItem::contains(const QPointF& point) const
{
    if (containmentMask() || !xf)
        return QQuickItem::contains(point);
    return itemBounds(*xf, this).contains(point);
}

QRectF GroupItem::clipRect() const { return xf ? itemBounds(*xf, this) : QQuickItem::clipRect(); }

namespace {

QQuickItem* makeGroup() { return new GroupItem(); }

} // namespace

void setGroupAutoSize(QQuickItem* item, bool on)
{
    if (isGroup(item))
        static_cast<GroupItem*>(item)->setAutoSize(on);
}

// A child's matrix or own bounds changed: its parent Group recomputes its bounds (no-op for other parents).
void pokeGroup(const QQuickItem* item)
{
    // typeid instead of dynamic_cast: hot path (every TRANSFORM, 10000 per apply in the perf test), GroupItem is final.
    QQuickItem* p = item->parentItem();
    if (p && typeid(*p) == typeid(GroupItem))
        static_cast<GroupItem*>(p)->invalidate();
}

bool isGroup(const QQuickItem* item)
{
    return item && typeid(*item) == typeid(GroupItem);
}

QQuickItem* groupTextureSource(const QQuickItem* item)
{
    return isGroup(item) ? static_cast<const GroupItem*>(item)->proxy() : nullptr;
}

// Group (C#) = GroupItem: implicit size and group bounds from the children incl. their transform.
TypeDef groupType()
{
    TypeDef t{VLQT_ELEMENT_TYPE_GROUP, "GROUP", nullptr, {}, makeGroup};
    t.autoSize = AutoSize::Always;
    t.container = true;
    t.applyAutoSize = setGroupAutoSize; // origin of the bounds: extent with Auto Size, anchor-placed SIZE without
    return t;
}

} // namespace vlqt
