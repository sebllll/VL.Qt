#pragma once

#include "resources/resources.h"
#include "vlqt.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QObject>
#include <QtCore/QPointer>
#include <QtGui/QColor>
#include <QtGui/QImage>
#include <QtQuick/QQuickItem>

#include <array>
#include <atomic>
#include <mutex>
#include <vector>

class QQmlEngine;
class QQuickWindow;
namespace vlqt { class Scene; }

namespace vlqt {
// objectName of the F5 overlay item: its changes are no scene change (vlqt_scene_get_change_count).
inline constexpr char perfOverlayName[] = "vlqt-perf-overlay";
}

// Non-empty (renderer name) if the window renders on the CPU: Qt Quick software backend, a software QRhi
// adapter (CpuDevice, e.g. WARP) or a Mesa software rasterizer (llvmpipe, softpipe). Valid after sceneGraphInitialized.
QString softwareRendererName(QQuickWindow* window);

// Timings of one pump (vlqt_app_process_events_for), in ms. 0 = no value in this pump (no frame, no GPU timestamp).
struct PerfSample {
    float frameInterval = 0; // interval between the last two frameSwapped (time between two frames, not a duration)
    float pump = 0;    // whole processEvents
    float sync = 0;    // beforeSynchronizing -> afterSynchronizing
    float render = 0;  // beforeRendering -> afterRendering (CPU, command recording)
    float present = 0; // afterRendering -> frameSwapped (endFrame/Present)
    float gpu = 0;     // QRhi timestamps of the last completed frame, 0 = unavailable
    int swaps = 0;
    // pump minus vsync/sync/render/present: posted events, input, polish, animation ticks (and, with several windows,
    // the other windows' frames). >= 0.
    float events = 0;
    // beforeFrameBegin -> beforeSynchronizing (QRhi::beginFrame; D3D11 waits there for the refresh with VSync).
    float vsync = 0;
    // what the host (C#) measured around this pump, reported after it (vlqt_scene_report_host_pump).
    vlqt_host_pump host{};
    bool hostReported = false;
};

// Running counters of a window's pumps: since start and over the last kRecentSeconds (1 s buckets).
// No Qt, no allocation; add() is O(1), recent() sums kRecentSeconds buckets.
class PerfCounters
{
public:
    static constexpr int kRecentSeconds = 10;
    struct Counts {
        long long pumps = 0;
        long long overBudget = 0; // pump > budget
        long long longFrames = 0; // frame interval > 1.5 x budget after a pump with a frame (missed refresh)
        long long idle = 0;       // swaps == 0: no frame in this pump (mostly: nothing changed)
        long long multiSwap = 0;  // swaps > 1
    };

    void add(const PerfSample& s, double tMs, double budgetMs);
    const Counts& total() const { return m_total; }
    Counts recent(double nowMs) const;

private:
    Counts m_total;
    std::array<Counts, kRecentSeconds> m_buckets{};
    std::array<long long, kRecentSeconds> m_bucketSec{-1, -1, -1, -1, -1, -1, -1, -1, -1, -1};
    bool m_prevSwap = false; // previous pump had a frame
};

// samples[i] has a frame interval between consecutive frames (previous pump also had a frame); the interval
// after idle pumps (on-demand rendering) spans the idle time and is not a frame time.
bool consecutiveFrame(const PerfSample* samples, int i);

// Fills out from n samples (oldest to newest, the last up to PerfMonitor::kHistory are used for the percentiles)
// and the counters. Percentiles are nearest-rank; sorts copies on the stack, so call it at query time only.
void perfStats(const PerfSample* samples, int n, const PerfCounters& counters, double nowMs, double budgetMs,
    vlqt_perf_stats* out);

// The same computation on given samples with timestamps (vlqt_test_perf_eval).
void perfEval(const vlqt_perf_sample* samples, const double* tMs, int n, double budgetMs, vlqt_perf_stats* out);

// averages/maxima/rates of n host pumps (PerfMonitor::hostStats and vlqt_test_perf_host_eval). No allocation.
void perfHostStats(const vlqt_host_pump* frames, int n, vlqt_host_pump_stats* out);

// Per-window measurement, overlay (F2 perfmeter, F5 stats pages) and key handling.
// Keys (window focused, no other modifiers): F2 meter on/off; F5 Summary -> Timeline -> Scene -> System -> Resources -> off;
// Shift+F5 hides/shows the stats keeping the page; F6 (only while the stats are visible) freezes the overlay; F7 (same)
// hides/shows the host segments (vvvv, GC, apply) of the timeline, process-wide.
// The last page is remembered process-wide (new windows open on it).
// vlqt_scene_set_overlay / get_overlay flags: bit0 meter, bit1 stats, bit3 freeze, bits 4..6 = page + 1
// (set: 0 = keep the remembered page).
class PerfMonitor : public QObject
{
    Q_OBJECT
public:
    static constexpr int kHistory = 240;
    enum Flags { Meter = 1, Stats = 2, Freeze = 8 };
    enum Page { Summary, Timeline, Scene, System, Resources, PageCount };
    static constexpr int kPageShift = 4;
    // Phases for injectSpike (test): events = outside vsync/sync/render/present but inside the pump. Vsync is last to
    // keep the numbers of vlqt_test_perf_spike; the frame order (events, vsync, sync, ...) is kPhaseOrder in perf.cpp.
    enum Phase { PhaseEvents, PhaseSync, PhaseRender, PhasePresent, PhaseVsync, PhaseCount };

