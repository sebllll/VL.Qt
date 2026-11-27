// The per-window perf monitor: measurement hooks, the ring of samples, the F2/F5 overlay state and the keys.
// The statistics are in perf_stats.h, the text pages in perf_pages.cpp, the graph and timeline items in perf_items.h.
//
// Threading contract. A monitor is created on the GUI (vvvv) thread and belongs to it, except for what is listed here.
//  - GUI thread only: the ring (m_ring, m_head, m_count), m_cur, m_counters, the overlay (flags, page, frozen state,
//    m_overlay and its children, every QML property including "rendering"), m_renderer/m_rendererKnown, m_keys,
//    m_budgetMs and s_frameBudgetMs (set and read in the pump), m_scene, m_frozenRing, m_resRows, the text pages and
//    every vlqt_* export that reads the monitor. endPump runs here.
//  - Signal thread: the frame signals of the window (connected in the constructor) fill m_pending and the timestamps
//    (m_tFrameBegin ... m_tLastSwap); only that thread touches them. For a window it is the GUI thread, inside
//    processEvents; for an offscreen scene it is the scene's render thread (offscreen/offscreen_render.cpp), which also
//    runs detectRenderer.
//  - Shared: m_done under m_doneLock (afterFrameEnd adds a finished frame, endPump takes it: the only hand-off of
//    measurements). The process-wide renderer warning is a std::atomic<bool> in device/renderer_info.cpp.
//  - Render thread to GUI thread: detectRenderer reads the renderer on the signal thread, where the QRhi is valid, and
//    hands the result to publishRenderer on the GUI thread (a queued call when the signal thread is another one). It
//    never writes a QML property or a member of the GUI side from the signal thread.
//  - Item painting: PerfGraph and PerfTimeline read the ring in updatePaintNode, which runs inside the scene graph
//    sync. The GUI thread cannot be in endPump then: a window syncs on the GUI thread itself; a scene syncs in the
//    blocking RenderThread::call of beginAndSync (offscreen_render.cpp), during which the GUI thread waits. A software
//    batch also renders in a blocking call; a hardware batch renders in post() and never touches the ring.
#pragma once

#include "device/renderer_info.h"
#include "diagnostics/perf_stats.h"
#include "resources/resources.h"
#include "vlqt.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QObject>
#include <QtCore/QPointer>
#include <QtGui/QColor>
#include <QtQuick/QQuickItem>

#include <array>
#include <mutex>
#include <vector>

class QQmlEngine;
class QQuickWindow;

namespace vlqt {

class Scene;

// Per-window measurement, overlay (F2 perfmeter, F5 stats pages) and key handling.
// Keys (window focused, no other modifiers; setKeysEnabled(false) turns all of them off): F2 meter on/off; F5 Summary -> Timeline -> Scene
// -> System -> Resources -> off;
// Shift+F5 hides/shows the stats keeping the page; F6 (only while the stats are visible) freezes the overlay; F7 (same)
// hides/shows the host segments (vvvv, GC, apply) of the timeline, process-wide.
// The last page is remembered process-wide (new windows open on it).
// vlqt_test_set_overlay / vlqt_scene_get_overlay flags: bit0 meter, bit1 stats, bit3 freeze, bits 4..6 = page + 1
// (set: 0 = keep the remembered page).
class PerfMonitor : public QObject // no Q_OBJECT: no signals, slots or properties; the connects use member pointers
{
public:
    static constexpr int kHistory = kPerfHistory;
    enum Flags { Meter = 1, Stats = 2, Freeze = 8 };
    enum Page { Summary, Timeline, Scene, System, Resources, PageCount };
    static constexpr int kPageShift = 4;
    // The Qt phases of a pump in frame order (timeline bottom -> top, summary rows, injectSpike): events = outside
    // vsync/sync/render/present but inside the pump.
    enum QtPhase { PhaseEvents, PhaseVsync, PhaseSync, PhaseRender, PhasePresent, PhaseCount };

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
    // The frame budget: vvvv's frame interval (vlqt_app_set_frame_budget), taken at the end of each pump of the window.
    double budgetMs() const { return m_budgetMs; }
    // vlqt_app_set_frame_budget: the budget the next pumps take (process-wide, 1000 / 60 until set).
    static void setFrameBudget(double ms) { s_frameBudgetMs = ms; }
    int overlay() const { return m_flags | (m_frozen ? Freeze : 0) | ((m_page + 1) << kPageShift); }
    void setOverlay(int flags);
    int page() const { return m_page; }
    // The diagnostic keys (F2, F5, Shift+F5, F6, F7): on = the event filter swallows them, off = it is removed and the
    // keys reach the scene (Renderer [Qt] Enable Keyboard Shortcuts). On by default; the overlay itself is unchanged.
    void setKeysEnabled(bool on);

    // test hook: busy-waits ms inside the given QtPhase of the next rendered frame (vlqt_test_perf_spike). A one-shot
    // connection to a frame signal, so a monitor with nothing injected has no test code in its frame path.
    void injectSpike(int phase, float ms);
    // Element registry for the Scene page; set once after both exist, read only while that page is shown.
    void setScene(const vlqt::Scene* scene) { m_scene = scene; }

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
    void publishRenderer(RendererInfo info);
    void updateOverlay();
    void setFrozen(bool on);
    // The text of the pages (perf_pages.cpp).
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
    static inline double s_frameBudgetMs = 1000.0 / 60;
    double m_budgetMs = s_frameBudgetMs;
    // Frame signals fire on the GUI thread for a window and on the scene's render thread for an offscreen scene,
    // concurrently with endPump. They fill m_pending and the timestamps (only that thread touches them);
    // afterFrameEnd adds the finished frame to m_done under m_doneLock, and endPump takes m_done. A sample therefore holds
    // whole frames only: those finished before the pump ended (threaded scenes: usually the previous host frame's).
    PerfSample m_pending;
    PerfSample m_done;
    std::mutex m_doneLock;
    double m_tFrameBegin = -1, m_tSyncBegin = 0, m_tRenderBegin = 0, m_tRenderEnd = 0, m_tLastSwap = -1;
    int m_flags = 0;
    bool m_keys = true; // the event filter is installed on the window
    RendererInfo m_renderer; // describeRenderer() after scenegraph init, published on the GUI thread
    bool m_rendererKnown = false; // m_renderer is valid
    QPointer<QQuickItem> m_overlay;
    QQuickItem* m_graph = nullptr;    // child of m_overlay
    QQuickItem* m_timeline = nullptr; // child of m_overlay
    double m_lastTextMs = -1e9;
    double m_lastTimelineMs = -1e9;
    int m_page = 0;
    bool m_frozen = false;
    std::vector<PerfSample> m_frozenRing; // filled while frozen
    mutable std::vector<vlqt::res::OverviewRow> m_resRows; // Resources page: reused, no allocation once grown
    QMetaObject::Connection m_spike; // the pending injectSpike, if any

    friend QString perfPageText(const PerfMonitor& monitor, int page); // perf_pages.h

public:
    // host values (apply/reconcile, ops, .NET GC) of the last completed pump. false = no pump yet.
    bool reportHostPump(const vlqt_host_pump& f);
    // Last reported host pump (false = none in the ring) and averages/rates over the summary range (~1 s).
    bool lastHostPump(vlqt_host_pump* out) const;
    void hostStats(vlqt_host_pump_stats* out) const;
};

} // namespace vlqt
