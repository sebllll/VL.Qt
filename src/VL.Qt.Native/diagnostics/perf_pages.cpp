#include "diagnostics/perf_pages.h"
#include "device/renderer_info.h"
#include "diagnostics/perf_items.h"
#include "scene/scene.h"
#include "snapshot_resource.h"

#include <QtGui/QScreen>
#include <QtGui/QTextDocumentFragment>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
#include <private/qquickshadereffectsource_p.h>
#include <rhi/qrhi.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <map>

namespace vlqt {
namespace {

constexpr double kOnDemandIdleShare = 0.25; // Summary shows "on demand" above this share of idle pumps

// Colors: phases (timeline bars + summary rows) and thresholds against the budget.
constexpr Rgb kPhaseColor[PerfMonitor::PhaseCount] = {
    {90, 156, 255},  // events
    {176, 196, 222}, // vsync wait
    {192, 128, 255}, // sync
    kWorkColor,      // render
    {64, 208, 208},  // present
};
constexpr Rgb kRedColor{255, 80, 80};
constexpr Rgb kGreyColor{144, 144, 144};
constexpr const char* kGreen = kGpuColor.html;
constexpr const char* kYellow = kBudgetColor.html;
constexpr const char* kRed = kRedColor.html;
constexpr const char* kGrey = kGreyColor.html;
const char* const kPhaseName[PerfMonitor::PhaseCount] = {"events", "vsync wait", "sync", "render", "present"};
const char* const kPageName[PerfMonitor::PageCount] = {"SUMMARY", "TIMELINE", "SCENE", "SYSTEM", "RESOURCES"};

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

// Host segments of the timeline (vvvv update, .NET GC pause, apply).
constexpr Rgb kHostColor[SegQt] = {
    kVvvvColor,      // vvvv (same as the F2 line)
    {176, 112, 64},  // GC
    {160, 160, 184}, // apply
};
const char* const kHostName[SegQt] = {"vvvv", "GC", "apply"};

} // namespace

bool s_hostSegments = true;

const Rgb& segColor(int seg) { return seg < SegQt ? kHostColor[seg] : kPhaseColor[seg - SegQt]; }
float segMs(const PerfSample& s, int seg)
{
    switch (seg) {
    case SegVvvv: return s.hostReported ? std::max(0.f, s.host.vvvv - s.host.gc_pause) : 0;
    case SegGc: return s.hostReported ? s.host.gc_pause : 0;
    case SegApply: return s.hostReported ? s.host.apply : 0;
    default: return phaseMs(s, seg - SegQt);
    }
}
int firstSeg() { return s_hostSegments ? 0 : SegQt; }
float stackMs(const PerfSample& s)
{
    float sum = 0;
    for (int seg = firstSeg(); seg < SegQt; seg++)
        sum += segMs(s, seg);
    return sum + std::max(s.pump, s.events + s.vsync + s.sync + s.render + s.present);
}

namespace {

const char* segName(int seg) { return seg < SegQt ? kHostName[seg] : kPhaseName[seg - SegQt]; }

// A duration in the vvvv frame (Qt work, a phase, vvvv, apply): green < half the budget, yellow < budget, red above.
const char* budgetColor(double ms, double budget) { return ms < 0.5 * budget ? kGreen : ms < budget ? kYellow : kRed; }
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

// MultiEffect items below item (effect layers), overlays skipped.
int countMultiEffects(const QQuickItem* item, const QQuickItem* skip, const QQuickItem* skip2)
{
    int n = 0;
    for (const QQuickItem* c : item->childItems())
        if (c != skip && c != skip2)
            n += (c->inherits("QQuickMultiEffect") ? 1 : 0) + countMultiEffects(c, skip, skip2);
    return n;
}

// Largest layer texture below item (DIP; every layer and ShaderEffectSource, MultiEffect internals included), overlays
// skipped: the size Qt asks for, before its clamp to the maximum texture size.
void largestLayer(const QQuickItem* item, const QQuickItem* skip, const QQuickItem* skip2, QSizeF& largest)
{
    for (const QQuickItem* c : item->childItems()) {
        if (c == skip || c == skip2)
            continue;
        if (const auto* es = qobject_cast<const QQuickShaderEffectSource*>(c); es && es->sourceItem()) {
            const QRectF r = es->sourceRect();
            const QSizeF s = r.isNull() ? es->sourceItem()->size() : QSizeF(std::abs(r.width()), std::abs(r.height()));
            if (s.width() * s.height() > largest.width() * largest.height())
                largest = s;
        }
        largestLayer(c, skip, skip2, largest);
    }
}

} // namespace

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

