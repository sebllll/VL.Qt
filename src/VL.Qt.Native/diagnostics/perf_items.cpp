#include "diagnostics/perf_items.h"
#include "diagnostics/perf.h"
#include "diagnostics/perf_monitor.h"
#include "diagnostics/perf_pages.h"

#include <QtGui/QPainter>
#include <QtQml/qqml.h>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGGeometryNode>
#include <QtQuick/QSGImageNode>
#include <QtQuick/QSGRendererInterface>
#include <QtQuick/QSGVertexColorMaterial>

#include <algorithm>
#include <array>
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

// F2 graph series, back to front (frame on top); the colors double as the legend in the meter text.
struct GraphSeries { QRgb color; const char* html; float (*value)(const PerfSample&); };
constexpr QRgb kGraphGuide = qRgba(255, 255, 255, 60);
const GraphSeries kGraphSeries[] = {
    {qRgb(255, 110, 180), "#ff6eb4", [](const PerfSample& s) { return s.hostReported ? s.host.vvvv : 0.f; }},
    {qRgb(255, 160, 40), "#ffa028", [](const PerfSample& s) { return s.workMs(); }},
    {qRgb(80, 220, 120), "#50dc78", [](const PerfSample& s) { return s.gpu; }},
    {qRgb(255, 255, 255), "#ffffff", [](const PerfSample& s) { return s.frameInterval; }},
};
constexpr int kGraphSeriesCount = int(std::size(kGraphSeries));

// Shared by both PerfGraph paths: y scale (ms at the top edge).
float graphYMax(const PerfMonitor* m)
{
    float yMax = 33.4f;
    for (int i = 0; i < (m ? m->count() : 0); i++) {
        const PerfSample& s = m->sample(i);
        yMax = std::max({yMax, s.frameInterval, s.workMs(), s.gpu, s.hostReported ? s.host.vvvv : 0.f});
    }
    return std::min(yMax * 1.1f, 1000.f / 15); // spikes above 66 ms clip at the top
}

// Calls point(i, ms) for every sample, oldest first; pumps without a value keep the previous one.
template <class F>
void graphSeries(const PerfMonitor* m, int series, F point)
{
    float last = 0;
    for (int i = 0; i < m->count(); i++) {
        const float val = kGraphSeries[series].value(m->sample(i));
        if (val > 0)
            last = val;
        point(i, last);
    }
}

} // namespace

// All lines (guides + series) in one vertex-colored DrawLines node: one draw call and one buffer upload per frame
// instead of one per line. Rewritten every pump (one new sample each).
QSGNode* PerfGraph::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    const QSGRendererInterface* ri = window() ? window()->rendererInterface() : nullptr;
    if (ri && ri->graphicsApi() == QSGRendererInterface::Software)
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
    const float w = float(width()), h = float(height());
    const int count = m_monitor ? m_monitor->count() : 0;
    const float yMax = graphYMax(m_monitor);
    auto y = [&](float ms) { return h - std::min(ms, yMax) / yMax * h; };
    const float step = w / float(PerfMonitor::kHistory - 1);

    QSGGeometry* g = node->geometry();
    const int segs = std::max(0, count - 1);
    g->allocate(4 + kGraphSeriesCount * segs * 2); // no-op once the ring is full
    auto* v = g->vertexDataAsColoredPoint2D();
    auto put = [&v](float px, float py, QRgb c) { (v++)->set(px, py, uchar(qRed(c)), uchar(qGreen(c)), uchar(qBlue(c)), uchar(qAlpha(c))); };
    const QRgb guide = qPremultiply(kGraphGuide);
    put(0, y(1000.f / 60), guide); put(w, y(1000.f / 60), guide);
    put(0, y(1000.f / 30), guide); put(w, y(1000.f / 30), guide);
    for (int k = 0; m_monitor && k < kGraphSeriesCount; k++) {
        const QRgb c = kGraphSeries[k].color;
        float px = 0, py = 0;
        graphSeries(m_monitor, k, [&](int i, float ms) {
            const float x = w - (count - 1 - i) * step, yy = y(ms); // newest at the right edge
            if (i > 0) { put(px, py, c); put(x, yy, c); }
            px = x; py = yy;
        });
    }
    node->markDirty(QSGNode::DirtyGeometry);
    return node;
}

