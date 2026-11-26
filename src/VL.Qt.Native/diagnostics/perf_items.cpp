#include "diagnostics/perf_items.h"
#include "diagnostics/overlay_geometry.h"
#include "diagnostics/overlay_software.h"
#include "diagnostics/perf.h"
#include "diagnostics/perf_monitor.h"
#include "diagnostics/perf_pages.h"

#include <QtQml/qqml.h>
#include <QtQuick/QSGGeometryNode>
#include <QtQuick/QSGVertexColorMaterial>

#include <algorithm>
#include <cstring>
#include <iterator>
#include <vector>

namespace vlqt {

void registerPerfTypes()
{
    qmlRegisterType<PerfGraph>("VLQt.Perf", 1, 0, "PerfGraph");
    qmlRegisterType<PerfTimeline>("VLQt.Perf", 1, 0, "PerfTimeline");
}

PerfGraph::PerfGraph(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
}

namespace {

// F2 graph series, back to front (frame on top); the colours are the legend of the meter text (perf_monitor.cpp).
struct GraphSeries { Rgb color; float (*value)(const PerfSample&); };
constexpr QRgb kGraphGuide = qRgba(255, 255, 255, 60);
const GraphSeries kGraphSeries[] = {
    {kVvvvColor, [](const PerfSample& s) { return s.hostReported ? s.host.vvvv : 0.f; }},
    {kWorkColor, [](const PerfSample& s) { return s.workMs(); }},
    {kGpuColor, [](const PerfSample& s) { return s.gpu; }},
    {kFrameColor, [](const PerfSample& s) { return s.frameInterval; }},
};
constexpr int kGraphSeriesCount = int(std::size(kGraphSeries));

// Calls point(i, v) for i in [0, n), oldest first; entries without a value (0) keep the previous one.
template <class Value, class F>
void forEachCarried(int n, Value value, F point)
{
    float last = 0;
    for (int i = 0; i < n; i++) {
        const float v = value(i);
        if (v > 0)
            last = v;
        point(i, last);
    }
}

// The geometry both PerfGraph paths share: the item size, the live ring and the y scale (ms at the top edge).
struct GraphView {
    const PerfMonitor* monitor;
    float w, h;
    int count;
    float yMax = 33.4f;
    float step;

    GraphView(const PerfMonitor* m, const QQuickItem& item)
        : monitor(m), w(float(item.width())), h(float(item.height())), count(m ? m->count() : 0),
          step(w / float(PerfMonitor::kHistory - 1))
    {
        for (int i = 0; i < count; i++) {
            const PerfSample& s = m->sample(i);
            yMax = std::max({yMax, s.frameInterval, s.workMs(), s.gpu, s.hostReported ? s.host.vvvv : 0.f});
        }
        yMax = std::min(yMax * 1.1f, 1000.f / 15); // spikes above 66 ms clip at the top
    }
    float y(float ms) const { return h - std::min(ms, yMax) / yMax * h; }
    float x(int i) const { return w - (count - 1 - i) * step; } // newest at the right edge
    // Calls point(i, x, y) of one series.
    template <class F>
    void series(int k, F point) const
    {
        forEachCarried(count, [&](int i) { return kGraphSeries[k].value(monitor->sample(i)); },
            [&](int i, float ms) { point(i, x(i), y(ms)); });
    }
};

} // namespace

// All lines (guides + series) in one vertex-colored DrawLines node: one draw call and one buffer upload per frame
// instead of one per line. Rewritten every pump (one new sample each).
QSGNode* PerfGraph::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    if (isSoftwareBackend(window()))
        return paintSoftware(old);
    auto* node = static_cast<QSGGeometryNode*>(old);
    if (!node) {
        node = new QSGGeometryNode();
        auto* geo = new QSGGeometry(QSGGeometry::defaultAttributes_ColoredPoint2D(), 0);
        geo->setDrawingMode(QSGGeometry::DrawLines);
        geo->setLineWidth(1);
        geo->setVertexDataPattern(QSGGeometry::DynamicPattern);
        node->setGeometry(geo);
        node->setFlag(QSGNode::OwnsGeometry);
        node->setMaterial(new QSGVertexColorMaterial());
        node->setFlag(QSGNode::OwnsMaterial);
    }
    const GraphView view(m_monitor, *this);