    // avg over the last ~1 s (PerfWindow, the one computation of vlqt_perf and the F2 meter), max over the whole view (same
    // range as the percentiles).
    const PerfWindow win = perfWindow(ring.data(), n, kSummaryPumps);
    double workMax = 0, gpuMax = 0;
    std::array<float, kHistory> work; // Qt work over the view, for the p95 tick of the bar
    double phMax[PhaseCount] = {};
    for (int i = 0; i < n; i++) {
        const PerfSample& s = ring[i];
        for (int ph = 0; ph < PhaseCount; ph++)
            phMax[ph] = std::max(phMax[ph], double(phaseMs(s, ph)));
        gpuMax = std::max(gpuMax, double(s.gpu));
        work[i] = s.workMs();
        workMax = std::max(workMax, double(work[i]));
    }
    const int na = win.pumps;
    const double frameAvg = win.frameAvg;
    const double pumpAvg = win.pumpAvg;
    const double workAvg = win.workAvg;
    auto phAvg = [&](int ph) { // per pump with a frame
        switch (ph) {
        case PerfMonitor::PhaseEvents: return win.eventsAvg;
        case PerfMonitor::PhaseSync: return win.syncAvg;
        case PerfMonitor::PhaseRender: return win.renderAvg;
        case PerfMonitor::PhaseVsync: return win.vsyncAvg;
        default: return win.presentAvg;
        }
    };
    double workP95 = 0; // nearest rank, as the percentiles of vlqt_perf_stats
    if (n > 0) {
        const int k = std::clamp(int(std::ceil(0.95 * n)) - 1, 0, n - 1);
        std::nth_element(work.begin(), work.begin() + k, work.begin() + n);
        workP95 = work[k];
    }
    // Qt renders only on changes; a low fps from idle pumps is not a slowdown -> neutral, no traffic light.
    const bool onDemand = na > 0 && win.idle > kOnDemandIdleShare * na;

    Html h;
    pageHeader(h, Summary, m_frozen);
    h.br().t("fps ").c(frameAvg > 0 && !onDemand ? frameColor(frameAvg, budget) : kGrey, win.fps > 0 ? num(win.fps, 5, 1) : QStringLiteral("    -"));
    if (onDemand)
        h.c(kGrey, QStringLiteral(" (on demand, %1 % idle)").arg(qRound(100.0 * win.idle / na)));
    h.t(QStringLiteral("   budget %1 ms (%2 Hz)").arg(num(budget, 0), num(1000.0 / budget, 0, 0)));
    vlqt_host_pump_stats hs; // C# side (live, also while frozen); only when the host reports it
    hostStats(&hs);
    const bool vvvv = hs.samples > 0 && hs.vvvv_ms_max > 0; // 0 = unknown (older host)
    if (frameAvg > 0 && !onDemand) {
        h.t(QStringLiteral("   Qt pump %1% of frame").arg(qRound(100.0 * pumpAvg / frameAvg)));
        if (vvvv)
            h.t(", ").c(kHostColor[SegVvvv].html, QStringLiteral("vvvv %1%").arg(qRound(100.0 * hs.vvvv_ms / frameAvg)));
    }
    h.br().c(kGrey, QStringLiteral("work avg vs. budget, tick = p95"));
    if (m_overlay) { // null for perfPageText without overlay
        m_overlay->setProperty("barUse", budget > 0 ? workAvg / budget : 0.0);
        m_overlay->setProperty("barP95", budget > 0 ? workP95 / budget : 0.0);
        m_overlay->setProperty("barColor", QColor(QLatin1String(budgetColor(workAvg, budget))));
    }

