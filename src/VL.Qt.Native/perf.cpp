#include "perf.h"
#include "commands.h"
#include "snapshot_resource.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/private/qabstractanimation_p.h> // QUnifiedTimer::runningAnimationCount (CorePrivate via GuiPrivate)
#include <QtGui/QKeyEvent>
#include <QtGui/QPainter>
#include <QtGui/QScreen>
#include <QtGui/QTextDocumentFragment>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGGeometryNode>
#include <QtQuick/QSGImageNode>
#include <QtQuick/QSGRendererInterface>
#include <QtQuick/QSGVertexColorMaterial>
#include <rhi/qrhi.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

#ifdef Q_OS_WIN
#include <dxgi1_5.h>
#endif

Q_LOGGING_CATEGORY(lcGpu, "vlqt.gpu")

QString softwareRendererName(QQuickWindow* window)
{
    if (!window)
        return {};
    if (window->rendererInterface() && window->rendererInterface()->graphicsApi() == QSGRendererInterface::Software)
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

constexpr int kSummaryPumps = 60; // ~1 s at 60 Hz
constexpr double kOnDemandIdleShare = 0.25; // Summary shows "on demand" above this share of idle pumps
constexpr double kTextIntervalMs = 250;
constexpr double kTimelineIntervalMs = 50;


const char* kOverlayQml = R"(import QtQuick
import VLQt.Perf
Item {
  id: root
  property int flags: 0
  property string meterText: ""
  property string statsText: ""
  property string bodyText: ""
  property string footText: ""
  property int page: 0
  property real barUse: 0
  property real barP95: 0
  property color barColor: "#50dc78"
  property int rendering: 0
  property bool layered: true
  enabled: false // display only, never takes input
  Item {
    id: meter; visible: root.flags & 1
    x: 8; y: 8; width: 340; height: 72
    Rectangle { // background, text, badge: own texture like the F5 panel, re-rendered only when the text changes (#265)
      anchors.fill: parent; radius: 3; color: "#c0000000"
      layer.enabled: root.layered
      Text { x: 6; y: 3; text: root.meterText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
      Rectangle { // 1 = GPU (hardware), 2 = CPU (software rendering); 0 = not known yet
        visible: root.rendering > 0
        anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 4
        width: badge.implicitWidth + 6; height: badge.implicitHeight + 2
        color: root.rendering === 2 ? "#e03030" : "#30a050"
        Text { id: badge; anchors.centerIn: parent; text: root.rendering === 2 ? "CPU" : "GPU"
               color: "white"; font.family: "Consolas"; font.pixelSize: 9; font.bold: true }
      }
    }
    PerfGraph { objectName: "graph"; anchors.fill: parent; anchors.margins: 4; anchors.topMargin: 18 }
  }
  Rectangle { // F5 pages: 0 Summary, 1 Timeline, 2 Scene, 3 System, 4 Resources
    id: panel
    visible: root.flags & 2
    x: 8; y: (root.flags & 1) ? meter.y + meter.height + 6 : 8
    width: col.width + 12; height: col.height + 8; radius: 3; color: "#d0000000"
    // Own texture, re-rendered only when the text changes (4 Hz): the scene's per-frame batch updates then see one
    // quad instead of the glyphs. Off on the Qt Quick software backend (layered = false): there the layer draws
    // its content shifted up plus an opaque white area.
    layer.enabled: root.layered
    Column {
      id: col; x: 6; y: 4; spacing: 4
      Text { text: root.statsText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
      Item { // Summary: pump avg against the budget, white tick = p95
        visible: root.page === 0
        width: 300; height: 6
        Rectangle { anchors.fill: parent; color: "#40ffffff" }
        Rectangle { width: Math.min(root.barUse, 1) * parent.width; height: parent.height; color: root.barColor }
        Rectangle { visible: root.barP95 > 0; x: Math.min(root.barP95, 1) * (parent.width - 2); width: 2; height: parent.height; color: "white" }
      }
      Text { visible: text !== ""; text: root.bodyText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
      Item { id: slot; visible: root.page === 1; width: 480; height: 140 } // the timeline's place
      Text { visible: root.page === 1 && text !== ""; text: root.footText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
    }
  }
  // Outside the panel layer: a timeline update only rewrites its vertex buffers, no layer render pass and no text
  // re-render.
  PerfTimeline {
    objectName: "timeline"; visible: panel.visible && root.page === 1
    x: panel.x + col.x + slot.x; y: panel.y + col.y + slot.y; width: slot.width; height: slot.height
  }
}
)";

int s_lastPage = 0; // F5 page shown last (any window); new windows open on it

// Colors: phases (timeline bars + summary rows) and thresholds against the budget.
struct Rgb { int r, g, b; const char* html; };
constexpr Rgb kPhaseColor[PerfMonitor::PhaseCount] = {
    {90, 156, 255, "#5a9cff"},  // events
    {192, 128, 255, "#c080ff"}, // sync
    {255, 160, 40, "#ffa028"},  // render
    {64, 208, 208, "#40d0d0"},  // present
    {176, 196, 222, "#b0c4de"}, // vsync wait
};
constexpr Rgb kGpuColor{80, 220, 120, "#50dc78"};
constexpr Rgb kBudgetColor{255, 208, 64, "#ffd040"};
const char* const kGreen = "#50dc78";
const char* const kYellow = "#ffd040";
const char* const kRed = "#ff5050";
const char* const kGrey = "#909090";
const char* const kPhaseName[PerfMonitor::PhaseCount] = {"events", "sync", "render", "present", "vsync wait"};
// Frame order of the phases (timeline bottom -> top, summary rows): the vsync wait sits between the events and the
// sync. The enum keeps vsync last, so the phase numbers of vlqt_test_perf_spike stay.
constexpr int kPhaseOrder[PerfMonitor::PhaseCount] = {PerfMonitor::PhaseEvents, PerfMonitor::PhaseVsync,
    PerfMonitor::PhaseSync, PerfMonitor::PhaseRender, PerfMonitor::PhasePresent};
const char* const kPageName[PerfMonitor::PageCount] = {"SUMMARY", "TIMELINE", "SCENE", "SYSTEM", "RESOURCES"};

#ifdef Q_OS_WIN
// Same query as QRhiD3D11::create(): IDXGIFactory5::CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING),
// qtbase v6.12.0 src/gui/rhi/qrhid3d11.cpp:211-219 (supportsAllowTearing). Asked once per process.
bool dxgiAllowTearingSupported()
{
    static const bool supported = [] {
        bool result = false;
        IDXGIFactory1* factory = nullptr;
        if (SUCCEEDED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), reinterpret_cast<void**>(&factory)))) {
            IDXGIFactory5* factory5 = nullptr;
            if (SUCCEEDED(factory->QueryInterface(__uuidof(IDXGIFactory5), reinterpret_cast<void**>(&factory5)))) {
                BOOL allowTearing = FALSE;
                if (SUCCEEDED(factory5->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allowTearing, sizeof(allowTearing))))
                    result = allowTearing != FALSE;
                factory5->Release();
            }
            factory->Release();
        }
        return result;
    }();
    return supported;
}
#endif

