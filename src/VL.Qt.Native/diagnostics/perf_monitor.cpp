#include "diagnostics/perf_monitor.h"
#include "diagnostics/overlay_software.h"
#include "diagnostics/perf.h"
#include "diagnostics/perf_items.h"
#include "diagnostics/perf_pages.h"
#include "app/scene_change.h"

#include <QtCore/QLoggingCategory>
#include <QtGui/QFontDatabase>
#include <QtGui/QKeyEvent>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
#include <rhi/qrhi.h>

#include <algorithm>
#include <utility>
#include <vector>

Q_LOGGING_CATEGORY(lcGpu, "vlqt.gpu")

namespace vlqt {

QString softwareRendererName(QQuickWindow* window)
{
    if (!window)
        return {};
    if (isSoftwareBackend(window))
        return QStringLiteral("Qt Quick software backend");
    QRhi* rhi = window->rhi();
    if (!rhi)
        return {};
    const QRhiDriverInfo info = rhi->driverInfo();
    const QString name = QString::fromUtf8(info.deviceName);
    static const char* const kSoftware[] = {"llvmpipe", "softpipe", "Software Rasterizer", "SwiftShader"};
    bool sw = info.deviceType == QRhiDriverInfo::CpuDevice;
    for (const char* s : kSoftware)
        sw = sw || name.contains(QLatin1String(s), Qt::CaseInsensitive);
    return sw ? (name.isEmpty() ? QStringLiteral("CPU device") : name) : QString();
}

namespace {

std::vector<PerfMonitor*> s_monitors;
bool s_softwareWarned = false; // once per process

constexpr double kTextIntervalMs = 250;
constexpr double kTimelineIntervalMs = 50;

int s_lastPage = 0; // F5 page shown last (any window); new windows open on it

} // namespace

PerfMonitor::PerfMonitor(QQuickWindow* window, QQmlEngine* engine)
    : QObject(window), m_window(window), m_engine(engine)
{
    m_clock.start();
    s_monitors.push_back(this);
    window->installEventFilter(this);

    // GPU rendering is the supported mode; the software backends (Qt Quick software renderer, Mesa llvmpipe, WARP) are
    // a fallback that gets this warning and, on the Qt Quick one, the reduced drawing of overlay_software.h.
    // sceneGraphInitialized alone is not enough: the Qt Quick software render loop shares one render context and
    // emits it only for the first window, so later windows detect on their first swap.
    connect(window, &QQuickWindow::sceneGraphInitialized, this, &PerfMonitor::detectRenderer, Qt::DirectConnection);
    connect(window, &QQuickWindow::frameSwapped, this, &PerfMonitor::detectRenderer,
        Qt::ConnectionType(Qt::DirectConnection | Qt::SingleShotConnection));
    // Windows (basic render loop): all signals arrive on the GUI (vvvv) thread inside processEvents. Offscreen scenes:
    // on the scene's render thread (offscreen/offscreen_render.cpp); afterFrameEnd hands the frame to endPump
    // (perf_monitor.h, m_done).
    // m_spikePhase >= 0 only after vlqt_test_perf_spike (test hook).
    // vsync wait: beforeFrameBegin -> beforeSynchronizing. The wait for the refresh happens in QRhi::beginFrame, between
    // those two signals, and Qt has no signal at its end (docs/design/timing.md, "Vsync wait"). Software backend: no
    // beforeFrameBegin, the segment is 0.
    connect(window, &QQuickWindow::beforeFrameBegin, this, [this] {
        if (m_spikePhase.load(std::memory_order_relaxed) >= 0) spin(PhaseEvents);
        m_tFrameBegin = nowMs();
    }, Qt::DirectConnection);
    connect(window, &QQuickWindow::beforeSynchronizing, this, [this] {
        if (m_spikePhase.load(std::memory_order_relaxed) >= 0) spin(m_tFrameBegin >= 0 ? PhaseVsync : PhaseEvents);
        m_tSyncBegin = nowMs();
        if (m_tFrameBegin >= 0)
            m_pending.vsync += float(m_tSyncBegin - std::exchange(m_tFrameBegin, -1.0));
    }, Qt::DirectConnection);
    connect(window, &QQuickWindow::afterSynchronizing, this, [this] {
        if (m_spikePhase.load(std::memory_order_relaxed) >= 0) spin(PhaseSync);
        m_pending.sync += float(nowMs() - m_tSyncBegin);
    }, Qt::DirectConnection);
    connect(window, &QQuickWindow::beforeRendering, this, [this] { m_tRenderBegin = nowMs(); }, Qt::DirectConnection);
    connect(window, &QQuickWindow::afterRendering, this, [this] {
        if (m_spikePhase.load(std::memory_order_relaxed) >= 0) spin(PhaseRender);
        m_tRenderEnd = nowMs();
        m_pending.render += float(m_tRenderEnd - m_tRenderBegin);
        // Valid between beginFrame and endFrame; reports the last frame the GPU has finished.
        if (QRhiSwapChain* sc = m_window->swapChain())
            if (QRhiCommandBuffer* cb = sc->currentFrameCommandBuffer())
                m_pending.gpu = float(cb->lastCompletedGpuTime() * 1000.0);
    }, Qt::DirectConnection);
    connect(window, &QQuickWindow::frameSwapped, this, [this] {
        if (m_spikePhase.load(std::memory_order_relaxed) >= 0) spin(PhasePresent);
        const double now = nowMs();
        m_pending.present += float(now - m_tRenderEnd);
        if (m_tLastSwap >= 0)
            m_pending.frameInterval = float(now - m_tLastSwap);
        m_tLastSwap = now;
        m_pending.swaps++;
    }, Qt::DirectConnection);
    // Last signal of a frame, also after a failed beginFrame of a window (no sync then). Scenes: QQuickRenderControl
    // emits it in endFrame; a scene's failed beginFrame has none, the next beforeFrameBegin starts over.
    connect(window, &QQuickWindow::afterFrameEnd, this, [this] {
        m_tFrameBegin = -1;
        {
            const std::lock_guard<std::mutex> lock(m_doneLock);
            m_done.vsync += m_pending.vsync;
            m_done.sync += m_pending.sync;
            m_done.render += m_pending.render;
            m_done.present += m_pending.present;
            m_done.swaps += m_pending.swaps;
            if (m_pending.gpu > 0)
                m_done.gpu = m_pending.gpu;
            if (m_pending.frameInterval > 0)
                m_done.frameInterval = m_pending.frameInterval;
        }
        m_pending = PerfSample{};
    }, Qt::DirectConnection);
}

void PerfMonitor::detectRenderer()
{
    m_software = softwareRendererName(m_window);
    m_rendererKnown = true;
    updateRenderingBadge();
    if (m_software.isEmpty() || s_softwareWarned)
        return;
    s_softwareWarned = true;
    qCWarning(lcGpu, "SOFTWARE RENDERING: Qt Quick renders on the CPU (%s), not on the GPU. Expect low fps. "
        "WSLg: GALLIUM_DRIVER=d3d12 selects the host GPU via Mesa d3d12.", qPrintable(m_software));
    // no extra stderr line; exports see it via a logging provider or VLQT_LOG_STDERR (app/log.cpp emitLog).
}

PerfMonitor::~PerfMonitor()
{
    s_monitors.erase(std::remove(s_monitors.begin(), s_monitors.end(), this), s_monitors.end());
    delete m_overlay;
}

void PerfMonitor::endPumpAll(float pumpMs)
{
    for (PerfMonitor* m : s_monitors)
        m->endPump(pumpMs);
}

void PerfMonitor::endPump(float pumpMs)
{
    {
        const std::lock_guard<std::mutex> lock(m_doneLock);
        m_cur = std::exchange(m_done, PerfSample{});
    }
    m_cur.pump = pumpMs;
    m_cur.events = std::max(0.0f, pumpMs - m_cur.vsync - m_cur.sync - m_cur.render - m_cur.present);
    m_budgetMs = s_frameBudgetMs;
    m_counters.add(m_cur, nowMs(), m_budgetMs);
    m_ring[(m_head + m_count) % kHistory] = m_cur;
    if (m_count < kHistory)
        m_count++;
    else
        m_head = (m_head + 1) % kHistory;
    m_cur = PerfSample{};
    if (m_flags)
        updateOverlay();
}

void PerfMonitor::summary(vlqt_perf* out) const
{
    // One more sample than the window: it decides whether the window's first interval is a consecutive one.
    const int n = std::min(m_count, kSummaryPumps + 1);
    std::array<PerfSample, kSummaryPumps + 1> s;
    for (int i = 0; i < n; i++)
        s[i] = sample(m_count - n + i);
    perfSummary(perfWindow(s.data(), n, kSummaryPumps), out);
}

bool PerfMonitor::lastSample(PerfSample* out) const
{
    if (m_count == 0)
        return false;
    *out = sample(m_count - 1);
    return true;
}

void PerfMonitor::stats(vlqt_perf_stats* out) const
{
    std::array<PerfSample, kHistory> s;
    for (int i = 0; i < m_count; i++)
        s[i] = sample(i);
    perfStats(s.data(), m_count, m_counters, nowMs(), budgetMs(), out);
}

bool PerfMonitor::reportHostPump(const vlqt_host_pump& f)
{
    if (m_count == 0)
        return false;
    PerfSample& s = m_ring[(m_head + m_count - 1) % kHistory];
    s.host = f;
    s.hostReported = true;
    return true;
}

bool PerfMonitor::lastHostPump(vlqt_host_pump* out) const
{
    if (m_count == 0 || !sample(m_count - 1).hostReported)
        return false;
    *out = sample(m_count - 1).host;
    return true;
}

void PerfMonitor::hostStats(vlqt_host_pump_stats* out) const
{
    std::array<vlqt_host_pump, kSummaryPumps> frames;
    int n = 0;
    for (int i = std::max(0, m_count - kSummaryPumps); i < m_count; i++)
        if (sample(i).hostReported)
            frames[n++] = sample(i).host;
    perfHostStats(frames.data(), n, out);
}

void PerfMonitor::setOverlay(int flags)
{
    const int old = m_flags;
    const int page = (flags >> kPageShift) & 7;
    m_flags = flags & (Meter | Stats);
    if (page >= 1 && page <= PageCount)
        m_page = page - 1;
    else if ((m_flags & Stats) && !(old & Stats))
        m_page = s_lastPage; // opened without a page: the remembered one
    if (m_flags & Stats)
        s_lastPage = m_page;
    setFrozen((flags & Freeze) && (m_flags & Stats));
    if (m_flags)
        ensureOverlay();
    if (m_overlay) {
        m_overlay->setProperty("flags", m_flags);
        m_overlay->setProperty("page", m_page);
    }
    m_lastTextMs = -1e9; // forces a text update, also while frozen
    if (m_flags)
        updateOverlay();
    if (m_overlay && m_timeline)
        m_timeline->update();
    m_window->update();
}

void PerfMonitor::setKeysEnabled(bool on)
{
    if (on == m_keys)
        return;
    m_keys = on;
    if (on)
        m_window->installEventFilter(this);
    else
        m_window->removeEventFilter(this);
}

void PerfMonitor::setFrozen(bool on)
{
    if (on == m_frozen)
        return;
    if (on) {
        m_frozenRing.resize(m_count);
        for (int i = 0; i < m_count; i++)
            m_frozenRing[i] = sample(i);
    } else {
        m_frozenRing.clear();
    }
    m_frozen = on;
}

void PerfMonitor::injectSpike(int phase, float ms)
{
    m_spikeMs.store(ms, std::memory_order_relaxed);
    m_spikePhase.store(phase >= 0 && phase < PhaseCount && ms > 0 ? phase : -1, std::memory_order_release);
}

void PerfMonitor::spin(int phase)
{
    int expected = phase;
    if (!m_spikePhase.compare_exchange_strong(expected, -1, std::memory_order_acquire))
        return;
    QElapsedTimer t;
    t.start();
    const qint64 ns = qint64(double(m_spikeMs.load(std::memory_order_relaxed)) * 1e6);
    while (t.nsecsElapsed() < ns) {
    }
}

bool PerfMonitor::eventFilter(QObject* obj, QEvent* ev)
{
    if (obj == m_window && ev->type() == QEvent::KeyPress) {
        auto* ke = static_cast<QKeyEvent*>(ev);
        const int keep = overlay() & ~(7 << kPageShift); // flags + freeze, page unchanged
        if (!ke->isAutoRepeat() && ke->modifiers() == Qt::NoModifier) {
            if (ke->key() == Qt::Key_F2) { setOverlay(keep ^ Meter); return true; }
            if (ke->key() == Qt::Key_F5) {
                if (!(m_flags & Stats))
                    setOverlay(keep | Stats); // remembered page
                else if (m_page + 1 < PageCount)
                    setOverlay(keep | ((m_page + 2) << kPageShift));
                else { // cycled through: off, next F5 starts with Summary
                    m_page = s_lastPage = Summary;
                    setOverlay(keep & ~(Stats | Freeze));
                }
                return true;
            }
            // Freeze only while the stats are visible; otherwise F6 belongs to the scene.
            if (ke->key() == Qt::Key_F6 && (m_flags & Stats)) { setOverlay(keep ^ Freeze); return true; }
            // F7 hides/shows the host segments (vvvv, GC, apply) in the timeline; same rule as F6.
            if (ke->key() == Qt::Key_F7 && (m_flags & Stats)) {
                s_hostSegments = !s_hostSegments;
                m_lastTextMs = m_lastTimelineMs = -1e9; // redraw now (also while frozen: forced text)
                updateOverlay();
                if (m_timeline)
                    m_timeline->update();
                return true;
            }
        }
        if (!ke->isAutoRepeat() && ke->modifiers() == Qt::ShiftModifier && ke->key() == Qt::Key_F5) {
            setOverlay((keep ^ Stats) & ~Freeze); // hide / show, page kept
            return true;
        }
    }
    return QObject::eventFilter(obj, ev);
}

void PerfMonitor::ensureOverlay()
{
    if (m_overlay)
        return;
    QQmlComponent comp(m_engine);
    comp.loadUrl(QUrl(QStringLiteral("qrc:/vlqt/qml/perf/perf_overlay.qml")));
    QObject* obj = comp.isError() ? nullptr : comp.create();
    auto* item = qobject_cast<QQuickItem*>(obj);
    if (!item) {
        for (const auto& e : comp.errors())
            qWarning("perf overlay: %s", qPrintable(e.toString()));
        delete obj;
        return;
    }
    QQmlEngine::setObjectOwnership(item, QQmlEngine::CppOwnership);
    // Before the item joins the scene: no layers on the Qt Quick software backend (see qml/perf/perf_overlay.qml).
    if (isSoftwareBackend(m_window))
        item->setProperty("layered", false);
    // Monospaced: Consolas where it exists (Windows), else the system fixed font (the columns are aligned with spaces).
    // One family: the overlay's QML has no font.families ("Cannot assign to non-existent property").
    static const QString fixedFamily = QFontDatabase::hasFamily(QStringLiteral("Consolas"))
        ? QStringLiteral("Consolas") : QFontDatabase::systemFont(QFontDatabase::FixedFont).family();
    item->setProperty("fontFamily", fixedFamily);
    item->setObjectName(QLatin1String(vlqt::overlayObjectName)); // not counted as a scene change (app/scene_change.h)
    item->setParentItem(m_window->contentItem());
    item->setZ(1e9); // above the user root
    auto* graph = item->findChild<PerfGraph*>(QStringLiteral("graph"));
    if (graph)
        graph->setMonitor(this);
    auto* timeline = item->findChild<PerfTimeline*>(QStringLiteral("timeline"));
    if (timeline)
        timeline->setMonitor(this);
    m_graph = graph;
    m_timeline = timeline;
    m_overlay = item;
    updateRenderingBadge();
}

// GPU/CPU badge in the meter: set on renderer detection / overlay creation, not per frame.
void PerfMonitor::updateRenderingBadge()
{
    if (m_overlay && m_rendererKnown)
        m_overlay->setProperty("rendering", m_software.isEmpty() ? 1 : 2);
}

void PerfMonitor::updateOverlay()
{
    if (!m_overlay)
        return;
    if ((m_flags & Meter) && m_graph)
        m_graph->update();
    const bool stats = m_flags & Stats;
    const double now = nowMs();
    // New timeline geometry costs a vertex fill (sync) and upload (render), not every pump; 20 Hz is smooth enough
    // to read. The timeline sits outside the panel layer, so this re-renders no text. 10 Hz measured no
    // better within the noise.
    if (stats && m_page == Timeline && !m_frozen && m_timeline && now - m_lastTimelineMs >= kTimelineIntervalMs) {
        m_lastTimelineMs = now;
        m_timeline->update();
    }

    // Text changes re-layout glyphs; a few times per second is enough to read.
    if (now - m_lastTextMs < kTextIntervalMs)
        return;
    const bool forced = m_lastTextMs < -1e8; // setOverlay: page/flags changed
    m_lastTextMs = now;

    if (m_flags & Meter) {
        vlqt_perf p;
        summary(&p);
        vlqt_host_pump_stats hs;
        hostStats(&hs);
        auto f = [](double v, int prec = 2) { return QString::number(v, 'f', prec); };
        auto opt = [&f](double v) { return v > 0 ? f(v, 1) : QStringLiteral("-"); };
        // The colours are the legend of the graph series (kGraphSeries in perf_items.cpp).
        auto col = [](const Rgb& c, const QString& text) {
            return QStringLiteral("<font color='%1'>%2</font>").arg(QLatin1String(c.html), text);
        };
        m_overlay->setProperty("meterText", QStringLiteral("%1 fps  frame %2  ").arg(opt(p.fps), opt(p.frame_ms))
            + col(kVvvvColor, QStringLiteral("vvvv ") + opt(hs.vvvv_ms)) + QStringLiteral("  ")
            + col(kGpuColor, QStringLiteral("gpu ") + opt(p.gpu_ms)) + QStringLiteral("  ")
            + col(kWorkColor, QStringLiteral("work ") + f(p.work_ms, 1)));
    }

    if (!stats || (m_frozen && !forced))
        return;
    QString body, foot;
    QString head;
    switch (m_page) {
    case Summary: head = summaryPage(&body); break;
    case Timeline: head = timelinePage(&foot); break;
    case Scene: head = scenePage(); break;
    case System: head = systemPage(); break;
    default: head = resourcesPage(); break;
    }
    m_overlay->setProperty("statsText", head);
    m_overlay->setProperty("bodyText", body);
    m_overlay->setProperty("footText", foot);
}

} // namespace vlqt
