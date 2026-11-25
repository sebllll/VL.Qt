#include "diagnostics/perf_stats.h"

#include <algorithm>
#include <cmath>

namespace vlqt {

void PerfCounters::add(const PerfSample& s, double tMs, double budgetMs)
{
    const long long sec = (long long)std::floor(tMs / 1000.0);
    const int b = int(((sec % kRecentSeconds) + kRecentSeconds) % kRecentSeconds);
    if (m_bucketSec[b] != sec) {
        m_buckets[b] = Counts{};
        m_bucketSec[b] = sec;
    }
    const bool over = s.workMs() > budgetMs;
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
    const int first = std::max(0, n - kPerfHistory);
    std::array<float, kPerfHistory> pump, frame;
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

namespace {

PerfSample toPerfSample(const vlqt_perf_sample& v)
{
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
    return s;
}

} // namespace

void perfEval(const vlqt_perf_sample* samples, const double* tMs, int n, double budgetMs, vlqt_perf_stats* out)
{
    PerfCounters counters;
    std::array<PerfSample, kPerfHistory> ring;
    const int first = std::max(0, n - kPerfHistory);
    for (int i = 0; i < n; i++) {
        const PerfSample s = toPerfSample(samples[i]);
        counters.add(s, tMs[i], budgetMs);
        if (i >= first)
            ring[i - first] = s;
    }
    perfStats(ring.data(), n - first, counters, n > 0 ? tMs[n - 1] : 0, budgetMs, out);
}

PerfWindow perfWindow(const PerfSample* samples, int n, int window)
{
    PerfWindow w;
    const int first = std::max(0, n - window);
    double frameSum = 0, pumpSum = 0, workSum = 0, gpuSum = 0, swaps = 0;
    double events = 0, vsync = 0, sync = 0, render = 0, present = 0;
    for (int i = first; i < n; i++) {
        const PerfSample& s = samples[i];
        w.pumps++;
        pumpSum += s.pump;
        w.pumpMax = std::max(w.pumpMax, double(s.pump));
        workSum += s.workMs();
        swaps += s.swaps;
        if (s.gpu > 0) {
            gpuSum += s.gpu;
            w.gpuMax = std::max(w.gpuMax, double(s.gpu));
            w.gpus++;
        }
        if (s.swaps == 0) {
            w.idle++;
            continue;
        }
        w.rendered++;
        events += s.events;
        vsync += s.vsync;
        sync += s.sync;
        render += s.render;
        present += s.present;
        if (consecutiveFrame(samples, i)) {
            frameSum += s.frameInterval;
            w.frameMax = std::max(w.frameMax, double(s.frameInterval));
            w.frames++;
        }
    }
    if (w.pumps) {
        w.pumpAvg = pumpSum / w.pumps;
        w.workAvg = workSum / w.pumps;
        w.swapsPerPump = swaps / w.pumps;
    }
    if (w.rendered) {
        w.eventsAvg = events / w.rendered;
        w.vsyncAvg = vsync / w.rendered;
        w.syncAvg = sync / w.rendered;
        w.renderAvg = render / w.rendered;
        w.presentAvg = present / w.rendered;
    }
    if (w.frames) {
        w.frameAvg = frameSum / w.frames;
        w.fps = w.frameAvg > 0 ? 1000.0 / w.frameAvg : 0;
    }
    if (w.gpus)
        w.gpuAvg = gpuSum / w.gpus;
    return w;
}

void perfSummary(const PerfWindow& w, vlqt_perf* out)
{
    *out = vlqt_perf{};
    out->fps = w.fps;
    out->frame_ms = w.frameAvg;
    out->frame_ms_max = w.frameMax;
    out->pump_ms = w.pumpAvg;
    out->pump_ms_max = w.pumpMax;
    out->sync_ms = w.syncAvg;
    out->render_ms = w.renderAvg;
    out->present_ms = w.presentAvg;
    out->gpu_ms = w.gpuAvg;
    out->gpu_ms_max = w.gpuMax;
    out->swaps_per_pump = w.swapsPerPump;
    out->samples = w.pumps;
    out->vsync_ms = w.vsyncAvg;
    out->work_ms = w.workAvg;
}

void perfSummaryEval(const vlqt_perf_sample* samples, int n, vlqt_perf* out)
{
    std::array<PerfSample, kPerfHistory> ring;
    const int first = std::max(0, n - kPerfHistory);
    for (int i = first; i < n; i++)
        ring[i - first] = toPerfSample(samples[i]);
    perfSummary(perfWindow(ring.data(), n - first, kSummaryPumps), out);
}

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

} // namespace vlqt
