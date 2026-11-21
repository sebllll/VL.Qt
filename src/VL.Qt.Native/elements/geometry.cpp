#include "elements/geometry.h"
#include "elements/elements.h"
#include "elements/group_item.h"
#include "user_qml/qml_item.h"

#include <QtCore/Qt>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQml/QQmlListReference>
#include <QtQuick/QQuickItem>
#include <QtQuick/private/qquicktranslate_p.h>          // QQuickMatrix4x4::setMatrix without QVariant

namespace vlqt {

// ---------------------------------------------------------------------------------------------
// Common properties (ids 1..255): C++ QQuickItem API, no QML lookup.

// Other types (Rectangle, Text, Image) always take SIZE, also 0 (not drawn, not clickable).
// Button and the value inputs take part in AUTO_SIZE like the containers (implicit size = content + style padding).
void applySize(ElementEntry& n)
{
    if (n.type == VLQT_ELEMENT_TYPE_GROUP)
        setGroupAutoSize(n.item, n.autoSize); // frame origin: extent with Auto Size, (0, 0) with SIZE
    if (n.type == VLQT_ELEMENT_TYPE_QML)
        static_cast<QmlItem*>(n.item)->setAutoSize(n.autoSize); // root = implicit size of the QML, or this item's SIZE
    if (isAutoSized(n)) {
        n.item->resetWidth();
        n.item->resetHeight();
    } else {
        n.item->setWidth(n.size[0]);
        n.item->setHeight(n.size[1]);
    }
}

namespace {

// SIZE: width/height only; the position comes from ANCHOR + TRANSFORM (see "Geometry" in protocol.h). The
// anchor placement follows the size change by itself (Elements::initGeometry), also for implicit sizes.
bool setSize(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    n.size[0] = v.f[0];
    n.size[1] = v.f[1];
    applySize(n);
    return true;
}

} // namespace

// AUTO_SIZE (containers only, default on): SIZE is ignored and the implicit size applies (Row/Column:
// content + padding, Group: extent of the children). Off: SIZE counts, 0 stays 0.
bool setAutoSize(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    n.autoSize = v.b;
    applySize(n);
    return true;
}

namespace {

bool setTransform(Elements& els, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    // Wire: row-major like Stride, row vectors (translation in M41..M43). QMatrix4x4(const float*)
    // reads row-major too, but Qt uses column vectors -> transpose (translation lands in column 4).
    if (!els.setMatrix(n, QMatrix4x4(v.f).transposed(), err))
        return false;
    // Pin wins: every TRANSFORM (also a resync) puts the item back where the pin says, the drag offset
    // goes to 0. SIZE/ANCHOR keep it.
    ++n.transformEpoch;
    return els.setDragOffset(n, 0, 0, err);
}

// ANCHOR: Qt::Alignment with exactly one horizontal (Left/Right/HCenter) and one vertical flag (Top/Bottom/VCenter).
// Outside Row/Column it places the item (x/y = -anchor) and is the pivot of TRANSFORM, inside only the pivot.
bool setAnchor(Elements& els, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    const int h = v.i & Qt::AlignHorizontal_Mask, vert = v.i & Qt::AlignVertical_Mask;
    const float fx = h == Qt::AlignLeft ? 0.f : h == Qt::AlignRight ? 1.f : h == Qt::AlignHCenter ? 0.5f : -1.f;
    const float fy = vert == Qt::AlignTop ? 0.f : vert == Qt::AlignBottom ? 1.f : vert == Qt::AlignVCenter ? 0.5f : -1.f;
    if (fx < 0 || fy < 0 || (v.i & ~(Qt::AlignHorizontal_Mask | Qt::AlignVertical_Mask))) {
        err = "anchor must be one horizontal and one vertical Qt::AlignmentFlag";
        return false;
    }
    return els.setAnchor(n, fx, fy, err);
}
bool setOpacity(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&) { n.item->setOpacity(v.f[0]); return true; }
bool setClip(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&) { n.item->setClip(v.b); return true; }
// DEBUG: only a flag for the debug overlay (diagnostics/debug_overlay.h), the item itself is not touched.
bool setDebug(Elements& els, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    if (n.debug != v.b)
        els.debugCount += v.b ? 1 : -1;
    n.debug = v.b;
    return true;
}

} // namespace

const std::vector<PropDef>& commonProps()
{
    static const std::vector<PropDef> t = {
        {VLQT_PROPERTY_SIZE, VLQT_VALUE_TYPE_VEC2, setSize, nullptr},
        {VLQT_PROPERTY_TRANSFORM, VLQT_VALUE_TYPE_MAT4, setTransform, nullptr},
        {VLQT_PROPERTY_OPACITY, VLQT_VALUE_TYPE_F32, setOpacity, nullptr},
        {VLQT_PROPERTY_CLIP, VLQT_VALUE_TYPE_BOOL, setClip, nullptr},
        {VLQT_PROPERTY_ANCHOR, VLQT_VALUE_TYPE_I32, setAnchor, nullptr},
        {VLQT_PROPERTY_DEBUG, VLQT_VALUE_TYPE_BOOL, setDebug, nullptr},
    };
    return t;
}

QObject* Elements::createTransform(std::string& err)
{
    if (!matrixComponent) {
        matrixComponent = new QQmlComponent(engine);
        matrixComponent->setData(QByteArrayLiteral("import QtQuick\nMatrix4x4 {}\n"), QUrl(QStringLiteral("vlqt_matrix4x4.qml")));
    }
    QObject* obj = matrixComponent->isError() ? nullptr : matrixComponent->create();
    if (!obj) {
        err = "Matrix4x4 could not be created: " + matrixComponent->errorString().toStdString();
        return nullptr;
    }
    if (!qobject_cast<QQuickMatrix4x4*>(obj)) {
        delete obj;
        err = "Matrix4x4 is not a QQuickMatrix4x4";
        return nullptr;
    }
    QQmlEngine::setObjectOwnership(obj, QQmlEngine::CppOwnership);
    return obj;
}

// Qt column vectors: v' = T(d) * T(p) * M * T(-p) * v with p = anchor in the item's local coordinates (actual size)
// and d = drag offset: the offset comes last, i.e. it moves the item in the parent's space. One Matrix4x4 for
// both instead of a Matrix4x4 + Translate pair: the order in item.transform can never be wrong.
// Together with the anchor placement (x/y = -p outside Row/Column, placeItem) Qt maps v -> d + M * (v - p), i.e.
// (v - anchor) * M in row vectors as in protocol.h; in a Row/Column v -> layoutPos + d + p + M * (v - p).
QMatrix4x4 pivoted(const TransformState& s, const QQuickItem* item)
{
    const QPointF a = anchorPoint(s, item);
    const float px = float(a.x()), py = float(a.y());
    QMatrix4x4 r;
    r.translate(s.dx + px, s.dy + py);
    r *= s.base;
    r.translate(-px, -py);
    return r;
}

namespace {

// A Row/Column (Qt positioner) owns the position of its children: writing x/y there would overwrite the layout, and
// the positioner only lays out again on a size change. The lookup runs once per meta object (hot path: every
// size change of every element, 10000 per apply in the perf test).
bool inPositioner(const QQuickItem* item)
{
    static thread_local const QMetaObject* lastMeta = nullptr;
    static thread_local bool lastPositioned = false;
    const QQuickItem* parent = item->parentItem();
    if (!parent)
        return false;
    const QMetaObject* meta = parent->metaObject();
    if (meta != lastMeta) {
        lastMeta = meta;
        lastPositioned = meta->indexOfSignal("positioningComplete()") >= 0;
    }
    return lastPositioned;
}

// TRANSFORM is a pure x/y translation (Qt column vectors: identity except m(0,3), m(1,3)).
bool isTranslation2D(const QMatrix4x4& m)
{
    return m(0, 0) == 1 && m(0, 1) == 0 && m(0, 2) == 0
        && m(1, 0) == 0 && m(1, 1) == 1 && m(1, 2) == 0
        && m(2, 0) == 0 && m(2, 1) == 0 && m(2, 2) == 1 && m(2, 3) == 0
        && m(3, 0) == 0 && m(3, 1) == 0 && m(3, 2) == 0 && m(3, 3) == 1;
}

// Anchor placement: outside a Row/Column the anchor point (actual size) lies at the parent origin before
// TRANSFORM, i.e. x/y = -anchor. Inside one the layout keeps x/y.
// Fast path (perf): as long as no Matrix4x4 is attached (TRANSFORM was only ever a pure x/y translation, no drag
// offset, never in a Row/Column) the translation goes into x/y as well: x/y = t - anchor is the same mapping as
// T(t) * T(-anchor), without a QML transform object, its QVariant writes and the general matrix in every
// mapToScene/sync. Elements::setMatrix decides when to attach the Matrix4x4 (from then on it carries TRANSFORM).
void placeItem(const TransformState& s, QQuickItem* item)
{
    if (inPositioner(item))
        return;
    QPointF p = -anchorPoint(s, item);
    if (!s.matrix)
        p += QPointF(s.base(0, 3), s.base(1, 3));
    item->setPosition(p);
}

} // namespace

// Matrix4x4 objects only come from Elements::createTransform (checked with qobject_cast there). setMatrix compares
// first and emits matrixChanged only on a real change; no QVariant/QMetaProperty detour (perf).
void setMatrixValue(QObject* t, const QMatrix4x4& m)
{
    static_cast<QQuickMatrix4x4*>(t)->setMatrix(m);
}

namespace {

void writeMatrix(const TransformState& s, QQuickItem* item)
{
    if (s.matrix)
        setMatrixValue(s.matrix, pivoted(s, item));
}

} // namespace

bool takesAutoSize(const ElementEntry& n)
{
    return n.type == VLQT_ELEMENT_TYPE_GROUP || n.type == VLQT_ELEMENT_TYPE_ROW || n.type == VLQT_ELEMENT_TYPE_COLUMN
        || n.type == VLQT_ELEMENT_TYPE_BUTTON || n.type == VLQT_ELEMENT_TYPE_SPIN_BOX
        || n.type == VLQT_ELEMENT_TYPE_CHECK_BOX || n.type == VLQT_ELEMENT_TYPE_SWITCH || n.type == VLQT_ELEMENT_TYPE_SLIDER
        || n.type == VLQT_ELEMENT_TYPE_TEXT_FIELD || n.type == VLQT_ELEMENT_TYPE_QML
        || (n.type == VLQT_ELEMENT_TYPE_SHADER && n.effectSource); // implicit size = Input; without Input the SIZE
}

QRectF itemFrame(const TransformState& s, const QQuickItem* item)
{
    const qreal w = item->width(), h = item->height();
    if (s.sizedFrame)
        return QRectF(-s.fx * w, -s.fy * h, w, h);
    return QRectF(s.ox, s.oy, w, h);
}

QPointF anchorPoint(const TransformState& s, const QQuickItem* item)
{
    // Group with Auto Size off: the frame is placed around the origin, the item and its children stay.
    if (s.sizedFrame)
        return QPointF(0, 0);
    // Left/Top (0) is always the origin: an auto-sized Group stays a pure transform container (its origin = that of
    // its children) even when they reach into negative coordinates; Center/Right/Bottom resolve against the frame.
    const qreal x = s.fx == 0 ? 0 : s.ox + s.fx * item->width();
    const qreal y = s.fy == 0 ? 0 : s.oy + s.fy * item->height();
    return QPointF(x, y);
}

void Elements::initGeometry(ElementEntry& node)
{
    node.xf = std::make_shared<TransformState>();
    // Default ANCHOR AlignLeft|AlignTop for all elements (fx = fy = 0, TransformState default).
    auto* group = node.type == VLQT_ELEMENT_TYPE_GROUP ? static_cast<GroupItem*>(node.item) : nullptr;
    placeItem(*node.xf, node.item);
    // Placement and the anchor of TRANSFORM follow every size change (Auto Size, layout, text, children extent), not
    // only the SIZE sent by C#. Connected at creation, i.e. before the mask frame's connections (createMaskFrame), so
    // the frame always sees the final x/y and matrix. Context = the item: the connection ends with it, xf is shared.
    auto update = [xf = node.xf, item = node.item]() {
        placeItem(*xf, item);
        writeMatrix(*xf, item);
        markGeometry(*xf); // size and frame changed
    };
    QObject::connect(node.item, &QQuickItem::widthChanged, node.item, update);
    QObject::connect(node.item, &QQuickItem::heightChanged, node.item, update);
    if (group) {
        group->xf = node.xf;
        group->placed = update; // a mask frame follows through x/y (the anchor moves with the frame origin)
        // Auto Size off: the frame origin depends on the size (-fx * w), the effect padding follows.
        QObject::connect(group, &QQuickItem::widthChanged, group, [group] { group->syncEffectFrame(); });
        QObject::connect(group, &QQuickItem::heightChanged, group, [group] { group->syncEffectFrame(); });
    }
}

bool Elements::placeAfterReparent(ElementEntry& node, std::string& err)
{
    // Into a Row/Column with a translation still folded into x/y (placeItem): the layout owns x/y now, the
    // translation moves to the Matrix4x4.
    if (!node.transform && !node.xf->base.isIdentity() && inPositioner(node.item))
        return writeTransform(node, err);
    placeItem(*node.xf, node.item);
    return true;
}

bool Elements::setMatrix(ElementEntry& node, const QMatrix4x4& m, std::string& err)
{
    TransformState& s = *node.xf;
    if (!node.transform && isTranslation2D(m) && s.dx == 0 && s.dy == 0 && (!inPositioner(node.item) || m.isIdentity())) {
        // Fast path (placeItem): x/y only. The x/y change itself pokes a parent Group and a mask frame.
        s.base = m;
        placeItem(s, node.item);
        return true;
    }
    s.base = m;
    return writeTransform(node, err);
}

bool Elements::setDragOffset(ElementEntry& node, float dx, float dy, std::string& err)
{
    if (node.xf->dx == dx && node.xf->dy == dy)
        return true; // unchanged: no Matrix4x4 for items that were never transformed or dragged
    node.xf->dx = dx;
    node.xf->dy = dy;
    return writeTransform(node, err);
}

bool Elements::writeTransform(ElementEntry& node, std::string& err)
{
    if (!node.transform) {
        QObject* t = createTransform(err);
        if (!t)
            return false;
        t->setParent(node.item); // deleted together with the item
        QQmlListReference list(node.item, "transform");
        if (!list.isValid() || !list.append(t)) {
            delete t;
            err = "appending Matrix4x4 to 'transform' failed";
            return false;
        }
        node.transform = t;
        node.xf->matrix = t; // from now on written by the size connection of initGeometry too
        placeItem(*node.xf, node.item); // the translation leaves x/y (fast path) and goes into the matrix below
    }
    setMatrixValue(node.transform, pivoted(*node.xf, node.item));
    updateMaskFrame(node);
    pokeGroup(node.item);
    markGeometry(*node.xf); // Qt has no public signal for a transform change
    return true;
}

bool Elements::setAnchor(ElementEntry& node, float fx, float fy, std::string& err)
{
    if (node.xf->fx == fx && node.xf->fy == fy)
        return true;
    node.xf->fx = fx;
    node.xf->fy = fy;
    placeItem(*node.xf, node.item);
    if (node.type == VLQT_ELEMENT_TYPE_GROUP)
        static_cast<GroupItem*>(node.item)->syncEffectFrame(); // Auto Size off: the frame moves with the anchor
    if (node.transform)
        setMatrixValue(node.transform, pivoted(*node.xf, node.item));
    updateMaskFrame(node);
    pokeGroup(node.item);
    markGeometry(*node.xf); // matrix pivot or Group frame (Auto Size off) moved, maybe without an x/y change
    return true;
}

} // namespace vlqt
