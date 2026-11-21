// The perf exports of a scene handle (vlqt.h, "scene": overlay, perf getters, host pump). Qt GUI thread.
#include "window/scene_handle.h"
#include "app/exports.h"
#include "diagnostics/perf.h"
#include "vlqt.h"

using vlqt::asWindow;
using vlqt::fail;
using vlqt::guarded;
using vlqt::PerfSample;

extern "C" {

VLQT_API int vlqt_scene_get_perf(vlqt_scene* p, vlqt_perf* out)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "null out");
        w->perf->summary(out);
        return 0;
    });
}

VLQT_API int vlqt_scene_get_perf_sample(vlqt_scene* p, vlqt_perf_sample* out)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "null out");
        PerfSample s;
        if (!w->perf->lastSample(&s)) {
            *out = vlqt_perf_sample{};
            return fail(VLQT_ERR_NO_DATA, "get_perf_sample: no pump yet");
        }
        *out = vlqt_perf_sample{s.frameInterval, s.pump, s.sync, s.render, s.present, s.gpu, s.events, s.swaps, s.vsync, s.workMs()};
        return 0;
    });
}

VLQT_API int vlqt_scene_get_perf_stats(vlqt_scene* p, vlqt_perf_stats* out)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "null out");
        w->perf->stats(out);
        return 0;
    });
}

VLQT_API int vlqt_scene_report_host_pump(vlqt_scene* p, const vlqt_host_pump* in)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (!in)
            return fail(VLQT_ERR_BAD_ARGUMENT, "null in");
        if (!w->perf->reportHostPump(*in))
            return fail(VLQT_ERR_NO_DATA, "report_host_pump: no pump yet");
        return 0;
    });
}

VLQT_API int vlqt_scene_get_host_pump(vlqt_scene* p, vlqt_host_pump* out)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "null out");
        if (!w->perf->lastHostPump(out)) {
            *out = vlqt_host_pump{};
            return fail(VLQT_ERR_NO_DATA, "get_host_pump: no host pump reported for the last pump");
        }
        return 0;
    });
}

VLQT_API int vlqt_scene_get_host_pump_stats(vlqt_scene* p, vlqt_host_pump_stats* out)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "null out");
        w->perf->hostStats(out);
        return 0;
    });
}

VLQT_API int vlqt_scene_set_overlay(vlqt_scene* p, int flags)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        w->perf->setOverlay(flags);
        return 0;
    });
}

VLQT_API int vlqt_scene_get_overlay(vlqt_scene* p)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "get_overlay: null window");
        return w->perf->overlay();
    });
}

} // extern "C"
