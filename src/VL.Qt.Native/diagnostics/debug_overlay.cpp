#include "diagnostics/debug_overlay.h"
#include "app/scene_change.h"
#include "diagnostics/overlay_geometry.h"
#include "diagnostics/overlay_software.h"
#include "scene/scene.h"

#include <QtGui/QFont>
#include <QtGui/QFontMetricsF>
#include <QtQuick/QSGGeometryNode>
#include <QtQuick/QSGVertexColorMaterial>

#include <algorithm>
#include <cmath>
#include <cstring>

namespace vlqt {
namespace {

// Colors (straight alpha, premultiplied when written). Legend: debug_overlay.h.
const QRgb kFrame = qRgba(0, 215, 215, 235);
const QRgb kOrigin = qRgba(255, 0, 200, 255);
const QRgb kAnchor = qRgba(255, 220, 0, 255);
const QRgb kBaseline = qRgba(0, 220, 90, 255);
const QRgb kAscent = qRgba(0, 220, 90, 200);
const QRgb kDescent = qRgba(255, 70, 70, 220);
const QRgb kImplicit = qRgba(255, 150, 0, 230);
const QRgb kChildren = qRgba(80, 140, 255, 235);
const QRgb kOverflow = qRgba(255, 40, 40, 80);
const QRgb kPadding = qRgba(120, 200, 100, 70);
const QRgb kSpacing = qRgba(190, 120, 255, 190);

constexpr float kLine = 1.f;   // line width, logical pixels (scene space)
constexpr float kDashOn = 4.f; // dash pattern (Auto Size frame, Text ascent/descent), scene space
constexpr float kDashOff = 3.f;
constexpr float kDotOn = 1.5f; // dot pattern (children extent), scene space
constexpr float kDotOff = 2.5f;

enum class Stroke { Solid, Dash, Dot };
constexpr float kHatch = 6.f;  // hatch distance of spacing gaps, item space

// Two layers: areas (padding, spacing hatch) of all nodes go first, lines and marks of all nodes on top, so a fill of a
// later node never covers a line of an earlier one.
struct Painter {
    std::vector<OverlayVertex>& fills;
    std::vector<OverlayVertex>& lines;
    const QQuickItem* item = nullptr; // current item: local points are mapped with its mapToScene
    qreal dpr = 1;
    bool area = false; // true: write into the fill layer

    QPointF map(qreal x, qreal y) const { return item->mapToScene(QPointF(x, y)); }

