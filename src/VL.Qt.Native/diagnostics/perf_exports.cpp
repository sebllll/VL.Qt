// The perf exports of a scene handle (vlqt.h, "scene": overlay, perf getters, host pump). Qt GUI thread.
#include "window/scene_handle.h"
#include "app/exports.h"
#include "diagnostics/perf.h"
#include "vlqt.h"

using vlqt::fail;
using vlqt::PerfSample;
using vlqt::SceneHandle;
using vlqt::withScene;

extern "C" {

VLQT_API int vlqt_scene_get_perf(vlqt_scene* p, vlqt_perf* out)
{
    return withScene(p, "vlqt_scene_get_perf", [&](SceneHandle& w) -> int {
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_get_perf: null out");
        w.perf->summary(out);
        return 0;
    });
}

VLQT_API int vlqt_scene_get_perf_sample(vlqt_scene* p, vlqt_perf_sample* out)
{
    return withScene(p, "vlqt_scene_get_perf_sample", [&](SceneHandle& w) -> int {
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_get_perf_sample: null out");
        PerfSample s;
        if (!w.perf->lastSample(&s)) {
            *out = vlqt_perf_sample{};
            return fail(VLQT_ERR_NO_DATA, "vlqt_scene_get_perf_sample: no pump yet");
        }
        *out = vlqt_perf_sample{s.frameInterval, s.pump, s.sync, s.render, s.present, s.gpu, s.events, s.swaps, s.vsync};
        return 0;
    });
}

VLQT_API int vlqt_scene_get_perf_stats(vlqt_scene* p, vlqt_perf_stats* out)
{
    return withScene(p, "vlqt_scene_get_perf_stats", [&](SceneHandle& w) -> int {
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_get_perf_stats: null out");
        w.perf->stats(out);
        return 0;
    });
}

VLQT_API int vlqt_scene_report_host_pump(vlqt_scene* p, const vlqt_host_pump* in)
{
    return withScene(p, "vlqt_scene_report_host_pump", [&](SceneHandle& w) -> int {
        if (!in)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_report_host_pump: null in");
        if (!w.perf->reportHostPump(*in))
            return fail(VLQT_ERR_NO_DATA, "vlqt_scene_report_host_pump: no pump yet");
        return 0;
    });
}

VLQT_API int vlqt_scene_get_host_pump(vlqt_scene* p, vlqt_host_pump* out)
{
    return withScene(p, "vlqt_scene_get_host_pump", [&](SceneHandle& w) -> int {
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_get_host_pump: null out");
        if (!w.perf->lastHostPump(out)) {
            *out = vlqt_host_pump{};
            return fail(VLQT_ERR_NO_DATA, "vlqt_scene_get_host_pump: no host pump reported for the last pump");
        }
        return 0;
    });
}

VLQT_API int vlqt_scene_get_host_pump_stats(vlqt_scene* p, vlqt_host_pump_stats* out)
{
    return withScene(p, "vlqt_scene_get_host_pump_stats", [&](SceneHandle& w) -> int {
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_get_host_pump_stats: null out");
        w.perf->hostStats(out);
        return 0;
    });
}

VLQT_API int vlqt_scene_get_overlay(vlqt_scene* p)
{
    return withScene(p, "vlqt_scene_get_overlay", [&](SceneHandle& w) -> int {
        return w.perf->overlay();
    });
}

} // extern "C"