    QSGGeometry* g = node->geometry();
    const int segs = std::max(0, view.count - 1);
    g->allocate(4 + kGraphSeriesCount * segs * 2); // no-op once the ring is full
    OverlayVertex* v = g->vertexDataAsColoredPoint2D();
    const QRgb guide = qPremultiply(kGraphGuide);
    for (float ms : {1000.f / 60, 1000.f / 30}) {
        setVertex(*v++, 0, view.y(ms), guide);
        setVertex(*v++, view.w, view.y(ms), guide);
    }
    for (int k = 0; m_monitor && k < kGraphSeriesCount; k++) {
        const QRgb c = kGraphSeries[k].color.rgba(); // opaque: premultiplied = straight
        float px = 0, py = 0;
        view.series(k, [&](int i, float x, float y) {
            if (i > 0) {
                setVertex(*v++, px, py, c);
                setVertex(*v++, x, y, c);
            }
            px = x;
            py = y;
        });
    }
    node->markDirty(QSGNode::DirtyGeometry);
    return node;
}

// Software backend: a QSGGeometryNode is silently not drawn there (the F2 graph showed no lines), so the lines are
// painted into an image (overlay_software.h). Only while the meter is on (update() at pump rate).
QSGNode* PerfGraph::paintSoftware(QSGNode* old)
{
    return paintToImageNode(this, old, m_image, [&](QPainter& p) {
        const GraphView view(m_monitor, *this);
        p.setPen(QPen(QColor::fromRgba(kGraphGuide), 0));
        p.drawLine(QPointF(0, view.y(1000.f / 60)), QPointF(view.w, view.y(1000.f / 60)));
        p.drawLine(QPointF(0, view.y(1000.f / 30)), QPointF(view.w, view.y(1000.f / 30)));
        QPointF pts[PerfMonitor::kHistory];
        for (int k = 0; m_monitor && k < kGraphSeriesCount; k++) {
            view.series(k, [&](int i, float x, float y) { pts[i] = QPointF(x, y); });
            p.setPen(QPen(QColor::fromRgb(kGraphSeries[k].color.rgba()), 0)); // cosmetic: 1 device pixel, like the RHI lines
            p.drawPolyline(pts, view.count);
        }
    });
}

// ---------------------------------------------------------------------------

PerfTimeline::PerfTimeline(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
}

TimelineView analyze(const PerfMonitor& m)
{
    TimelineView v;
    v.count = m.viewCount();
    v.budget = float(m.budgetMs());
    float top = 2 * v.budget;
    for (int i = 0; i < v.count; i++) {
        const PerfSample& s = m.viewSample(i);
        const float stack = stackMs(s);
        v.stack[size_t(i)] = stack;
        v.maxStack = std::max(v.maxStack, stack);
        top = std::max({top, stack * 1.1f, s.gpu * 1.1f});
        for (int k = 0; k < 3; k++) {
            if (v.peaks[size_t(k)] < 0 || stack > v.stack[size_t(v.peaks[size_t(k)])]) {
                for (int j = 2; j > k; j--)
                    v.peaks[size_t(j)] = v.peaks[size_t(j - 1)];
                v.peaks[size_t(k)] = i;
                break;
            }
        }
    }
    v.top = std::min(top, 6 * v.budget);
    return v;
}

namespace {

QSGGeometryNode* makeColoredNode(QSGGeometry::DrawingMode mode)
{
    auto* node = new QSGGeometryNode();
    auto* geo = new QSGGeometry(QSGGeometry::defaultAttributes_ColoredPoint2D(), 0);
    geo->setDrawingMode(mode);
    geo->setLineWidth(1);
    geo->setVertexDataPattern(QSGGeometry::DynamicPattern); // rewritten at 20 Hz
    node->setGeometry(geo);
    node->setFlag(QSGNode::OwnsGeometry);
    node->setMaterial(new QSGVertexColorMaterial());
    node->setFlag(QSGNode::OwnsMaterial);
    return node;
}

} // namespace