    constexpr int L = 12, W = 8;
    Html b;
    b.c(kGrey, lab("ms", L) + QStringLiteral("avg").rightJustified(W) + QStringLiteral("p50").rightJustified(W)
        + QStringLiteral("p95").rightJustified(W) + QStringLiteral("p99").rightJustified(W) + QStringLiteral("max").rightJustified(W)).br();
    auto pRow = [&](const char* name, double avg, double p50, double p95, double p99, double max, auto color) {
        b.t(lab(name, L)).c(color(avg), num(avg, W)).c(color(p50), num(p50, W)).c(color(p95), num(p95, W))
            .c(color(p99), num(p99, W)).c(color(max), num(max, W)).br();
    };
    pRow("frame", frameAvg, st.frame_p50, st.frame_p95, st.frame_p99, st.frame_max,
        [&](double v) { return onDemand ? kGrey : frameColor(v, budget); });
    // The pump includes the vsync wait and the present: what the frame costs on the host thread, not judged against the
    // budget. Qt work (PerfSample::workMs) is, like the over budget counter.
    b.t(lab("pump", L)).t(num(pumpAvg, W)).t(num(st.pump_p50, W)).t(num(st.pump_p95, W)).t(num(st.pump_p99, W))
        .t(num(st.pump_max, W)).br();
    b.t(lab("work", L)).c(budgetColor(workAvg, budget), num(workAvg, W)).t(QString(3 * W, u' '))
        .c(budgetColor(workMax, budget), num(workMax, W)).br();
    for (int ph = 0; ph < PhaseCount; ph++) {
        b.t("  ").c(kPhaseColor[ph].html, lab(kPhaseName[ph], L - 2));
        b.c(budgetColor(phAvg(ph), budget), num(phAvg(ph), W));
        b.t(QString(3 * W, u' ')).c(budgetColor(phMax[ph], budget), num(phMax[ph], W)).br();
    }
    b.c(kGpuColor.html, lab("GPU", L));
    if (win.gpus)
        b.t(num(win.gpuAvg, W)).t(QString(3 * W, u' ')).t(num(gpuMax, W)).br();
    else
        b.c(kGrey, QStringLiteral("n/a").rightJustified(W)).br();
    b.t(lab("swaps/pump", L)).t(num(win.swapsPerPump, W)).br();
    if (hs.samples > 0) {
        if (vvvv) // the vvvv update before the pump, same columns as the phases (avg, max)
            b.c(kHostColor[SegVvvv].html, lab("vvvv", L)).c(budgetColor(hs.vvvv_ms, budget), num(hs.vvvv_ms, W))
                .t(QString(3 * W, u' ')).c(budgetColor(hs.vvvv_ms_max, budget), num(hs.vvvv_ms_max, W)).br();
        b.c(kHostColor[SegApply].html, lab("apply", L)).c(budgetColor(hs.apply_ms, budget), num(hs.apply_ms, W)).t(QString(3 * W, u' '))
            .c(budgetColor(hs.apply_ms_max, budget), num(hs.apply_ms_max, W)).c(kGrey, QStringLiteral("  ops %1").arg(num(hs.ops, 0, 0))).br();
        b.t(lab("reconcile", L)).t(num(hs.reconcile_ms, W)).t(QString(3 * W, u' ')).t(num(hs.reconcile_ms_max, W)).br();
        b.c(kHostColor[SegGc].html, lab("GC /s", L)).c(hs.gc2_per_s > 0 ? kRed : hs.gc1_per_s > 0 ? kYellow : kGreen,
            QStringLiteral("gen0 %1  gen1 %2  gen2 %3  pause %4 ms").arg(num(hs.gc0_per_s, 0, 1), num(hs.gc1_per_s, 0, 1),
                num(hs.gc2_per_s, 0, 1), num(hs.gc_pause_ms_per_s, 0))).br();
    }
    b.c(kGrey, QStringLiteral("avg: last %1 pumps (phases: pumps with a frame), p/max: last %2").arg(kSummaryPumps).arg(n)).br().br();

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
    const TimelineView view = analyze(*this);
    const int n = view.count;
    Html h;
    pageHeader(h, Timeline, m_frozen);
    h.br().c(kGrey, QStringLiteral("%1 pumps, newest right, scale 0-%2 ms   F7 vvvv/GC/apply %3").arg(n)
        .arg(num(view.top, 0, 1), QLatin1String(s_hostSegments ? "on" : "off")));

