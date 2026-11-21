#include "elements/group_item.h"
#include "elements/element_types.h"
#include "elements/geometry.h"

#include <QtQuick/private/qquickitem_p.h>               // explicitVisible of Group children
#include <QtQuick/private/qquickshadereffectsource_p.h> // Group frame proxy

#include <algorithm>
#include <typeinfo>

namespace vlqt {

void GroupItem::syncEffectFrame()
{
    if ((!effectHolder && !proxy) || !xf)
        return;
    const QRectF f = itemFrame(*xf, this);
    if (effectHolder) {
        effectHolder->setProperty("frameLeft", std::max<qreal>(0, -f.left()));
        effectHolder->setProperty("frameTop", std::max<qreal>(0, -f.top()));
    }
    if (proxy) {
        proxy->setSourceRect(f);
        proxy->setSize(f.size()); // = implicit size of the ShaderEffect (Auto Size)
    }
}

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
        xf->sizedFrame = !on;
        if (placed)
            placed(); // anchor placement and pivot change with the frame kind
        syncEffectFrame(); // the frame kind changes even if origin and size stay
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
    // isVisible() is false for every child of a hidden Group, e.g. the Input of a ShaderEffect.
    QRectF e;
    bool any = false;
    for (QQuickItem* c : childItems()) {
        if (!QQuickItemPrivate::get(c)->explicitVisible || c->width() <= 0 || c->height() <= 0)
            continue;
        const QRectF r = c->mapRectToItem(this, QRectF(0, 0, c->width(), c->height()));
        e = any ? e.united(r) : r;
        any = true;
    }
    const qreal l = any ? std::min<qreal>(0, e.left()) : 0, t = any ? std::min<qreal>(0, e.top()) : 0;
    const qreal r = any ? std::max<qreal>(0, e.right()) : 0, b = any ? std::max<qreal>(0, e.bottom()) : 0;
    // Origin first: the width/height connection of initGeometry re-places with the new frame.
    bool moved = false;
    if (xf) {
        const float ox = autoSize_ ? float(l) : 0.f, oy = autoSize_ ? float(t) : 0.f;
        moved = xf->ox != ox || xf->oy != oy;
        xf->ox = ox;
        xf->oy = oy;
    }
    setImplicitSize(r - l, b - t);
    if (moved) {
        syncEffectFrame();
        if (placed)
            placed();
        markGeometry(*xf); // the frame moved inside the item: no x/y/size signal for that
    }
}

bool GroupItem::contains(const QPointF& point) const
{
    if (containmentMask() || !xf)
        return QQuickItem::contains(point);
    return itemFrame(*xf, this).contains(point);
}

QRectF GroupItem::clipRect() const { return xf ? itemFrame(*xf, this) : QQuickItem::clipRect(); }

namespace {

QQuickItem* makeGroup() { return new GroupItem(); }

} // namespace

void setGroupAutoSize(QQuickItem* item, bool on)
{
    if (isGroup(item))
        static_cast<GroupItem*>(item)->setAutoSize(on);
}

// A child's matrix changed: its parent Group recomputes the frame (no-op for other parents).
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
    return isGroup(item) ? static_cast<const GroupItem*>(item)->proxy.data() : nullptr;
}

// Group (C#) = GroupItem: implicit size/frame from the children incl. their transform.
TypeDef groupType()
{
    return {VLQT_ELEMENT_TYPE_GROUP, "GROUP", nullptr, {{VLQT_PROPERTY_AUTO_SIZE, VLQT_VALUE_TYPE_BOOL, setAutoSize, nullptr}}, makeGroup};
}

} // namespace vlqt