float phaseMs(const PerfSample& s, int phase)
{
    switch (phase) {
    case PerfMonitor::PhaseEvents: return s.events;
    case PerfMonitor::PhaseSync: return s.sync;
    case PerfMonitor::PhaseRender: return s.render;
    case PerfMonitor::PhaseVsync: return s.vsync;
    default: return s.present;
    }
}

// timeline segments, in frame order bottom -> top: the host side of the frame before the Qt pump (vvvv
// update, .NET GC pause, apply), then the Qt phases. Host values come from PerfSample.host (0 without a report).
// GC is carved out of the vvvv segment: the pause is reported per frame without its position, and most collections
// fall into the vvvv update (allocations of the patch); a pause longer than the vvvv update is shown in full.
// F7 (stats visible) hides/shows the host segments (s_hostSegments, process-wide like the page).
enum Segment { SegVvvv, SegGc, SegApply, SegQt };
constexpr int SegCount = SegQt + int(PerfMonitor::PhaseCount);
constexpr Rgb kHostColor[SegQt] = {
    {255, 110, 180, "#ff6eb4"}, // vvvv (same as the F2 line)
    {176, 112, 64, "#b07040"},  // GC
    {160, 160, 184, "#a0a0b8"}, // apply
};
const char* const kHostName[SegQt] = {"vvvv", "GC", "apply"};
bool s_hostSegments = true;

const Rgb& segColor(int seg) { return seg < SegQt ? kHostColor[seg] : kPhaseColor[kPhaseOrder[seg - SegQt]]; }
const char* segName(int seg) { return seg < SegQt ? kHostName[seg] : kPhaseName[kPhaseOrder[seg - SegQt]]; }
float segMs(const PerfSample& s, int seg)
{
    switch (seg) {
    case SegVvvv: return s.hostReported ? std::max(0.f, s.host.vvvv - s.host.gc_pause) : 0;
    case SegGc: return s.hostReported ? s.host.gc_pause : 0;
    case SegApply: return s.hostReported ? s.host.apply : 0;
    default: return phaseMs(s, kPhaseOrder[seg - SegQt]);
    }
}
// First segment shown (host segments off: the Qt phases only).
int firstSeg() { return s_hostSegments ? 0 : SegQt; }
// Bar height: the shown segments; = pump without host segments (events = pump - vsync - sync - render - present).
float stackMs(const PerfSample& s)
{
    float sum = 0;
    for (int seg = firstSeg(); seg < SegQt; seg++)
        sum += segMs(s, seg);
    return sum + std::max(s.pump, s.events + s.vsync + s.sync + s.render + s.present);
}

// Pump (Qt's share of a vvvv frame): green < half the budget, yellow < budget, red above.
const char* pumpColor(double ms, double budget) { return ms < 0.5 * budget ? kGreen : ms < budget ? kYellow : kRed; }
// Frame interval: green up to 1.1 x budget, yellow up to 1.5 x (one refresh late), red above.
const char* frameColor(double ms, double budget) { return ms <= 1.1 * budget ? kGreen : ms <= 1.5 * budget ? kYellow : kRed; }

// StyledText builder: spaces as &nbsp; so monospaced columns stay aligned.
class Html
{
public:
    Html& t(const QString& s)
    {
        for (QChar c : s) {
            if (c == u' ') m_s += QLatin1String("&nbsp;");
            else if (c == u'<') m_s += QLatin1String("&lt;");
            else if (c == u'>') m_s += QLatin1String("&gt;");
            else if (c == u'&') m_s += QLatin1String("&amp;");
            else m_s += c;
        }
        return *this;
    }
    Html& t(const char* s) { return t(QString::fromLatin1(s)); }
    Html& c(const char* color, const QString& s)
    {
        m_s += QLatin1String("<font color='") + QLatin1String(color) + QLatin1String("'>");
        t(s);
        m_s += QLatin1String("</font>");
        return *this;
    }
    Html& c(const char* color, const char* s) { return c(color, QString::fromLatin1(s)); }
    Html& br() { m_s += QLatin1String("<br>"); return *this; }
    const QString& str() const { return m_s; }
private:
    QString m_s;
};

QString num(double v, int width, int prec = 2) { return QString::number(v, 'f', prec).rightJustified(width); }
QString lab(const char* s, int width) { return QString::fromLatin1(s).leftJustified(width); }

// skip, skip2: overlays (perf, debug) with their internals
int countItems(const QQuickItem* item, const QQuickItem* skip, const QQuickItem* skip2)
{
    int n = 0;
    for (const QQuickItem* c : item->childItems())
        if (c != skip && c != skip2)
            n += 1 + countItems(c, skip, skip2);
    return n;
}

// MultiEffect items below item (effect layers, plan effects/01), overlays skipped.
int countMultiEffects(const QQuickItem* item, const QQuickItem* skip, const QQuickItem* skip2)
{
    int n = 0;
    for (const QQuickItem* c : item->childItems())
        if (c != skip && c != skip2)
            n += (c->inherits("QQuickMultiEffect") ? 1 : 0) + countMultiEffects(c, skip, skip2);
    return n;
}

} // namespace

void registerPerfTypes()
{
    qmlRegisterType<PerfGraph>("VLQt.Perf", 1, 0, "PerfGraph");
    qmlRegisterType<PerfTimeline>("VLQt.Perf", 1, 0, "PerfTimeline");
}

