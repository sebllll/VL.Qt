// The per-window perf monitor: measurement hooks, the ring of samples, the F2/F5 overlay state and the keys.
// The statistics are in perf_stats.h, the text pages in perf_pages.cpp, the graph and timeline items in perf_items.h.
#pragma once

#include "diagnostics/perf_stats.h"
#include "resources/resources.h"
#include "vlqt.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QObject>
#include <QtCore/QPointer>
#include <QtGui/QColor>
#include <QtQuick/QQuickItem>

#include <array>
#include <atomic>
#include <mutex>
#include <vector>

class QQmlEngine;
class QQuickWindow;

namespace vlqt {

class Scene;

// Non-empty (renderer name) if the window renders on the CPU: Qt Quick software backend, a software QRhi
// adapter (CpuDevice, e.g. WARP) or a Mesa software rasterizer (llvmpipe, softpipe). Valid after sceneGraphInitialized.
QString softwareRendererName(QQuickWindow* window);

// Pumps (about 1 s at 60 Hz) that the summary and the Summary page average over.
inline constexpr int kSummaryPumps = 60;

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
    static constexpr int kHistory = kPerfHistory;
    enum Flags { Meter = 1, Stats = 2, Freeze = 8 };
    enum Page { Summary, Timeline, Scene, System, Resources, PageCount };
    static constexpr int kPageShift = 4;
    // Phases for injectSpike (test): events = outside vsync/sync/render/present but inside the pump. Vsync is last to
    // keep the numbers of vlqt_test_perf_spike; the frame order (events, vsync, sync, ...) is kPhaseOrder in perf_pages.cpp.
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
    // overlay state unchanged. Defined in perf_pages.cpp.
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
    // Frame signals fire on the GUI thread for a window and on the scene's render thread for an offscreen scene,
    // concurrently with endPump. They fill m_pending and the timestamps (only that thread touches them);
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

} // namespace vlqt
