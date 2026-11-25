// The two scene graph items of the perf overlay: the F2 line graph and the F5 timeline. Both draw the ring of a PerfMonitor.
#pragma once

#include <QtCore/QtTypes>
#include <QtGui/QColor>
#include <QtGui/QImage>
#include <QtQuick/QQuickItem>

#include <vector>

namespace vlqt {

class PerfMonitor;

// Line graph of the ring buffer; drawn with scenegraph geometry, no JS/Canvas. Qt Quick software backend (no custom
// geometry there): the lines are painted into one image shown by an image node.
class PerfGraph : public QQuickItem
{
    Q_OBJECT
public:
    explicit PerfGraph(QQuickItem* parent = nullptr);
    void setMonitor(PerfMonitor* m) { m_monitor = m; }
protected:
    QSGNode* updatePaintNode(QSGNode* old, UpdatePaintNodeData*) override;
private:
    QSGNode* paintSoftware(QSGNode* old);
    PerfMonitor* m_monitor = nullptr;
    QImage m_image; // software backend
};

// F5 Timeline page: stacked bars per pump in frame order (host: vvvv, GC, apply, F7 toggles them; Qt: events, sync,
// render, present), GPU line, budget line, over-budget highlight (Qt work), max-hold line and peak markers. No JS/Canvas:
// with QRhi two geometry nodes with vertex colors; on the Qt Quick software backend (no custom geometry there)
// one rectangle node per primitive.
class PerfTimeline : public QQuickItem
{
    Q_OBJECT
public:
    explicit PerfTimeline(QQuickItem* parent = nullptr);
    void setMonitor(PerfMonitor* m) { m_monitor = m; }
    // ms at the top edge: 2 x budget, grows with the largest bar in view up to 6 x budget (higher bars clip).
    static float scaleFor(const PerfMonitor* m);
protected:
    QSGNode* updatePaintNode(QSGNode* old, UpdatePaintNodeData*) override;
private:
    struct Prim { float l, t, r, b; QRgb color; bool marker; }; // marker: downward triangle in l..r, t..b
    void buildPrims();
    QSGNode* paintRhi(QSGNode* old);
    QSGNode* paintSoftware(QSGNode* old);

    PerfMonitor* m_monitor = nullptr;
    std::vector<Prim> m_prims;        // filled areas, back to front: m_back highlights (alpha), bars, peak markers
    int m_back = 0;
    std::vector<Prim> m_lines;        // l,t -> r,b line segments
    QImage m_image;                   // software backend: the prims painted (paintSoftware)
};

} // namespace vlqt