    Html f;
    for (int seg = firstSeg(); seg < SegCount; seg++)
        f.c(segColor(seg).html, QStringLiteral("■ ")).t(segName(seg)).t("  ");
    f.br().c(kGpuColor.html, "- GPU  ").c(kBudgetColor.html, QStringLiteral("- budget %1 (work > budget: red)").arg(num(budget, 0)));
    f.t("  ").c(kGrey, "- max").br();
    // Peaks: the 3 largest bars (shown segments) with their dominant segment and age.
    const auto& peaks = view.peaks;
    for (int k = 0; k < 3 && peaks[k] >= 0; k++) {
        const PerfSample& s = viewSample(peaks[k]);
        int dom = firstSeg();
        for (int seg = dom + 1; seg < SegCount; seg++)
            if (segMs(s, seg) > segMs(s, dom))
                dom = seg;
        const float total = view.stack[size_t(peaks[k])];
        // The colour judges the pump's Qt work, like the red highlight; the bar total also holds the waits (and vvvv).
        f.t(k == 0 ? "peaks " : "      ").c(budgetColor(s.workMs(), budget), num(total, 6) + QStringLiteral(" ms"));
        f.t("  ").c(segColor(dom).html, lab(segName(dom), 10)).t(num(segMs(s, dom), 6));
        f.c(kGrey, QStringLiteral("  %1 pumps ago").arg(n - 1 - peaks[k]));
        if (k < 2 && peaks[k + 1] >= 0)
            f.br();
    }
    *foot = f.str();
    return h.str();
}