void PerfTimeline::buildPrims()
{
    m_prims.clear();
    m_lines.clear();
    if (!m_monitor)
        return;
    const TimelineView view = analyze(*m_monitor);
    const int n = view.count;
    const float w = float(width()), h = float(height());
    const float budget = view.budget, top = view.top;
    auto y = [&](float ms) { return h - std::clamp(ms / top, 0.f, 1.f) * h; };
    const float step = w / float(PerfMonitor::kHistory);
    const float bw = step >= 2 ? step - 1 : step;
    auto x0 = [&](int i) { return w - float(n - i) * step; }; // newest at the right edge

    for (int i = 0; i < n; i++) { // over-budget pumps (Qt work): full-height red background, one quad per run
        const PerfSample& s = m_monitor->viewSample(i);
        if (s.workMs() > budget) {
            if (i > 0 && m_monitor->viewSample(i - 1).workMs() > budget)
                m_prims.back().r = x0(i) + step;
            else
                m_prims.push_back({x0(i), 0, x0(i) + step, h, qRgba(255, 60, 60, 110), false});
        }
    }
    m_back = int(m_prims.size());
    // Stacked in frame order: [vvvv -> GC -> apply ->] events -> vsync wait -> sync -> render -> present (bottom to top).
    const int first = firstSeg();
    for (int i = 0; i < n; i++) {
        const PerfSample& s = m_monitor->viewSample(i);
        float acc = 0;
        for (int seg = first; seg < SegCount; seg++) {
            const float ms = segMs(s, seg);
            if (ms <= 0)
                continue;
            m_prims.push_back({x0(i), y(acc + ms), x0(i) + bw, y(acc), segColor(seg).rgba(), false});
            acc += ms;
        }
    }
    for (int peak : view.peaks) { // peak markers above the bar
        if (peak < 0)
            break;
        const float cx = x0(peak) + bw / 2;
        const float ty = std::max(y(view.stack[size_t(peak)]) - 2, 5.f);
        m_prims.push_back({cx - 3, ty - 5, cx + 3, ty, qRgb(255, 255, 255), true});
    }

    m_lines.push_back({0, y(budget), w, y(budget), kBudgetColor.rgba(), false});
    m_lines.push_back({0, y(view.maxStack), w, y(view.maxStack), qRgba(255, 255, 255, 140), false}); // max-hold
    bool gpu = false;
    for (int i = 0; i < n && !gpu; i++)
        gpu = m_monitor->viewSample(i).gpu > 0;
    if (gpu) { // pumps without a frame keep the previous GPU value
        float lastX = 0, lastY = y(0);
        forEachCarried(n, [&](int i) { return m_monitor->viewSample(i).gpu; }, [&](int i, float g) {
            const float cx = x0(i) + bw / 2, cy = y(g);
            if (i > 0)
                m_lines.push_back({lastX, lastY, cx, cy, kGpuColor.rgba(), false});
            lastX = cx;
            lastY = cy;
        });
    }
}

QSGNode* PerfTimeline::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    buildPrims();
    return isSoftwareBackend(window()) ? paintSoftware(old) : paintRhi(old);
}

namespace {

using Vertex = OverlayVertex;
// Vertex capacity in steps of 256 with a degenerate tail: allocate() keeps the buffer while the step stays the same.
int capacity(int n) { return (n + 255) / 256 * 256; }

template <class Prim>
void quad(Vertex*& v, const Prim& p)
{
    const QRgb c = qPremultiply(p.color);
    if (p.marker) {
        setVertex(v[0], p.l, p.t, c);
        setVertex(v[1], p.r, p.t, c);
        setVertex(v[2], (p.l + p.r) / 2, p.b, c);
        v += 3;
    } else {
        setVertex(v[0], p.l, p.t, c); setVertex(v[1], p.r, p.t, c); setVertex(v[2], p.l, p.b, c);
        setVertex(v[3], p.r, p.t, c); setVertex(v[4], p.r, p.b, c); setVertex(v[5], p.l, p.b, c);
        v += 6;
    }
}

template <class Prim>
void line(Vertex*& v, const Prim& p)
{
    const QRgb c = qPremultiply(p.color);
    setVertex(v[0], p.l, p.t, c);
    setVertex(v[1], p.r, p.b, c);
    v += 2;
}

// Writes count vertices via fill(Vertex*&) into the node, tail zeroed. onlyIfChanged: written to a scratch buffer
// first and the node left untouched (not dirty) when nothing changed.
template <class F>
void setVertices(QSGGeometryNode* node, int count, bool onlyIfChanged, F fill)
{
    QSGGeometry* g = node->geometry();
    const int cap = capacity(count);
    if (onlyIfChanged) {
        thread_local std::vector<Vertex> scratch; // one render thread per window (threaded loop)
        scratch.resize(size_t(count));
        Vertex* s = scratch.data();
        fill(s);
        if (g->vertexCount() == cap && std::memcmp(g->vertexData(), scratch.data(), size_t(count) * sizeof(Vertex)) == 0)
            return; // the tail is zero either way
        if (g->vertexCount() != cap)
            g->allocate(cap);
        if (count)
            std::memcpy(g->vertexData(), scratch.data(), size_t(count) * sizeof(Vertex));
    } else {
        if (g->vertexCount() != cap)
            g->allocate(cap);
        Vertex* v = g->vertexDataAsColoredPoint2D();
        fill(v);
    }
    std::memset(static_cast<void*>(g->vertexDataAsColoredPoint2D() + count), 0, size_t(cap - count) * sizeof(Vertex));
    node->markDirty(QSGNode::DirtyGeometry);
}

} // namespace