PerfMonitor::PerfMonitor(QQuickWindow* window, QQmlEngine* engine)
    : QObject(window), m_window(window), m_engine(engine)
{
    m_clock.start();
    s_monitors.push_back(this);
    window->installEventFilter(this);

    // GPU rendering is required (software rendering is not supported): warn loudly when Qt fell back to the CPU.
    // sceneGraphInitialized alone is not enough: the Qt Quick software render loop shares one render context and
    // emits it only for the first window, so later windows detect on their first swap.
    connect(window, &QQuickWindow::sceneGraphInitialized, this, &PerfMonitor::detectRenderer, Qt::DirectConnection);
    connect(window, &QQuickWindow::frameSwapped, this, &PerfMonitor::detectRenderer,
        Qt::ConnectionType(Qt::DirectConnection | Qt::SingleShotConnection));
    // Windows (basic render loop): all signals arrive on the GUI (vvvv) thread inside processEvents. Offscreen scenes:
    // on the scene's render thread (offscreen.cpp); afterFrameEnd hands the frame to endPump (perf.h, m_done).
    // m_spikePhase >= 0 only after vlqt_test_perf_spike (test hook).
    // vsync wait: beforeFrameBegin -> beforeSynchronizing. QSGGuiThreadRenderLoop::renderWindow emits
    // beforeFrameBegin right before QRhi::beginFrame, and syncSceneGraph emits beforeSynchronizing right after it; Qt has
    // no signal at the end of beginFrame. With D3D11 beginFrame waits on the frame latency waitable object
    // (qrhid3d11.cpp:1489-1496, v6.12.0, max frame latency 2): with VSync that is where the wait for the refresh lands,
    // Present(1) itself hardly blocks. Without VSync the same wait shows the GPU being behind. Also in this segment: the
    // rest of beginFrame (reading back the previous frame's timestamps), prepareSync and the beforeSynchronizing slots
    // connected before this one (the UpdateGate's dirty check, short). Software backend: no beforeFrameBegin, 0.
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
    m_counters.add(m_cur, nowMs(), budgetMs());
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
    *out = vlqt_perf{};
    const int n = std::min(m_count, kSummaryPumps);
    int frames = 0, gpus = 0, swaps = 0;
    for (int i = m_count - n; i < m_count; i++) {
        const PerfSample& s = sample(i);
        out->pump_ms += s.pump;
        out->pump_ms_max = std::max(out->pump_ms_max, double(s.pump));
        swaps += s.swaps;
        if (s.swaps == 0)
            continue;
        if (s.frameInterval > 0) {
            out->frame_ms += s.frameInterval;
            out->frame_ms_max = std::max(out->frame_ms_max, double(s.frameInterval));
            frames++;
        }
        out->sync_ms += s.sync / s.swaps;
        out->render_ms += s.render / s.swaps;
        out->present_ms += s.present / s.swaps;
        out->vsync_ms += s.vsync / s.swaps;
        if (s.gpu > 0) {
            out->gpu_ms += s.gpu;
            out->gpu_ms_max = std::max(out->gpu_ms_max, double(s.gpu));
            gpus++;
        }
    }
    out->samples = n;
    if (n == 0)
        return;
    out->pump_ms /= n;
    out->swaps_per_pump = double(swaps) / n;
    int pumpsWithSwap = 0;
    for (int i = m_count - n; i < m_count; i++)
        pumpsWithSwap += sample(i).swaps > 0;
    if (pumpsWithSwap) {
        out->sync_ms /= pumpsWithSwap;
        out->render_ms /= pumpsWithSwap;
        out->present_ms /= pumpsWithSwap;
        out->vsync_ms /= pumpsWithSwap;
    }
    if (frames) {
        out->frame_ms /= frames;
        out->fps = 1000.0 / out->frame_ms;
    }
    if (gpus)
        out->gpu_ms /= gpus;
}

