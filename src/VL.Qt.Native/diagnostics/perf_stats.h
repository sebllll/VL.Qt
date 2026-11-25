// The measured values of the perf monitor and the statistics over them: no Qt, no window, testable with
// vlqt_test_perf_eval and vlqt_test_perf_host_eval (perf_exports.cpp).
#pragma once

#include "vlqt.h"

#include <algorithm>
#include <array>

namespace vlqt {

// Samples the ring of a window keeps (PerfMonitor::kHistory).
inline constexpr int kPerfHistory = 240;
// Pumps (about 1 s at 60 Hz) that the summary (vlqt_perf, F2 meter) and the Summary page average over.
inline constexpr int kSummaryPumps = 60;

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

    // Qt work of this pump: events + sync + render, i.e. the pump without the vsync wait and the present (both wait for
    // the display refresh or the GPU). The one definition behind over budget, the work colours, the timeline's red
    // highlight, the F2 series "work" and vlqt_perf_sample.work, which the C# slow-frame warning reads. >= 0.
    float workMs() const { return std::max(0.f, pump - vsync - present); }
};

// Running counters of a window's pumps: since start and over the last kRecentSeconds (1 s buckets).
// No Qt, no allocation; add() is O(1), recent() sums kRecentSeconds buckets.
class PerfCounters
{
public:
    PerfCounters() { m_bucketSec.fill(-1); }
    static constexpr int kRecentSeconds = 10;
    struct Counts {
        long long pumps = 0;
        long long overBudget = 0; // workMs() > budget
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
    std::array<long long, kRecentSeconds> m_bucketSec; // second of each bucket, -1 = unused
    bool m_prevSwap = false; // previous pump had a frame
};

// samples[i] has a frame interval between consecutive frames (previous pump also had a frame); the interval
// after idle pumps (on-demand rendering) spans the idle time and is not a frame time.
bool consecutiveFrame(const PerfSample* samples, int i);

// The averages and maxima over the last pumps of a ring, computed once for vlqt_perf (PerfStats), the F2 meter text and the
// F5 Summary page, so that they agree. Rules:
//  - Frame statistics (frameAvg, frameMax, fps) only from consecutive frames (consecutiveFrame, like the percentiles).
//    After idle pumps the interval spans the idle time and is not a frame time. 0 = no consecutive frame in the window
//    (the scene is idle or renders on demand): fps is then 0, not a low number.
//  - Phase averages (events, vsync, sync, render, present) per pump that rendered (swaps > 0): the cost of a rendered
//    pump, not diluted by the idle ones.
//  - Pump, Qt work and swaps per pump of the window, GPU per pump with a timestamp.
struct PerfWindow {
    int pumps = 0;     // pumps in the window
    int rendered = 0;  // of those with a frame (swaps > 0)
    int idle = 0;      // without a frame
    int frames = 0;    // of those with a consecutive frame interval
    int gpus = 0;      // of those with a GPU timestamp
    double frameAvg = 0, frameMax = 0; // ms, 0 = no consecutive frame
    double fps = 0;                    // 1000 / frameAvg, 0 = no consecutive frame
    double pumpAvg = 0, pumpMax = 0;   // ms
    double workAvg = 0;                // ms, PerfSample::workMs
    double eventsAvg = 0, vsyncAvg = 0, syncAvg = 0, renderAvg = 0, presentAvg = 0; // ms per rendered pump
    double gpuAvg = 0, gpuMax = 0;     // ms, 0 = unavailable
    double swapsPerPump = 0;
};

// The window over the last `window` of n samples (oldest to newest). The sample before the window, if there is one, only
// decides whether the first frame interval is a consecutive one.
PerfWindow perfWindow(const PerfSample* samples, int n, int window);

// The vlqt_perf of a window (vlqt_scene_get_perf).
void perfSummary(const PerfWindow& w, vlqt_perf* out);

// The same on given samples, the last kSummaryPumps of them (vlqt_test_perf_summary_eval).
void perfSummaryEval(const vlqt_perf_sample* samples, int n, vlqt_perf* out);

// Fills out from n <= kPerfHistory samples (oldest to newest) and the counters. Percentiles are nearest-rank; sorts
// copies on the stack, so call it at query time only.
void perfStats(const PerfSample* samples, int n, const PerfCounters& counters, double nowMs, double budgetMs,
    vlqt_perf_stats* out);

// The same computation on given samples with timestamps (vlqt_test_perf_eval).
void perfEval(const vlqt_perf_sample* samples, const double* tMs, int n, double budgetMs, vlqt_perf_stats* out);

// averages/maxima/rates of n host pumps (PerfMonitor::hostStats and vlqt_test_perf_host_eval). No allocation.
void perfHostStats(const vlqt_host_pump* frames, int n, vlqt_host_pump_stats* out);

} // namespace vlqt