// Software backend: a QSGGeometryNode is silently not drawn there (the F2 graph showed no lines). Same pattern as
// PerfTimeline::paintSoftware: paint into one image, one image node. Only while the meter is on (update() at pump rate).
QSGNode* PerfGraph::paintSoftware(QSGNode* old)
{
    auto* node = static_cast<QSGImageNode*>(old);
    if (!node) {
        node = window()->createImageNode();
        node->setOwnsTexture(true);
    }
    const qreal dpr = window()->effectiveDevicePixelRatio();
    const QSize px(std::max(1, qRound(width() * dpr)), std::max(1, qRound(height() * dpr)));
    if (m_image.size() != px)
        m_image = QImage(px, QImage::Format_ARGB32_Premultiplied);
    m_image.fill(Qt::transparent);
    const float w = float(width()), h = float(height());
    const int count = m_monitor ? m_monitor->count() : 0;
    const float yMax = graphYMax(m_monitor);
    auto y = [&](float ms) { return h - std::min(ms, yMax) / yMax * h; };
    const float step = w / float(PerfMonitor::kHistory - 1);
    {
        QPainter p(&m_image);
        p.scale(dpr, dpr);
        p.setPen(QPen(QColor::fromRgba(kGraphGuide), 0));
        p.drawLine(QPointF(0, y(1000.f / 60)), QPointF(w, y(1000.f / 60)));
        p.drawLine(QPointF(0, y(1000.f / 30)), QPointF(w, y(1000.f / 30)));
        QPointF pts[PerfMonitor::kHistory];
        for (int k = 0; m_monitor && k < kGraphSeriesCount; k++) {
            graphSeries(m_monitor, k, [&](int i, float ms) { pts[i] = QPointF(w - (count - 1 - i) * step, y(ms)); });
            p.setPen(QPen(QColor::fromRgb(kGraphSeries[k].color), 0)); // cosmetic: 1 device pixel, like the RHI lines
            p.drawPolyline(pts, count);
        }
    }
    node->setTexture(window()->createTextureFromImage(m_image));
    node->setRect(QRectF(0, 0, width(), height()));
    node->markDirty(QSGNode::DirtyMaterial);
    return node;
}

// ---------------------------------------------------------------------------

PerfTimeline::PerfTimeline(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
}

float PerfTimeline::scaleFor(const PerfMonitor* m)
{
    const float budget = float(m->budgetMs());
    float top = 2 * budget;
    for (int i = 0; i < m->viewCount(); i++) {
        const PerfSample& s = m->viewSample(i);
        top = std::max({top, stackMs(s) * 1.1f, s.gpu * 1.1f});
    }
    return std::min(top, 6 * budget);
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

QRgb rgba(const Rgb& c, int a = 255) { return qRgba(c.r, c.g, c.b, a); }

// c premultiplied (QSGVertexColorMaterial), once per prim: qPremultiply(color).
void setVertex(QSGGeometry::ColoredPoint2D& v, float x, float y, QRgb c)
{
    v.set(x, y, uchar(qRed(c)), uchar(qGreen(c)), uchar(qBlue(c)), uchar(qAlpha(c)));
}

} // namespace

