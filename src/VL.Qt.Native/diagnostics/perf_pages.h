// What the F5 pages (perf_pages.cpp) and the timeline item (perf_items.cpp) share: colors and the timeline segments.
#pragma once

#include "diagnostics/perf_monitor.h"

namespace vlqt {

struct Rgb { int r, g, b; const char* html; };
inline constexpr Rgb kGpuColor{80, 220, 120, "#50dc78"};
inline constexpr Rgb kBudgetColor{255, 208, 64, "#ffd040"};

// Timeline segments, in frame order bottom -> top: the host side of the frame before the Qt pump (vvvv
// update, .NET GC pause, apply), then the Qt phases (PerfMonitor::Phase in kPhaseOrder). Host values come from
// PerfSample.host (0 without a report).
// GC is carved out of the vvvv segment: the pause is reported per frame without its position, and most collections
// fall into the vvvv update (allocations of the patch); a pause longer than the vvvv update is shown in full.
enum Segment { SegVvvv, SegGc, SegApply, SegQt };
inline constexpr int SegCount = SegQt + int(PerfMonitor::PhaseCount);

// F7 (stats visible) hides/shows the host segments (process-wide like the page).
extern bool s_hostSegments;

const Rgb& segColor(int seg);
float segMs(const PerfSample& s, int seg);
// First segment shown (host segments off: the Qt phases only).
int firstSeg();
// Bar height: the shown segments; = pump without host segments (events = pump - vsync - sync - render - present).
float stackMs(const PerfSample& s);

} // namespace vlqt