double PerfMonitor::budgetMs() const
{
    // QScreen::refreshRate is a cached value; cheap enough per pump.
    const QScreen* screen = m_window->screen();
    const double hz = screen ? screen->refreshRate() : 0;
    return 1000.0 / (hz >= 1 && hz <= 1000 ? hz : 60.0);
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

void PerfCounters::add(const PerfSample& s, double tMs, double budgetMs)
{
    const long long sec = (long long)std::floor(tMs / 1000.0);
    const int b = int(((sec % kRecentSeconds) + kRecentSeconds) % kRecentSeconds);
    if (m_bucketSec[b] != sec) {
        m_buckets[b] = Counts{};
        m_bucketSec[b] = sec;
    }
    const bool over = s.pump > budgetMs;
    // after idle pumps (on-demand rendering) the interval spans the idle time, not a missed refresh.
    const bool longFrame = s.swaps > 0 && m_prevSwap && s.frameInterval > 1.5 * budgetMs;
    m_prevSwap = s.swaps > 0;
    for (Counts* c : {&m_total, &m_buckets[b]}) {
        c->pumps++;
        c->overBudget += over;
        c->longFrames += longFrame;
        c->idle += s.swaps == 0;
        c->multiSwap += s.swaps > 1;
    }
}

PerfCounters::Counts PerfCounters::recent(double nowMs) const
{
    const long long now = (long long)std::floor(nowMs / 1000.0);
    Counts r;
    for (int b = 0; b < kRecentSeconds; b++) {
        if (m_bucketSec[b] <= now - kRecentSeconds || m_bucketSec[b] > now)
            continue;
        const Counts& c = m_buckets[b];
        r.pumps += c.pumps;
        r.overBudget += c.overBudget;
        r.longFrames += c.longFrames;
        r.idle += c.idle;
        r.multiSwap += c.multiSwap;
    }
    return r;
}

namespace {

// Nearest rank on sorted v[0..n): smallest value with at least p of the values <= it.
double percentile(const float* v, int n, double p)
{
    if (n <= 0)
        return 0;
    int k = int(std::ceil(p * n)) - 1;
    return v[std::clamp(k, 0, n - 1)];
}

} // namespace

bool consecutiveFrame(const PerfSample* samples, int i)
{
    return samples[i].swaps > 0 && samples[i].frameInterval > 0 && i > 0 && samples[i - 1].swaps > 0;
}

void perfStats(const PerfSample* samples, int n, const PerfCounters& counters, double nowMs, double budgetMs,
    vlqt_perf_stats* out)
{
    *out = vlqt_perf_stats{};
    out->budget_ms = budgetMs;
    const int first = std::max(0, n - PerfMonitor::kHistory);
    std::array<float, PerfMonitor::kHistory> pump, frame;
    int np = 0, nf = 0;
    for (int i = first; i < n; i++) {
        const PerfSample& s = samples[i];
        pump[np++] = s.pump;
        if (consecutiveFrame(samples, i))
            frame[nf++] = s.frameInterval;
    }
    std::sort(pump.begin(), pump.begin() + np);
    std::sort(frame.begin(), frame.begin() + nf);
    out->samples = np;
    out->frames = nf;
    out->pump_p50 = percentile(pump.data(), np, 0.50);
    out->pump_p95 = percentile(pump.data(), np, 0.95);
    out->pump_p99 = percentile(pump.data(), np, 0.99);
    out->pump_max = np ? pump[np - 1] : 0;
    out->frame_p50 = percentile(frame.data(), nf, 0.50);
    out->frame_p95 = percentile(frame.data(), nf, 0.95);
    out->frame_p99 = percentile(frame.data(), nf, 0.99);
    out->frame_max = nf ? frame[nf - 1] : 0;

    const PerfCounters::Counts& t = counters.total();
    out->pumps = t.pumps;
    out->over_budget = t.overBudget;
    out->long_frames = t.longFrames;
    out->idle = t.idle;
    out->multi_swap = t.multiSwap;
    const PerfCounters::Counts r = counters.recent(nowMs);
    out->recent_pumps = r.pumps;
    out->recent_over_budget = r.overBudget;
    out->recent_long_frames = r.longFrames;
    out->recent_idle = r.idle;
    out->recent_multi_swap = r.multiSwap;
}

void perfEval(const vlqt_perf_sample* samples, const double* tMs, int n, double budgetMs, vlqt_perf_stats* out)
{
    PerfCounters counters;
    std::array<PerfSample, PerfMonitor::kHistory> ring;
    const int first = std::max(0, n - PerfMonitor::kHistory);
    for (int i = 0; i < n; i++) {
        const vlqt_perf_sample& v = samples[i];
        PerfSample s;
        s.frameInterval = v.frame_interval;
        s.pump = v.pump;
        s.sync = v.sync;
        s.render = v.render;
        s.present = v.present;
        s.vsync = v.vsync;
        s.gpu = v.gpu;
        s.events = v.events;
        s.swaps = v.swaps;
        counters.add(s, tMs[i], budgetMs);
        if (i >= first)
            ring[i - first] = s;
    }
    perfStats(ring.data(), n - first, counters, n > 0 ? tMs[n - 1] : 0, budgetMs, out);
}

// #245 ---------------------------------------------------------------------------------------------------------------

void perfHostStats(const vlqt_host_pump* frames, int n, vlqt_host_pump_stats* out)
{
    *out = vlqt_host_pump_stats{};
    if (n <= 0)
        return;
    double interval = 0, gc0 = 0, gc1 = 0, gc2 = 0, pause = 0;
    for (int i = 0; i < n; i++) {
        const vlqt_host_pump& f = frames[i];
        out->apply_ms += f.apply;
        out->apply_ms_max = std::max(out->apply_ms_max, double(f.apply));
        out->reconcile_ms += f.reconcile;
        out->reconcile_ms_max = std::max(out->reconcile_ms_max, double(f.reconcile));
        out->host_ms += f.host;
        out->host_ms_max = std::max(out->host_ms_max, double(f.host));
        out->ops += f.ops;
        out->ops_max = std::max(out->ops_max, double(f.ops));
        out->alloc_bytes += double(f.alloc_bytes);
        out->alloc_bytes_max = std::max(out->alloc_bytes_max, double(f.alloc_bytes));
        out->vvvv_ms += f.vvvv;
        out->vvvv_ms_max = std::max(out->vvvv_ms_max, double(f.vvvv));
        out->wait_ms += f.wait;
        out->wait_ms_max = std::max(out->wait_ms_max, double(f.wait));
        interval += f.interval;
        gc0 += f.gc0;
        gc1 += f.gc1;
        gc2 += f.gc2;
        pause += f.gc_pause;
    }
    out->samples = n;
    out->apply_ms /= n;
    out->reconcile_ms /= n;
    out->host_ms /= n;
    out->ops /= n;
    out->alloc_bytes /= n;
    out->vvvv_ms /= n;
    out->wait_ms /= n;
    if (interval > 0) {
        const double perS = 1000.0 / interval;
        out->gc0_per_s = gc0 * perS;
        out->gc1_per_s = gc1 * perS;
        out->gc2_per_s = gc2 * perS;
        out->gc_pause_ms_per_s = pause * perS;
    }
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
    comp.setData(QByteArray(kOverlayQml), QUrl(QStringLiteral("vlqt-perf-overlay.qml")));
    QObject* obj = comp.isError() ? nullptr : comp.create();
    auto* item = qobject_cast<QQuickItem*>(obj);
    if (!item) {
        for (const auto& e : comp.errors())
            qWarning("perf overlay: %s", qPrintable(e.toString()));
        delete obj;
        return;
    }
    QQmlEngine::setObjectOwnership(item, QQmlEngine::CppOwnership);
    // Before the item joins the scene: no layers on the Qt Quick software backend (see kOverlayQml).
    const QSGRendererInterface* ri = m_window->rendererInterface();
    if (QQuickWindow::sceneGraphBackend() == QLatin1String("software")
        || (ri && ri->graphicsApi() == QSGRendererInterface::Software))
        item->setProperty("layered", false);
    item->setObjectName(QLatin1String(vlqt::perfOverlayName)); // not counted as a scene change (app/render_gate.cpp)
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
        // Colors double as the graph legend (kGraphSeries).
        m_overlay->setProperty("meterText", QStringLiteral(
            "%1 fps  frame %2  <font color='#ff6eb4'>vvvv %3</font>  <font color='#50dc78'>gpu %4</font>  "
            "<font color='#ffa028'>cpu %5</font>")
            .arg(f(p.fps, 1), f(p.frame_ms, 1), opt(hs.vvvv_ms), opt(p.gpu_ms), f(p.pump_ms, 1)));
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

namespace {

// First line of every page: name, position, keys, frozen marker.
void pageHeader(Html& h, int page, bool frozen)
{
    h.c(kYellow, QStringLiteral("%1 %2/%3").arg(QString::fromLatin1(kPageName[page])).arg(page + 1).arg(int(PerfMonitor::PageCount)));
    h.c(kGrey, QStringLiteral("   F5 next  Shift+F5 hide  F6 freeze"));
    if (frozen)
        h.t("  ").c(kRed, "FROZEN");
}

} // namespace

QString PerfMonitor::summaryPage(QString* body) const
{
    const double budget = budgetMs();
    const int n = viewCount();
    std::array<PerfSample, kHistory> ring;
    for (int i = 0; i < n; i++)
        ring[i] = viewSample(i);
    vlqt_perf_stats st;
    perfStats(ring.data(), n, m_counters, nowMs(), budget, &st);

    // avg over the last ~1 s, max over the whole view (same window as the percentiles).
    const int first = std::max(0, n - kSummaryPumps);
    double frameSum = 0, pumpSum = 0, gpuSum = 0, gpuMax = 0, swaps = 0;
    double phSum[PhaseCount] = {}, phMax[PhaseCount] = {};
    double rowSum = 0; // frame row: consecutive frames only, like the percentiles
    int frames = 0, rowFrames = 0, gpus = 0, idle = 0;
    for (int i = 0; i < n; i++) {
        const PerfSample& s = ring[i];
        for (int ph = 0; ph < PhaseCount; ph++)
            phMax[ph] = std::max(phMax[ph], double(phaseMs(s, ph)));
        gpuMax = std::max(gpuMax, double(s.gpu));
        if (i < first)
            continue;
        pumpSum += s.pump;
        swaps += s.swaps;
        for (int ph = 0; ph < PhaseCount; ph++)
            phSum[ph] += phaseMs(s, ph);
        if (s.swaps > 0 && s.frameInterval > 0) { frameSum += s.frameInterval; frames++; }
        if (consecutiveFrame(ring.data(), i)) { rowSum += s.frameInterval; rowFrames++; }
        if (s.gpu > 0) { gpuSum += s.gpu; gpus++; }
        idle += s.swaps == 0;
    }
    const int na = n - first;
    const double frameAvg = frames ? frameSum / frames : 0;
    const double rowAvg = rowFrames ? rowSum / rowFrames : 0;
    const double pumpAvg = na ? pumpSum / na : 0;
    // Qt renders only on changes; a low fps from idle pumps is not a slowdown -> neutral, no traffic light.
    const bool onDemand = na > 0 && idle > kOnDemandIdleShare * na;

    Html h;
    pageHeader(h, Summary, m_frozen);
    h.br().t("fps ").c(frameAvg > 0 && !onDemand ? frameColor(frameAvg, budget) : kGrey, frameAvg > 0 ? num(1000.0 / frameAvg, 5, 1) : QStringLiteral("    -"));
    if (onDemand)
        h.c(kGrey, QStringLiteral(" (on demand, %1 % idle)").arg(qRound(100.0 * idle / na)));
    h.t(QStringLiteral("   budget %1 ms (%2 Hz)").arg(num(budget, 0), num(1000.0 / budget, 0, 0)));
    vlqt_host_pump_stats hs; // C# side (live, also while frozen); only when the host reports it
    hostStats(&hs);
    const bool vvvv = hs.samples > 0 && hs.vvvv_ms_max > 0; // 0 = unknown (older host)
    if (frameAvg > 0 && !onDemand) {
        h.t(QStringLiteral("   Qt pump %1% of frame").arg(qRound(100.0 * pumpAvg / frameAvg)));
        if (vvvv)
            h.t(", ").c(kHostColor[SegVvvv].html, QStringLiteral("vvvv %1%").arg(qRound(100.0 * hs.vvvv_ms / frameAvg)));
    }
    h.br().c(kGrey, QStringLiteral("pump avg vs. budget, tick = p95"));
    if (m_overlay) { // null for pageText without overlay
        m_overlay->setProperty("barUse", budget > 0 ? pumpAvg / budget : 0.0);
        m_overlay->setProperty("barP95", budget > 0 ? st.pump_p95 / budget : 0.0);
        m_overlay->setProperty("barColor", QColor(QLatin1String(pumpColor(pumpAvg, budget))));
    }

    constexpr int L = 12, W = 8;
    Html b;
    b.c(kGrey, lab("ms", L) + QStringLiteral("avg").rightJustified(W) + QStringLiteral("p50").rightJustified(W)
        + QStringLiteral("p95").rightJustified(W) + QStringLiteral("p99").rightJustified(W) + QStringLiteral("max").rightJustified(W)).br();
    auto pRow = [&](const char* name, double avg, double p50, double p95, double p99, double max, auto color) {
        b.t(lab(name, L)).c(color(avg), num(avg, W)).c(color(p50), num(p50, W)).c(color(p95), num(p95, W))
            .c(color(p99), num(p99, W)).c(color(max), num(max, W)).br();
    };
    pRow("frame", rowAvg, st.frame_p50, st.frame_p95, st.frame_p99, st.frame_max,
        [&](double v) { return onDemand ? kGrey : frameColor(v, budget); });
    pRow("pump", pumpAvg, st.pump_p50, st.pump_p95, st.pump_p99, st.pump_max,
        [&](double v) { return pumpColor(v, budget); });
    for (int ph : kPhaseOrder) {
        b.t("  ").c(kPhaseColor[ph].html, lab(kPhaseName[ph], L - 2));
        b.c(pumpColor(na ? phSum[ph] / na : 0, budget), num(na ? phSum[ph] / na : 0, W));
        b.t(QString(3 * W, u' ')).c(pumpColor(phMax[ph], budget), num(phMax[ph], W)).br();
    }
    b.c(kGpuColor.html, lab("GPU", L));
    if (gpus)
        b.t(num(gpuSum / gpus, W)).t(QString(3 * W, u' ')).t(num(gpuMax, W)).br();
    else
        b.c(kGrey, QStringLiteral("n/a").rightJustified(W)).br();
    b.t(lab("swaps/pump", L)).t(num(na ? swaps / na : 0, W)).br();
    if (hs.samples > 0) {
        if (vvvv) // the vvvv update before the pump, same columns as the phases (avg, max)
            b.c(kHostColor[SegVvvv].html, lab("vvvv", L)).c(pumpColor(hs.vvvv_ms, budget), num(hs.vvvv_ms, W))
                .t(QString(3 * W, u' ')).c(pumpColor(hs.vvvv_ms_max, budget), num(hs.vvvv_ms_max, W)).br();
        b.c(kHostColor[SegApply].html, lab("apply", L)).c(pumpColor(hs.apply_ms, budget), num(hs.apply_ms, W)).t(QString(3 * W, u' '))
            .c(pumpColor(hs.apply_ms_max, budget), num(hs.apply_ms_max, W)).c(kGrey, QStringLiteral("  ops %1").arg(num(hs.ops, 0, 0))).br();
        b.t(lab("reconcile", L)).t(num(hs.reconcile_ms, W)).t(QString(3 * W, u' ')).t(num(hs.reconcile_ms_max, W)).br();
        b.c(kHostColor[SegGc].html, lab("GC /s", L)).c(hs.gc2_per_s > 0 ? kRed : hs.gc1_per_s > 0 ? kYellow : kGreen,
            QStringLiteral("gen0 %1  gen1 %2  gen2 %3  pause %4 ms").arg(num(hs.gc0_per_s, 0, 1), num(hs.gc1_per_s, 0, 1),
                num(hs.gc2_per_s, 0, 1), num(hs.gc_pause_ms_per_s, 0))).br();
    }
    b.c(kGrey, QStringLiteral("avg: last %1 pumps, p/max: last %2").arg(kSummaryPumps).arg(n)).br().br();

    b.c(kGrey, lab("counters", L + 2) + QStringLiteral("total").rightJustified(W) + QStringLiteral("10 s").rightJustified(W)).br();
    auto cRow = [&](const char* name, long long total, long long recent, bool bad) {
        b.t(lab(name, L + 2)).c(bad && total ? kYellow : kGreen, QString::number(total).rightJustified(W))
            .c(bad && recent ? kRed : kGreen, QString::number(recent).rightJustified(W)).br();
    };
    cRow("over budget", st.over_budget, st.recent_over_budget, true);
    cRow("long frames", st.long_frames, st.recent_long_frames, true);
    cRow("multi-swap", st.multi_swap, st.recent_multi_swap, true);
    cRow("idle", st.idle, st.recent_idle, false);
    b.t(lab("pumps", L + 2)).t(QString::number(st.pumps).rightJustified(W)).t(QString::number(st.recent_pumps).rightJustified(W));
    *body = b.str();
    return h.str();
}

QString PerfMonitor::timelinePage(QString* foot) const
{
    const double budget = budgetMs();
    const int n = viewCount();
    Html h;
    pageHeader(h, Timeline, m_frozen);
    h.br().c(kGrey, QStringLiteral("%1 pumps, newest right, scale 0-%2 ms   F7 vvvv/GC/apply %3").arg(n)
        .arg(num(PerfTimeline::scaleFor(this), 0, 1), QLatin1String(s_hostSegments ? "on" : "off")));

    Html f;
    for (int seg = firstSeg(); seg < SegCount; seg++)
        f.c(segColor(seg).html, QStringLiteral("■ ")).t(segName(seg)).t("  ");
    f.br().c(kGpuColor.html, "- GPU  ").c(kBudgetColor.html, QStringLiteral("- budget %1 (pump > budget: red)").arg(num(budget, 0)));
    f.t("  ").c(kGrey, "- max").br();
    // Peaks: the 3 largest bars (shown segments) with their dominant segment and age.
    int peaks[3] = {-1, -1, -1};
    for (int i = 0; i < n; i++) {
        const float v = stackMs(viewSample(i));
        for (int k = 0; k < 3; k++) {
            if (peaks[k] < 0 || v > stackMs(viewSample(peaks[k]))) {
                for (int j = 2; j > k; j--)
                    peaks[j] = peaks[j - 1];
                peaks[k] = i;
                break;
            }
        }
    }
    for (int k = 0; k < 3 && peaks[k] >= 0; k++) {
        const PerfSample& s = viewSample(peaks[k]);
        int dom = firstSeg();
        for (int seg = dom + 1; seg < SegCount; seg++)
            if (segMs(s, seg) > segMs(s, dom))
                dom = seg;
        const float total = stackMs(s);
        f.t(k == 0 ? "peaks " : "      ").c(pumpColor(total, budget), num(total, 6) + QStringLiteral(" ms"));
        f.t("  ").c(segColor(dom).html, lab(segName(dom), 10)).t(num(segMs(s, dom), 6));
        f.c(kGrey, QStringLiteral("  %1 pumps ago").arg(n - 1 - peaks[k]));
        if (k < 2 && peaks[k + 1] >= 0)
            f.br();
    }
    *foot = f.str();
    return h.str();
}

namespace {

const char* typeName(quint16 type)
{
    switch (type) {
    case VLQT_ELEMENT_TYPE_GROUP: return "group";
    case VLQT_ELEMENT_TYPE_RECTANGLE: return "rect";
    case VLQT_ELEMENT_TYPE_TEXT: return "text";
    case VLQT_ELEMENT_TYPE_IMAGE: return "image";
    case VLQT_ELEMENT_TYPE_ROW: return "row";
    case VLQT_ELEMENT_TYPE_COLUMN: return "column";
    case VLQT_ELEMENT_TYPE_SHADER: return "shader";
    case VLQT_ELEMENT_TYPE_VIDEO: return "video";
    default: return "other";
    }
}

QString mb(quint64 bytes) { return QString::number(double(bytes) / (1024.0 * 1024.0), 'f', 1) + QStringLiteral(" MB"); }

const char* deviceTypeName(QRhiDriverInfo::DeviceType t)
{
    switch (t) {
    case QRhiDriverInfo::IntegratedDevice: return "integrated";
    case QRhiDriverInfo::DiscreteDevice: return "discrete";
    case QRhiDriverInfo::ExternalDevice: return "external";
    case QRhiDriverInfo::VirtualDevice: return "virtual";
    case QRhiDriverInfo::CpuDevice: return "CPU";
    default: return nullptr;
    }
}

const QString kNa = QStringLiteral("n/a");

} // namespace

// Built only while the page is visible (4 Hz text interval) or on demand (pageText); nothing here runs per frame.
QString PerfMonitor::scenePage() const
{
    Html h;
    pageHeader(h, Scene, m_frozen);
    constexpr int L = 12;
    // Elements = reconciler nodes of this window (CREATE minus DESTROY), the root not counted.
    h.br().t(lab("elements", L));
    if (m_scene) {
        h.t(QString::number(qulonglong(m_scene->nodes.size())).rightJustified(8));
        int byType[16] = {};
        for (const auto& kv : m_scene->nodes)
            byType[kv.second.type < 16 ? kv.second.type : 0]++;
        QString parts;
        for (int t = 0; t < 16; t++)
            if (byType[t])
                parts += QStringLiteral("  %1 %2").arg(QString::fromLatin1(typeName(quint16(t)))).arg(byType[t]);
        h.c(kGrey, parts);
    } else {
        h.t(kNa.rightJustified(8));
    }
    const int items = countItems(m_window->contentItem(), m_overlay, m_scene ? m_scene->debugOverlay() : nullptr);
    h.br().t(lab("QML items", L)).t(QString::number(items).rightJustified(8)).c(kGrey, "  QQuickItems below the root, incl. element internals");
    // Effect layers (elements with an effect chain) and all MultiEffect items (one per MULTI run).
    int layers = 0;
    if (m_scene)
        for (const auto& kv : m_scene->nodes)
            layers += vlqt::Elements::effectLayerItem(kv.second) ? 1 : 0;
    const int effects = countMultiEffects(m_window->contentItem(), m_overlay, m_scene ? m_scene->debugOverlay() : nullptr);
    h.br().t(lab("effects", L)).t(QString::number(layers).rightJustified(8))
        .c(kGrey, QStringLiteral("  effect layers, %1 MultiEffect items").arg(effects));
    // QUnifiedTimer of this (GUI) thread: QAbstractAnimations + QML animations/animators; process-wide, not per window.
    const QUnifiedTimer* timer = QUnifiedTimer::instance(false);
    h.br().t(lab("animations", L)).t(QString::number(timer ? qlonglong(timer->runningAnimationCount()) : 0).rightJustified(8))
        .c(kGrey, "  running (process)");
    // Ops per apply: commands the host applied to this window per pump (host pumps, ~1 s).
    vlqt_host_pump_stats hs;
    hostStats(&hs);
    vlqt_host_pump last;
    h.br().t(lab("ops/apply", L));
    if (hs.samples > 0 && lastHostPump(&last))
        h.t(num(hs.ops, 8, 1)).c(kGrey, QStringLiteral("  avg   max %1  last %2").arg(num(hs.ops_max, 0, 0)).arg(last.ops));
    else
        h.t(kNa.rightJustified(8)).c(kGrey, "  no host pumps reported");

    // QRhi statistics (feasibility): GPU memory only from the D3D12/Vulkan
    // allocators; draw calls/batches are not exposed by Qt Quick.
    QRhi* rhi = m_window->rhi();
    const QRhiStats st = rhi ? rhi->statistics() : QRhiStats{};
    h.br().t(lab("gpu memory", L));
    if (st.totalUsageBytes > 0)
        h.t(mb(st.totalUsageBytes).rightJustified(8)).c(kGrey, "  process, adapter-reported");
    else if (st.allocCount > 0)
        h.t(mb(st.usedBytes).rightJustified(8)).c(kGrey, QStringLiteral("  %1 allocations, %2 unused").arg(st.allocCount).arg(mb(st.unusedBytes)));
    else
        h.t(kNa.rightJustified(8)).c(kGrey, rhi ? "  not reported by this backend" : "  no QRhi (software)");
    h.br().t(lab("pipelines", L));
    if (rhi)
        h.t(num(double(st.totalPipelineCreationTime), 8, 0)).c(kGrey, "  ms total creation time");
    else
        h.t(kNa.rightJustified(8));
    h.br().t(lab("draw calls", L)).t(kNa.rightJustified(8)).c(kGrey, "  not exposed by Qt Quick");
    return h.str();
}

QString PerfMonitor::systemPage() const
{
    Html h;
    pageHeader(h, System, m_frozen);
    constexpr int L = 14; // "allow tearing" + 1
    QRhi* rhi = m_window->rhi();
    const QSGRendererInterface* ri = m_window->rendererInterface();
    const bool quickSoftware = ri && ri->graphicsApi() == QSGRendererInterface::Software;
    h.br().t(lab("backend", L));
    if (rhi)
        h.t(QString::fromLatin1(rhi->backendName())).c(kGrey, "  QRhi");
    else if (quickSoftware)
        h.t("software").c(kGrey, "  Qt Quick 2D renderer, no QRhi");
    else
        h.t(kNa);
    const QRhiDriverInfo info = rhi ? rhi->driverInfo() : QRhiDriverInfo{};
    const QString device = QString::fromUtf8(info.deviceName);
    h.br().t(lab("adapter", L)).t(device.isEmpty() ? kNa : device);
    if (const char* type = rhi ? deviceTypeName(info.deviceType) : nullptr)
        h.c(kGrey, QStringLiteral("  %1").arg(QString::fromLatin1(type)));
    h.br().t(lab("rendering", L));
    if (!m_rendererKnown)
        h.c(kGrey, kNa);
    else if (m_software.isEmpty())
        h.c(kGreen, "GPU");
    else
        h.c(kRed, QStringLiteral("CPU (%1)").arg(m_software));
    const qreal dpr = m_window->devicePixelRatio();
    const QSize px = m_window->size() * dpr;
    h.br().t(lab("window", L)).t(QStringLiteral("%1x%2 px").arg(px.width()).arg(px.height()))
        .c(kGrey, QStringLiteral("  %1x%2 logical").arg(m_window->width()).arg(m_window->height()));
    h.br().t(lab("DPR", L)).t(QString::number(dpr, 'g', 3));
    const QScreen* screen = m_window->screen();
    const double hz = screen ? screen->refreshRate() : 0;
    h.br().t(lab("refresh", L));
    if (hz >= 1 && hz <= 1000)
        h.t(QStringLiteral("%1 Hz").arg(num(hz, 0, 1)));
    else
        h.t(kNa);
    h.c(kGrey, QStringLiteral("  budget %1 ms").arg(num(budgetMs(), 0)) + (screen ? QStringLiteral("  ") + screen->name() : QString()));
    // Actual swap chain flag where there is one; the requested QSurfaceFormat value otherwise (software: n/a).
    h.br().t(lab("swap int.", L));
    if (QRhiSwapChain* sc = m_window->swapChain()) {
        const bool vsync = !(sc->flags() & QRhiSwapChain::NoVSync);
        h.t(QString::number(vsync ? 1 : 0)).c(kGrey, vsync ? "  VSync on, Present waits" : "  VSync off, Present does not wait");
    } else if (rhi) {
        h.t(QString::number(m_window->format().swapInterval())).c(kGrey, "  requested");
    } else {
        h.t(kNa);
        if (quickSoftware)
            h.c(kGrey, "  software backend presents via the backing store");
    }
    // Does Qt call Present with DXGI_PRESENT_ALLOW_TEARING? D3D11 only. QRhiD3D11::beginFrame/endFrame
    // (qrhid3d11.cpp:1561-1562, v6.12.0) sets it when swapInterval == 0 and the swap chain has
    // DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING; that flag is set when NoVSync and supportsAllowTearing
    // (qrhid3d11.cpp:5422-5430) = dxgiAllowTearingSupported() here.
    h.br().t(lab("allow tearing", L));
    QRhiSwapChain* tearSc = m_window->swapChain();
#ifdef Q_OS_WIN
    if (rhi && rhi->backend() == QRhi::D3D11 && tearSc) {
        const bool noVsync = tearSc->flags() & QRhiSwapChain::NoVSync;
        const bool supported = dxgiAllowTearingSupported();
        const bool tearing = noVsync && supported;
        h.c(tearing ? kYellow : kGreen, tearing ? "yes" : "no");
        h.c(kGrey, tearing ? "  Present uses ALLOW_TEARING, frames may tear"
            : !noVsync ? "  VSync on" : "  not supported by the system, Present(0) in the flip model may still wait");
    } else {
        h.c(kGrey, kNa);
    }
#else
    Q_UNUSED(tearSc);
    h.c(kGrey, kNa);
#endif
    h.br().t(lab("Qt", L)).t(QString::fromLatin1(qVersion()));
    return h.str();
}

namespace {

constexpr int kResRows = 16;          // list rows on the Resources page
constexpr int64_t kResStuckMs = 2000; // Decoding/Uploading longer than this: listed first, red

QString bytesText(int64_t b)
{
    if (b < 1024)
        return QStringLiteral("%1 B").arg(b);
    if (b < 1024 * 1024)
        return QString::number(double(b) / 1024.0, 'f', 1) + QStringLiteral(" KB");
    if (b < (int64_t(1) << 30))
        return QString::number(double(b) / (1024.0 * 1024.0), 'f', 1) + QStringLiteral(" MB");
    return QString::number(double(b) / double(int64_t(1) << 30), 'f', 1) + QStringLiteral(" GB");
}

QString ageText(int64_t ms)
{
    if (ms < 10000)
        return QString::number(double(ms) / 1000.0, 'f', 1) + QStringLiteral(" s");
    if (ms < 600000)
        return QStringLiteral("%1 s").arg(ms / 1000);
    return QStringLiteral("%1 min").arg(ms / 60000);
}

bool resStuck(const vlqt::res::Row& r)
{
    using vlqt::res::State;
    return (r.state == State::Decoding || r.state == State::Uploading) && r.ageMs > kResStuckMs;
}

// List order: Failed and stuck first, then the ones in progress, then Ready; the oldest first within each group.
int resRank(const vlqt::res::Row& r)
{
    using vlqt::res::State;
    if (r.state == State::Failed || resStuck(r))
        return 0;
    return r.state == State::Ready ? 2 : 1;
}

const char* resColor(const vlqt::res::Row& r)
{
    using vlqt::res::State;
    switch (r.state) {
    case State::Ready: return kGreen;
    case State::Failed: return kRed;
    case State::Decoding:
    case State::Uploading: return resStuck(r) ? kRed : kYellow;
    default: return kGrey;
    }
}

} // namespace

// live resources of the process-wide manager (resources.h), the same on every window. Built only while the
// page is visible (4 Hz text interval) or on demand (pageText); the row buffer is reused.
QString PerfMonitor::resourcesPage() const
{
    using vlqt::res::State;
    QElapsedTimer timer; // own build time in the footer (< 0.5 ms with 1000 resources)
    timer.start();
    vlqt::res::Overview ov;
    vlqt::res::snapshot(m_resRows, ov);
    int byState[int(State::Released) + 1] = {};
    int64_t uploaded = 0;
    int stuck = 0;
    for (const auto& r : m_resRows) {
        byState[int(r.state)]++;
        uploaded += r.bytes;
        stuck += resStuck(r);
    }

    Html h;
    pageHeader(h, Resources, m_frozen);
    constexpr int L = 13;
    h.br().t(lab("live", L)).t(QString::number(qulonglong(m_resRows.size())).rightJustified(6));
    const struct { State s; const char* name; const char* color; } states[] = {
        {State::Pending, "pending", kGrey}, {State::Decoding, "decoding", kYellow}, {State::Uploading, "uploading", kYellow},
        {State::Ready, "ready", kGreen}, {State::Failed, "failed", kRed}, {State::Released, "released", kGrey}};
    for (const auto& st : states)
        h.t("  ").c(byState[int(st.s)] ? st.color : kGrey, QStringLiteral("%1 %2").arg(QString::fromLatin1(st.name)).arg(byState[int(st.s)]));
    if (stuck)
        h.t("  ").c(kRed, QStringLiteral("stuck %1").arg(stuck));
    h.br().t(lab("uploaded", L)).t(bytesText(uploaded).rightJustified(10)).c(kGrey, "  VRAM of all live resources");
    h.br().t(lab("upload queue", L)).t(bytesText(ov.uploadBytes).rightJustified(10))
        .c(kGrey, QStringLiteral("  %1 job(s), decode queue %2").arg(ov.uploadJobs).arg(ov.decodeJobs));
    const double use = ov.budget > 0 ? double(ov.usedLastFrame) / double(ov.budget) : 0;
    h.br().t(lab("budget", L)).t(bytesText(ov.budget).rightJustified(10)).t(" / frame   last frame ")
        .c(use >= 1 ? kYellow : kGreen, QStringLiteral("%1 (%2%)").arg(bytesText(ov.usedLastFrame)).arg(qRound(100 * use)));
    // cost of the snapshots (grab start to delivery); grey with nothing in the last 10 s
    vlqt::res::snap::Stats snap;
    vlqt::res::snap::stats(snap);
    const QString none(QChar(0x2013));
    h.br().c(snap.inWindow ? kGreen : kGrey, QStringLiteral("%1%2/s   last %3 ms   max %4 ms")
        .arg(lab("snapshots", L)).arg(snap.perSecond, 3)
        .arg(snap.inWindow ? QString::number(snap.lastMs, 'f', 1) : none)
        .arg(snap.inWindow ? QString::number(snap.maxMs, 'f', 1) : none));

    if (m_resRows.empty())
        return h.str();
    const int n = std::min(int(m_resRows.size()), kResRows);
    std::partial_sort(m_resRows.begin(), m_resRows.begin() + n, m_resRows.end(), [](const auto& a, const auto& b) {
        const int ra = resRank(a), rb = resRank(b);
        return ra != rb ? ra < rb : a.ageMs != b.ageMs ? a.ageMs > b.ageMs : a.id < b.id;
    });
    h.br().br().c(kGrey, lab("id", 10) + lab("kind", 7) + lab("state", 10) + QStringLiteral("size").rightJustified(10)
        + QStringLiteral("refs").rightJustified(6) + QStringLiteral("age").rightJustified(9));
    for (int i = 0; i < n; i++) {
        const auto& r = m_resRows[i];
        const QString id = QStringLiteral("#%1.%2").arg(uint32_t(r.id)).arg(uint32_t(r.id >> 32));
        h.br().t(id.leftJustified(10)).t(lab(r.kind, 7)).c(resColor(r), lab(vlqt::res::stateName(r.state), 10))
            .t(bytesText(r.bytes).rightJustified(10)).t(QStringLiteral("%1+%2").arg(r.hostRefs).arg(r.elementRefs).rightJustified(6))
            .c(resStuck(r) ? kRed : kGrey, ageText(r.ageMs).rightJustified(9));
    }
    if (int(m_resRows.size()) > n)
        h.br().c(kGrey, QStringLiteral("... %1 more").arg(int(m_resRows.size()) - n));
    h.br().c(kGrey, QStringLiteral("refs = host+elements, age = time in the state, stuck = decoding/uploading > %1 s").arg(kResStuckMs / 1000));
    h.br().c(kGrey, QStringLiteral("page built in %1 ms").arg(QString::number(timer.nsecsElapsed() / 1e6, 'f', 3)));
    return h.str();
}

QString PerfMonitor::pageText(int page) const
{
    QString html, part;
    switch (page) {
    case Summary: html = summaryPage(&part); break; // also Summary/Timeline (head + body/foot)
    case Timeline: html = timelinePage(&part); break;
    case Scene: html = scenePage(); break;
    case System: html = systemPage(); break;
    case Resources: html = resourcesPage(); break;
    default: break;
    }
    if (!part.isEmpty())
        html += QLatin1String("<br>") + part;
    return QTextDocumentFragment::fromHtml(html).toPlainText().replace(QChar(0xa0), u' ');
}

// ---------------------------------------------------------------------------

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
    {qRgb(255, 160, 40), "#ffa028", [](const PerfSample& s) { return s.pump; }},
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
        yMax = std::max({yMax, s.frameInterval, s.pump, s.gpu, s.hostReported ? s.host.vvvv : 0.f});
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
    for (int i = 0; i < n; i++) { // over-budget pumps: full-height red background, one quad per run
        const PerfSample& s = m_monitor->viewSample(i);
        if (s.pump > budget) {
            if (i > 0 && m_monitor->viewSample(i - 1).pump > budget)
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
// Measured and dropped (Snapdragon/D3D11): a ring of chunk nodes moved by transforms (uploads only new
// samples, but each extra node costs per frame, also without updates) and opaque materials for the bars (cheaper
// render phase, but a higher pump median).
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