    void vert(const QPointF& p, QRgb c)
    {
        OverlayVertex x;
        setVertex(x, float(p.x()), float(p.y()), c);
        (area ? fills : lines).push_back(x);
    }
    // Device pixel grid (scene units): a line of whole device pixels whose edges lie on pixel boundaries. Without it a
    // 1 px line on an integer coordinate straddles two pixels and the 4x MSAA resolve (window/window_exports.cpp) halves it into both
    // at DPR 1 (frame + padding mixed to (37,144,124) on CI).
    qreal snapEdge(qreal v) const { return std::round(v * dpr) / dpr; }
    qreal snapCenter(qreal v, qreal widthPx) const { return (std::round(v * dpr - widthPx / 2) + widthPx / 2) / dpr; }
    // Quad of scene points (two triangles).
    void quad(const QPointF& a, const QPointF& b, const QPointF& c, const QPointF& d, QRgb color)
    {
        const QRgb p = qPremultiply(color);
        vert(a, p); vert(b, p); vert(c, p);
        vert(a, p); vert(c, p); vert(d, p);
    }
    // Segment of scene points as a quad of kLine width (no line primitive: width 1 only on D3D).
    // Axis-aligned segments (in scene space) are snapped to the device pixel grid; rotated ones stay as they are.
    void segment(QPointF a, QPointF b, QRgb c, qreal width = kLine)
    {
        QPointF d = b - a;
        qreal len = std::hypot(d.x(), d.y());
        if (len < 1e-3)
            return;
        const bool vertical = std::abs(d.x()) <= 1e-4 * len, horizontal = std::abs(d.y()) <= 1e-4 * len;
        if (vertical || horizontal) {
            const qreal px = std::max<qreal>(1, std::round(width * dpr));
            width = px / dpr;
            if (vertical) {
                const qreal x = snapCenter(a.x(), px);
                a = QPointF(x, snapEdge(a.y()));
                b = QPointF(x, snapEdge(b.y()));
            } else {
                const qreal y = snapCenter(a.y(), px);
                a = QPointF(snapEdge(a.x()), y);
                b = QPointF(snapEdge(b.x()), y);
            }
            d = b - a;
            len = std::hypot(d.x(), d.y());
            if (len < 1e-3)
                return;
        }
        const QPointF n(-d.y() / len * width / 2, d.x() / len * width / 2);
        quad(a + n, b + n, b - n, a - n, c);
    }
    void dashed(const QPointF& a, const QPointF& b, QRgb c, qreal on, qreal off)
    {
        const QPointF d = b - a;
        const qreal len = std::hypot(d.x(), d.y());
        for (qreal t = 0; t < len; t += on + off)
            segment(a + d * (t / len), a + d * (std::min(t + on, len) / len), c);
    }
    // Local line / rect of the current item.
    void line(qreal x0, qreal y0, qreal x1, qreal y1, QRgb c, Stroke s = Stroke::Solid)
    {
        const QPointF a = map(x0, y0), b = map(x1, y1);
        if (s == Stroke::Solid)
            segment(a, b, c);
        else if (s == Stroke::Dash)
            dashed(a, b, c, kDashOn, kDashOff);
        else
            dashed(a, b, c, kDotOn, kDotOff);
    }
    void rect(const QRectF& r, QRgb c, Stroke s = Stroke::Solid)
    {
        line(r.left(), r.top(), r.right(), r.top(), c, s);
        line(r.right(), r.top(), r.right(), r.bottom(), c, s);
        line(r.right(), r.bottom(), r.left(), r.bottom(), c, s);
        line(r.left(), r.bottom(), r.left(), r.top(), c, s);
    }
    void fill(const QRectF& r, QRgb c)
    {
        if (r.width() > 0 && r.height() > 0)
            quad(map(r.left(), r.top()), map(r.right(), r.top()), map(r.right(), r.bottom()), map(r.left(), r.bottom()), c);
    }
    // Area between outer and inner (padding), as four bands.
    void ring(const QRectF& o, const QRectF& i, QRgb c)
    {
        fill(QRectF(o.left(), o.top(), o.width(), i.top() - o.top()), c);
        fill(QRectF(o.left(), i.bottom(), o.width(), o.bottom() - i.bottom()), c);
        fill(QRectF(o.left(), i.top(), i.left() - o.left(), i.height()), c);
        fill(QRectF(i.right(), i.top(), o.right() - i.right(), i.height()), c);
    }
    // 45 degree hatch inside a local rect (spacing gap), clipped to it.
    void hatch(const QRectF& r, QRgb c)
    {
        if (r.width() <= 0 || r.height() <= 0)
            return;
        line(r.left(), r.top(), r.right(), r.top(), c);
        line(r.left(), r.bottom(), r.right(), r.bottom(), c);
        // lines x - y = k, from the bottom-left to the top-right corner of r
        for (qreal k = r.left() - r.bottom(); k < r.right() - r.top(); k += kHatch) {
            // points on the line: (y + k, y); clip y to [top, bottom] and x to [left, right]
            const qreal y0 = std::max(r.top(), r.left() - k), y1 = std::min(r.bottom(), r.right() - k);
            if (y1 > y0)
                line(y0 + k, y0, y1 + k, y1, c);
        }
    }
};

qreal prop(const QQuickItem* item, const char* name) { return item->property(name).toReal(); }

QRectF padded(const QQuickItem* item, const QRectF& r)
{
    return r.adjusted(prop(item, "leftPadding"), prop(item, "topPadding"), -prop(item, "rightPadding"), -prop(item, "bottomPadding"));
}

// Equal up to half a logical pixel (layout rounding): a line drawn twice on top of each other only hides the first.
bool same(const QRectF& a, const QRectF& b)
{
    return std::abs(a.left() - b.left()) < 0.5 && std::abs(a.top() - b.top()) < 0.5
        && std::abs(a.right() - b.right()) < 0.5 && std::abs(a.bottom() - b.bottom()) < 0.5;
}

} // namespace

DebugOverlay::DebugOverlay(QQuickItem* contentItem, const Scene& scene) : QQuickItem(contentItem), m_scene(scene)
{
    setObjectName(QLatin1String(overlayObjectName)); // not counted as a scene change (app/scene_change.h)
    setFlag(ItemHasContents, true);
    setEnabled(false);
    setAcceptedMouseButtons(Qt::NoButton);
    setAcceptHoverEvents(false);
    setZ(1e9 - 1); // above the user root, below the perf overlay (1e9)
    setVisible(false);
    setSize(contentItem->size());
    QObject::connect(contentItem, &QQuickItem::widthChanged, this, [this, contentItem] { setWidth(contentItem->width()); refresh(); });
    QObject::connect(contentItem, &QQuickItem::heightChanged, this, [this, contentItem] { setHeight(contentItem->height()); refresh(); });
}

void DebugOverlay::setActive(bool on)
{
    if (on == m_active)
        return;
    m_active = on;
    setVisible(on);
    follow(window());
    refresh();
}

// While active, repaint in every frame whose polish left an item of the scene to sync (afterAnimating: after polish,
// before sync, GUI thread in both the basic loop and the render control of an offscreen scene). The overlay's own
// repaint is no such item, so a static scene renders nothing.
void DebugOverlay::follow(QQuickWindow* win)
{
    QObject::disconnect(m_follow);
    if (m_active && win) {
        m_follow = QObject::connect(win, &QQuickWindow::afterAnimating, this, [this, win] {
            if (SceneChangeCounter::itemsDirty(win))
                update();
        }, Qt::DirectConnection);
    }
}

// A window swap (Scene::moveToWindow) moves the overlay to the new window.
void DebugOverlay::itemChange(ItemChange change, const ItemChangeData& data)
{
    if (change == ItemSceneChange)
        follow(data.window);
    QQuickItem::itemChange(change, data);
}

void DebugOverlay::refresh()
{
    if (m_active)
        update();
}

void DebugOverlay::drawNode(const ElementEntry& n)
{
    const QQuickItem* item = n.item;
    Painter p{m_verts, m_lines, item, m_dpr};
    const qreal w = item->width(), h = item->height();
    // Frame = actual size: (0, 0, w, h) = SIZE, or the Auto Size result; for an auto-sized Group with children in
    // negative coordinates it starts there. Everything else is drawn relative to it.
    const QRectF frame = itemBounds(*n.xf, item);
    const bool container = n.def->container;

    // Children extent (tracked, visible, non-empty children incl. their x/y and TRANSFORM; like the Group frame, not
    // Qt's childrenRect; internals like the MultiEffect inner item are not tracked).
    QRectF children;
    bool any = false;
    if (container) {
        for (const QQuickItem* c : item->childItems()) {
            if (!m_scene.ids.count(c) || !c->isVisible() || c->width() <= 0 || c->height() <= 0)
                continue;
            const QRectF r = c->mapRectToItem(item, QRectF(0, 0, c->width(), c->height()));
            children = any ? children.united(r) : r;
            any = true;
        }
    }
    // Implicit size = what Auto Size gives. Group: extent of the children and its origin (the frame it would get,
    // also while Auto Size is off and its frame is (0, 0, SIZE)); others start at the origin.
    QRectF implicit(0, 0, item->implicitWidth(), item->implicitHeight());
    if (n.type == VLQT_ELEMENT_TYPE_GROUP && any) {
        implicit = QRectF(QPointF(std::min<qreal>(0, children.left()), std::min<qreal>(0, children.top())),
                          QPointF(std::max<qreal>(0, children.right()), std::max<qreal>(0, children.bottom())));
    }

    // Padding, spacing and overflow into the fill layer (under the lines of all nodes), all relative to the frame.
    p.area = true;
    if (n.def->layout || n.type == VLQT_ELEMENT_TYPE_TEXT)
        p.ring(frame, padded(item, frame), kPadding);
    if (n.def->layout) {
        // Gaps between consecutive laid-out children (Qt skips invisible and zero-size ones).
        const bool column = n.type == VLQT_ELEMENT_TYPE_COLUMN;
        const QRectF inner = padded(item, frame);
        const QQuickItem* prev = nullptr;
        for (const QQuickItem* c : item->childItems()) {
            if (!m_scene.ids.count(c) || !c->isVisible() || c->width() <= 0 || c->height() <= 0)
                continue;
            if (prev) {
                const QRectF gap = column ? QRectF(inner.left(), prev->y() + prev->height(), inner.width(), c->y() - prev->y() - prev->height())
                                          : QRectF(prev->x() + prev->width(), inner.top(), c->x() - prev->x() - prev->width(), inner.height());
                p.hatch(gap.intersected(frame), kSpacing); // children overflowing the frame: no spacing outside
            }
            prev = c;
        }
    }
    // Overflow: the part of the children extent outside the frame.
    if (any) {
        const QRectF inside = children.intersected(frame);
        if (inside.isEmpty())
            p.fill(children, kOverflow);
        else
            p.ring(children, inside, kOverflow);
    }
    p.area = false;

    if (any && !same(children, frame) && !same(children, implicit))
        p.rect(children, kChildren, Stroke::Dot);
    if ((takesAutoSize(n) || n.type == VLQT_ELEMENT_TYPE_TEXT) && !same(implicit, frame) && !implicit.isEmpty())
        p.rect(implicit, kImplicit);
    if (n.type == VLQT_ELEMENT_TYPE_TEXT) {
        const qreal base = item->baselineOffset();
        const QFontMetricsF fm(item->property("font").value<QFont>());
        p.line(0, base - fm.ascent(), w, base - fm.ascent(), kAscent, Stroke::Dash);
        p.line(0, base + fm.descent(), w, base + fm.descent(), kDescent, Stroke::Dash);
        p.line(0, base, w, base, kBaseline);
    }

    // Frame on top: solid = fixed SIZE, dashed = Auto Size (follows the implicit size).
    p.rect(frame, kFrame, isAutoSized(n) ? Stroke::Dash : Stroke::Solid);

    // Origin: cross in scene space at the item's (0, 0) = its top left; anchor: diamond at the anchor point (default
    // center), i.e. where the parent origin lies before TRANSFORM (outside Row/Column) and the pivot of TRANSFORM.
    const QPointF o = p.map(0, 0);
    p.segment(o - QPointF(5, 0), o + QPointF(5, 0), kOrigin, 1.5f);
    p.segment(o - QPointF(0, 5), o + QPointF(0, 5), kOrigin, 1.5f);
    const QPointF a = anchorPoint(*n.xf, item);
    const QPointF pv = p.map(a.x(), a.y());
    p.quad(pv + QPointF(0, -4), pv + QPointF(4, 0), pv + QPointF(0, 4), pv + QPointF(-4, 0), kAnchor);
}

void DebugOverlay::build()
{
    m_verts.clear();
    m_lines.clear();
    m_dpr = window() ? window()->effectiveDevicePixelRatio() : 1;
    for (const auto& kv : m_scene.nodes) {
        const ElementEntry& n = kv.second;
        if ((m_all || n.debug) && n.item->isVisible()) // isVisible: effective (also hidden ancestors, effect sources)
            drawNode(n);
    }
    m_verts.insert(m_verts.end(), m_lines.begin(), m_lines.end()); // lines after all fills
}

QSGNode* DebugOverlay::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    if (!m_active) {
        delete old;
        return nullptr;
    }
    build();
    if (isSoftwareBackend(window()))
        return paintSoftware(old);
    auto* node = static_cast<QSGGeometryNode*>(old);
    if (!node) {
        node = new QSGGeometryNode();
        auto* geo = new QSGGeometry(QSGGeometry::defaultAttributes_ColoredPoint2D(), 0);
        geo->setDrawingMode(QSGGeometry::DrawTriangles);
        geo->setVertexDataPattern(QSGGeometry::DynamicPattern);
        node->setGeometry(geo);
        node->setFlag(QSGNode::OwnsGeometry);
        node->setMaterial(new QSGVertexColorMaterial());
        node->setFlag(QSGNode::OwnsMaterial);
    }
    QSGGeometry* g = node->geometry();
    const int count = int(m_verts.size());
    if (g->vertexCount() != count)
        g->allocate(count);
    if (count)
        std::memcpy(g->vertexData(), m_verts.data(), size_t(count) * sizeof(OverlayVertex));
    node->markDirty(QSGNode::DirtyGeometry);
    return node;
}

// Software backend: custom geometry is not drawn there, so the triangles go into one image (overlay_software.h).
QSGNode* DebugOverlay::paintSoftware(QSGNode* old)
{
    return paintToImageNode(this, old, m_image, [&](QPainter& painter) {
        painter.setPen(Qt::NoPen);
        for (size_t i = 0; i + 2 < m_verts.size(); i += 3) {
            const OverlayVertex& a = m_verts[i];
            const QPointF tri[3] = {{a.x, a.y}, {m_verts[i + 1].x, m_verts[i + 1].y}, {m_verts[i + 2].x, m_verts[i + 2].y}};
            painter.setBrush(QColor::fromRgba(vertexColor(a)));
            painter.drawPolygon(tri, 3);
        }
    });
}

} // namespace vlqt
