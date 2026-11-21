// The measured values of the perf monitor and the statistics over them: no Qt, no window, testable with
// vlqt_test_perf_eval and vlqt_test_perf_host_eval (perf_exports.cpp).
#pragma once

#include "vlqt.h"

#include <array>

namespace vlqt {

// Samples the ring of a window keeps (PerfMonitor::kHistory).
inline constexpr int kPerfHistory = 240;

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

// Fills out from n samples (oldest to newest, the last up to kPerfHistory are used for the percentiles)
// and the counters. Percentiles are nearest-rank; sorts copies on the stack, so call it at query time only.
void perfStats(const PerfSample* samples, int n, const PerfCounters& counters, double nowMs, double budgetMs,
    vlqt_perf_stats* out);

// The same computation on given samples with timestamps (vlqt_test_perf_eval).
void perfEval(const vlqt_perf_sample* samples, const double* tMs, int n, double budgetMs, vlqt_perf_stats* out);

// averages/maxima/rates of n host pumps (PerfMonitor::hostStats and vlqt_test_perf_host_eval). No allocation.
void perfHostStats(const vlqt_host_pump* frames, int n, vlqt_host_pump_stats* out);

} // namespace vlqt
