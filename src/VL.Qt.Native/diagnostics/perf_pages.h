// What the F5 pages (perf_pages.cpp) and the timeline item (perf_items.cpp) share: colors and the timeline segments.
#pragma once

#include "diagnostics/perf_monitor.h"

#include <QtGui/QRgb>

namespace vlqt {

// A colour for the items (rgba()) and the text of the pages (html, derived: "#rrggbb"). Declare it constexpr with static
// storage (html is read as a const char*).
struct Rgb {
    int r, g, b;
    char html[8];
    constexpr Rgb(int red, int green, int blue)
        : r(red), g(green), b(blue), html{'#', hex(red >> 4), hex(red), hex(green >> 4), hex(green), hex(blue >> 4), hex(blue), 0} {}
    constexpr QRgb rgba(int a = 255) const { return qRgba(r, g, b, a); }
private:
    static constexpr char hex(int v) { return "0123456789abcdef"[v & 15]; }
};

// The series of the F2 meter and the F5 timeline; the meter text uses the same colours as its legend.
inline constexpr Rgb kVvvvColor{255, 110, 180};
inline constexpr Rgb kWorkColor{255, 160, 40}; // Qt work (the render phase of the timeline has this colour too)
inline constexpr Rgb kGpuColor{80, 220, 120};
inline constexpr Rgb kFrameColor{255, 255, 255};
inline constexpr Rgb kBudgetColor{255, 208, 64};

// Timeline segments, in frame order bottom -> top: the host side of the frame before the Qt pump (vvvv
// update, .NET GC pause, apply), then the Qt phases (PerfMonitor::QtPhase). Host values come from
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

// Plain text of an F5 page (PerfMonitor::Page; Summary and Timeline with body/foot), built on demand, overlay state
// unchanged. The test export vlqt_test_perf_text; also tells the pages from a monitor without an overlay.
QString perfPageText(const PerfMonitor& monitor, int page);

} // namespace vlqt
