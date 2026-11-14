#include "offscreen.h"
#include "protocol.h"
#include "vlqt.h"

// TODO: Stubs of #116 task 1.1. Task 1.2 adds the scene (QQuickRenderControl + QQuickWindow sharing struct Window of
// vlqt.cpp), 1.3 bind/unbind with the fences, 1.4 the batch render, 1.5 the adapter check and device loss.

namespace vlqt::offscreen {

int testState(void* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders)
{
    (void)readFence;
    (void)writeFence;
    (void)renders;
    if (!scene)
        return VLQT_OFFSCREEN_NULL_SCENE;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

} // namespace vlqt::offscreen

extern "C" {

VLQT_API void* vlqt_offscreen_create(int width, int height)
{
    (void)width;
    (void)height;
    return nullptr;
}

VLQT_API int vlqt_offscreen_bind(void* scene, void* device, void* ntHandle, int width, int height, int rtvFormat)
{
    (void)device;
    (void)ntHandle;
    (void)width;
    (void)height;
    (void)rtvFormat;
    if (!scene)
        return VLQT_OFFSCREEN_NULL_SCENE;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_offscreen_unbind(void* scene)
{
    if (!scene)
        return VLQT_OFFSCREEN_NULL_SCENE;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_offscreen_is_bound(void* scene)
{
    if (!scene)
        return VLQT_OFFSCREEN_NULL_SCENE;
    return 0;
}

VLQT_API int vlqt_offscreen_render_batch(void* context, void* const* scenes, int count, double driverSeconds)
{
    (void)driverSeconds;
    if (!context || count < 0 || (!scenes && count > 0))
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

} // extern "C"