    PerfMonitor(QQuickWindow* window, QQmlEngine* engine);
    ~PerfMonitor() override;

    // Called by vlqt_app_process_events_for after each pump for every window.
    static void endPumpAll(float pumpMs);
    // vlqt_app_process_events_for, only for the windows of that pump.
    void endPump(float pumpMs);

    void summary(vlqt_perf* out) const;
    // last completed pump (false = none yet) and percentiles/counters/budget.
    bool lastSample(PerfSample* out) const;
    void stats(vlqt_perf_stats* out) const;
    // 1000 / refresh rate of the window's screen, 60 Hz if unknown.
    double budgetMs() const;
    int overlay() const { return m_flags | (m_frozen ? Freeze : 0) | ((m_page + 1) << kPageShift); }
    void setOverlay(int flags);
    int page() const { return m_page; }

    // test hook: busy-waits ms inside the given phase of the next rendered frame (vlqt_test_perf_spike).
    void injectSpike(int phase, float ms);
    // Element registry for the Scene page; set once after both exist, read only while that page is shown.
    void setScene(const vlqt::Scene* scene) { m_scene = scene; }
    // test hook (vlqt_test_perf_text): plain text of a page (all five; Summary/Timeline with body/foot), built on demand,
    // overlay state unchanged.
    QString pageText(int page) const;

    int count() const { return m_count; }
    // Oldest to newest; i in [0, count()).
    const PerfSample& sample(int i) const { return m_ring[(m_head + i) % kHistory]; }
    // What the stats pages show: the live ring, or the snapshot taken on freeze.
    int viewCount() const { return m_frozen ? int(m_frozenRing.size()) : m_count; }
    const PerfSample& viewSample(int i) const { return m_frozen ? m_frozenRing[i] : sample(i); }

protected:
    bool eventFilter(QObject* obj, QEvent* ev) override;

private:
    void ensureOverlay();
    void updateRenderingBadge();
    void detectRenderer();
    void updateOverlay();
    void setFrozen(bool on);
    void spin(int phase);
    QString summaryPage(QString* body) const;
    QString timelinePage(QString* foot) const;
    QString scenePage() const;
    QString systemPage() const;
    QString resourcesPage() const;
    double nowMs() const { return m_clock.nsecsElapsed() / 1e6; }

    QQuickWindow* m_window;
    QQmlEngine* m_engine;
    const vlqt::Scene* m_scene = nullptr;
    QElapsedTimer m_clock;
    std::array<PerfSample, kHistory> m_ring{};
    int m_head = 0;   // index of the oldest sample
    int m_count = 0;
    PerfSample m_cur; // main thread: endPump fills it from m_done
    PerfCounters m_counters;
    // Frame signals fire on the GUI thread for a window and on the scene's render thread for an offscreen scene (#116
    // phase 5), concurrently with endPump. They fill m_pending and the timestamps (only that thread touches them);
    // afterFrameEnd adds the finished frame to m_done under m_doneLock, and endPump takes m_done. A sample therefore holds
    // whole frames only: those finished before the pump ended (threaded scenes: usually the previous host frame's).
    PerfSample m_pending;
    PerfSample m_done;
    std::mutex m_doneLock;
    double m_tFrameBegin = -1, m_tSyncBegin = 0, m_tRenderBegin = 0, m_tRenderEnd = 0, m_tLastSwap = -1;
    int m_flags = 0;
    QString m_software; // softwareRendererName() after scenegraph init
    bool m_rendererKnown = false; // m_software is valid
    QPointer<QQuickItem> m_overlay;
    QQuickItem* m_graph = nullptr;    // child of m_overlay
    QQuickItem* m_timeline = nullptr; // child of m_overlay
    double m_lastTextMs = -1e9;
    double m_lastTimelineMs = -1e9;
    int m_page = 0;
    bool m_frozen = false;
    std::vector<PerfSample> m_frozenRing; // filled while frozen
    mutable std::vector<vlqt::res::OverviewRow> m_resRows; // Resources page: reused, no allocation once grown
    std::atomic<int> m_spikePhase{-1}; // set on the main thread, taken in a frame signal (render thread for a scene)
    std::atomic<float> m_spikeMs{0};

public:
    // host values (apply/reconcile, ops, .NET GC) of the last completed pump. false = no pump yet.
    bool reportHostPump(const vlqt_host_pump& f);
    // Last reported host pump (false = none in the ring) and averages/rates over the summary range (~1 s).
    bool lastHostPump(vlqt_host_pump* out) const;
    void hostStats(vlqt_host_pump_stats* out) const;
};

void registerPerfTypes();

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

// F5 Timeline page: stacked bars per pump in frame order (host: vvvv, GC, apply - #260, F7 toggles; Qt: events, sync,
// render, present), GPU line, budget line, over-budget highlight (Qt pump), max-hold line and peak markers. No JS/Canvas: with QRhi two geometry nodes with vertex colors; on the
// Qt Quick software backend (no custom geometry there) one rectangle node per primitive.
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
