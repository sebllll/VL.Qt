// The perf monitor and overlay as the rest of QtNative uses them (PerfMonitor itself is in perf_monitor.h).
#pragma once

#include "diagnostics/perf_monitor.h"

namespace vlqt {

// objectName of the F5 overlay item: its changes are no scene change (vlqt_scene_get_change_count).
inline constexpr char perfOverlayName[] = "vlqt-perf-overlay";

// Registers the QML types of the overlay (PerfGraph, PerfTimeline as module VLQt.Perf); once at app start.
void registerPerfTypes();

} // namespace vlqt