// Four nodes, drawn in this order: over-budget highlights, bars + peak markers, budget + GPU lines, max-hold
// line. An update rewrites bars and lines; highlights and max-hold are marked dirty only when they change, which in a
// steady view is rare (max-hold) or never (no highlights): every dirty node makes the batch renderer re-batch.
// A ring of chunk nodes and opaque bar materials measured slower (docs/design/timing.md, "Timeline geometry").
QSGNode* PerfTimeline::paintRhi(QSGNode* old)
{
    QSGNode* root = old;
    if (!root) {
        root = new QSGNode();
        root->appendChildNode(makeColoredNode(QSGGeometry::DrawTriangles));       // highlights
        root->appendChildNode(makeColoredNode(QSGGeometry::DrawTriangles));       // bars, peak markers
        root->appendChildNode(makeColoredNode(QSGGeometry::DrawLines));           // budget, GPU
        root->appendChildNode(makeColoredNode(QSGGeometry::DrawLines));           // max-hold
    }
    auto node = [root](int i) { return static_cast<QSGGeometryNode*>(root->childAtIndex(i)); };
    const int back = std::min(m_back, int(m_prims.size()));

    setVertices(node(0), back * 6, true, [&](Vertex*& v) {
        for (int i = 0; i < back; i++)
            quad(v, m_prims[size_t(i)]);
    });
    int verts = 0;
    for (size_t i = size_t(back); i < m_prims.size(); i++)
        verts += m_prims[i].marker ? 3 : 6;
    setVertices(node(1), verts, false, [&](Vertex*& v) {
        for (size_t i = size_t(back); i < m_prims.size(); i++)
            quad(v, m_prims[i]);
    });
    // m_lines (buildPrims): budget, max-hold, then the GPU segments.
    const int lines = int(m_lines.size());
    setVertices(node(2), std::max(0, lines - 1) * 2, false, [&](Vertex*& v) {
        for (int i = 0; i < lines; i++)
            if (i != 1)
                line(v, m_lines[size_t(i)]);
    });
    setVertices(node(3), lines > 1 ? 2 : 0, true, [&](Vertex*& v) {
        if (lines > 1)
            line(v, m_lines[1]);
    });
    return root;
}

// Software backend: no custom geometry there, and many rectangle nodes make its dirty-region pass slow (a pool of
// ~1400 cost 100+ ms per update). So the prims are painted into one image, shown by a single image node.
QSGNode* PerfTimeline::paintSoftware(QSGNode* old)
{
    return paintToImageNode(this, old, m_image, [&](QPainter& p) {
        for (const Prim& r : m_prims) {
            const QColor c = QColor::fromRgba(r.color);
            if (r.marker) {
                const QPointF tri[3] = {{r.l, r.t}, {r.r, r.t}, {(r.l + r.r) / 2, r.b}};
                p.setRenderHint(QPainter::Antialiasing, true);
                p.setPen(Qt::NoPen);
                p.setBrush(c);
                p.drawPolygon(tri, 3);
                p.setRenderHint(QPainter::Antialiasing, false);
            } else {
                p.fillRect(QRectF(r.l, r.t, r.r - r.l, r.b - r.t), c);
            }
        }
        for (const Prim& l : m_lines) {
            p.setPen(QPen(QColor::fromRgba(l.color), 0)); // cosmetic: 1 device pixel, like the RHI lines
            p.drawLine(QPointF(l.l, l.t), QPointF(l.r, l.b));
        }
    });
}

} // namespace vlqt