namespace {

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

// Built only while the page is visible (4 Hz text interval) or on demand (perfPageText); nothing here runs per frame.
QString PerfMonitor::scenePage() const
{
    Html h;
    pageHeader(h, Scene, m_frozen);
    constexpr int L = 12;
    // Elements = reconciler nodes of this window (CREATE minus DESTROY), the root not counted.
    h.br().t(lab("elements", L));
    if (m_scene) {
        h.t(QString::number(qulonglong(m_scene->nodes.size())).rightJustified(8));
        // By type id (stable order); the name is TypeDef::name of the one type table, lower case here.
        std::map<quint16, std::pair<const TypeDef*, int>> byType;
        for (const auto& kv : m_scene->nodes) {
            auto& entry = byType[kv.second.def->id];
            entry.first = kv.second.def;
            entry.second++;
        }
        QString parts;
        for (const auto& kv : byType)
            parts += QStringLiteral("  %1 %2").arg(QString::fromLatin1(kv.second.first->name).toLower()).arg(kv.second.second);
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
    // -1: not finite or beyond any texture
    const auto dip = [](qreal v) { return v >= 0 && v < 1e9 ? int(std::ceil(v)) : -1; };
    QSizeF largest;
    largestLayer(m_window->contentItem(), m_overlay, m_scene ? m_scene->debugOverlay() : nullptr, largest);
    h.br().t(lab("effects", L)).t(QString::number(layers).rightJustified(8))
        .c(kGrey, QStringLiteral("  effect layers, %1 MultiEffect items, largest layer %2 x %3 DIP").arg(effects)
            .arg(dip(largest.width())).arg(dip(largest.height())));
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
    const RendererInfo ri = describeRenderer(m_window);
    h.br().t(lab("backend", L));
    if (ri.hasRhi)
        h.t(ri.backendName).c(kGrey, "  QRhi");
    else if (ri.quickSoftware)
        h.t("software").c(kGrey, "  Qt Quick 2D renderer, no QRhi");
    else
        h.t(kNa);
    h.br().t(lab("adapter", L)).t(ri.adapter.isEmpty() ? kNa : ri.adapter);
    if (const char* type = ri.hasRhi ? deviceTypeName(ri.deviceType) : nullptr)
        h.c(kGrey, QStringLiteral("  %1").arg(QString::fromLatin1(type)));
    h.br().t(lab("rendering", L));
    if (!m_rendererKnown)
        h.c(kGrey, kNa);
    else if (m_renderer.software.isEmpty())
        h.c(kGreen, "GPU");
    else
        h.c(kRed, QStringLiteral("CPU (%1)").arg(m_renderer.software));
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
    h.c(kGrey, (screen ? QStringLiteral("  ") + screen->name() : QString())
        + QStringLiteral("  budget %1 ms (vvvv frame)").arg(num(budgetMs(), 0)));
    // Actual swap chain flag where there is one; the requested QSurfaceFormat value otherwise (software: n/a).
    h.br().t(lab("swap int.", L));
    if (QRhiSwapChain* sc = m_window->swapChain()) {
        const bool vsync = !(sc->flags() & QRhiSwapChain::NoVSync);
        h.t(QString::number(vsync ? 1 : 0)).c(kGrey, vsync ? "  VSync on, Present waits" : "  VSync off, Present does not wait");
    } else if (ri.hasRhi) {
        h.t(QString::number(m_window->format().swapInterval())).c(kGrey, "  requested");
    } else {
        h.t(kNa);
        if (ri.quickSoftware)
            h.c(kGrey, "  software backend presents via the backing store");
    }
    // Does Qt call Present with DXGI_PRESENT_ALLOW_TEARING? D3D11 only. QRhiD3D11::beginFrame/endFrame
    // (qrhid3d11.cpp:1561-1562, v6.12.0) sets it when swapInterval == 0 and the swap chain has
    // DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING; that flag is set when NoVSync and supportsAllowTearing
    // (qrhid3d11.cpp:5422-5430) = RendererInfo::allowTearingSupported.
    h.br().t(lab("allow tearing", L));
    QRhiSwapChain* tearSc = m_window->swapChain();
    if (ri.hasRhi && ri.backend == QRhi::D3D11 && tearSc) {
        const bool noVsync = tearSc->flags() & QRhiSwapChain::NoVSync;
        const bool tearing = noVsync && ri.allowTearingSupported;
        h.c(tearing ? kYellow : kGreen, tearing ? "yes" : "no");
        h.c(kGrey, tearing ? "  Present uses ALLOW_TEARING, frames may tear"
            : !noVsync ? "  VSync on" : "  not supported by the system, Present(0) in the flip model may still wait");
    } else {
        h.c(kGrey, kNa);
    }
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

bool resStuck(const vlqt::res::OverviewRow& r)
{
    using vlqt::res::State;
    return (r.state == State::Decoding || r.state == State::Uploading) && r.ageMs > kResStuckMs;
}

// List order: Failed and stuck first, then the ones in progress, then Ready; the oldest first within each group.
int resRank(const vlqt::res::OverviewRow& r)
{
    using vlqt::res::State;
    if (r.state == State::Failed || resStuck(r))
        return 0;
    return r.state == State::Ready ? 2 : 1;
}

const char* resColor(const vlqt::res::OverviewRow& r)
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
// page is visible (4 Hz text interval) or on demand (perfPageText); the row buffer is reused.
QString PerfMonitor::resourcesPage() const
{
    using vlqt::res::State;
    QElapsedTimer timer; // own build time in the footer (< 0.5 ms with 1000 resources)
    timer.start();
    vlqt::res::OverviewTotals ov;
    vlqt::res::overview(m_resRows, ov);
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
    vlqt::res::snapshot::Stats snap;
    vlqt::res::snapshot::stats(snap);
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

QString perfPageText(const PerfMonitor& m, int page)
{
    QString html, part;
    switch (page) {
    case PerfMonitor::Summary: html = m.summaryPage(&part); break; // also Summary/Timeline (head + body/foot)
    case PerfMonitor::Timeline: html = m.timelinePage(&part); break;
    case PerfMonitor::Scene: html = m.scenePage(); break;
    case PerfMonitor::System: html = m.systemPage(); break;
    case PerfMonitor::Resources: html = m.resourcesPage(); break;
    default: break;
    }
    if (!part.isEmpty())
        html += QLatin1String("<br>") + part;
    return QTextDocumentFragment::fromHtml(html).toPlainText().replace(QChar(0xa0), u' ');
}

} // namespace vlqt