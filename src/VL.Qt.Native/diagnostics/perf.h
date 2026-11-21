// The perf monitor and overlay as the rest of QtNative uses them (PerfMonitor itself is in perf_monitor.h).
#pragma once

#include "diagnostics/perf_monitor.h"

namespace vlqt {

// Registers the QML types of the overlay (PerfGraph, PerfTimeline as module VLQt.Perf); once at app start.
void registerPerfTypes();

} // namespace vlqt