void PerfTimeline::buildPrims()
{
    m_prims.clear();
    m_lines.clear();
    const int n = m_monitor ? m_monitor->viewCount() : 0;
    const float w = float(width()), h = float(height());
    const float budget = m_monitor ? float(m_monitor->budgetMs()) : 1000.f / 60;
    // Bar heights once per sample (the peak search re-evaluated them per pump); top as scaleFor().
    std::array<float, PerfMonitor::kHistory> total;
    float top = 2 * budget;
    for (int i = 0; i < n; i++) {
        total[size_t(i)] = stackMs(m_monitor->viewSample(i));
        top = std::max({top, total[size_t(i)] * 1.1f, m_monitor->viewSample(i).gpu * 1.1f});
    }
    top = std::min(top, 6 * budget);
    auto y = [&](float ms) { return h - std::clamp(ms / top, 0.f, 1.f) * h; };
    const float step = w / float(PerfMonitor::kHistory);
    const float bw = step >= 2 ? step - 1 : step;
    auto x0 = [&](int i) { return w - float(n - i) * step; }; // newest at the right edge

    float maxPump = 0;
    int peaks[3] = {-1, -1, -1};
    for (int i = 0; i < n; i++) { // over-budget pumps (Qt work): full-height red background, one quad per run
        const PerfSample& s = m_monitor->viewSample(i);
        if (s.workMs() > budget) {
            if (i > 0 && m_monitor->viewSample(i - 1).workMs() > budget)
                m_prims.back().r = x0(i) + step;
            else
                m_prims.push_back({x0(i), 0, x0(i) + step, h, qRgba(255, 60, 60, 110), false});
        }
        maxPump = std::max(maxPump, total[size_t(i)]);
        for (int k = 0; k < 3; k++) {
            if (peaks[k] < 0 || total[size_t(i)] > total[size_t(peaks[k])]) {
                for (int j = 2; j > k; j--)
                    peaks[j] = peaks[j - 1];
                peaks[k] = i;
                break;
            }
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
            m_prims.push_back({x0(i), y(acc + ms), x0(i) + bw, y(acc), rgba(segColor(seg)), false});
            acc += ms;
        }
    }
    for (int k = 0; k < 3; k++) { // peak markers above the bar
        if (peaks[k] < 0)
            break;
        const float cx = x0(peaks[k]) + bw / 2;
        const float ty = std::max(y(total[size_t(peaks[k])]) - 2, 5.f);
        m_prims.push_back({cx - 3, ty - 5, cx + 3, ty, qRgb(255, 255, 255), true});
    }

    m_lines.push_back({0, y(budget), w, y(budget), rgba(kBudgetColor), false});
    m_lines.push_back({0, y(maxPump), w, y(maxPump), qRgba(255, 255, 255, 140), false}); // max-hold
    bool gpu = false;
    for (int i = 0; i < n && !gpu; i++)
        gpu = m_monitor->viewSample(i).gpu > 0;
    if (gpu) { // pumps without a frame keep the previous GPU value
        float last = 0, lastX = 0, lastY = y(0);
        for (int i = 0; i < n; i++) {
            const float g = m_monitor->viewSample(i).gpu;
            if (g > 0)
                last = g;
            const float cx = x0(i) + bw / 2, cy = y(last);
            if (i > 0)
                m_lines.push_back({lastX, lastY, cx, cy, rgba(kGpuColor), false});
            lastX = cx;
            lastY = cy;
        }
    }
}

QSGNode* PerfTimeline::updatePaintNode(QSGNode* old, UpdatePaintNodeData*)
{
    buildPrims();
    const QSGRendererInterface* ri = window() ? window()->rendererInterface() : nullptr;
    if (ri && ri->graphicsApi() == QSGRendererInterface::Software)
        return paintSoftware(old);
    return paintRhi(old);
}

namespace {

using Vertex = QSGGeometry::ColoredPoint2D;
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
    auto* node = static_cast<QSGImageNode*>(old);
    if (!node) {
        node = window()->createImageNode();
        node->setOwnsTexture(true);
    }
    const qreal dpr = window()->effectiveDevicePixelRatio();
    const QSize px(std::max(1, qRound(width() * dpr)), std::max(1, qRound(height() * dpr)));
    if (m_image.size() != px)
        m_image = QImage(px, QImage::Format_ARGB32_Premultiplied);
    m_image.fill(Qt::transparent);
    {
        QPainter p(&m_image);
        p.scale(dpr, dpr);
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
    }
    node->setTexture(window()->createTextureFromImage(m_image));
    node->setRect(QRectF(0, 0, width(), height()));
    node->markDirty(QSGNode::DirtyMaterial);
    return node;
}

} // namespace vlqt
