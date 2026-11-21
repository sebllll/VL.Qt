// The vlqt_offscreen_* exports (vlqt.h): argument checks and the guard around the scene functions of offscreen/.
#include "offscreen/offscreen_scene.h"
#include "app/exports.h"
#include "render_thread.h"
#include "vlqt.h"

#include <QtCore/QPointF>

#include <string>

using namespace vlqt::offscreen;
using vlqt::OffscreenScene;

namespace {

using vlqt::guarded;

// The offscreen part of handle for the export function; nullptr with vlqt_last_error set for a null handle or a window.
OffscreenScene* sceneOrFail(vlqt_scene* handle, const char* function)
{
    OffscreenScene* s = stateOf(handle);
    if (!s)
        vlqt::setLastError(std::string(function) + ": null handle or not an offscreen scene");
    return s;
}

} // namespace

extern "C" {

VLQT_API vlqt_scene* vlqt_offscreen_create(int width, int height)
{
    vlqt_scene* handle = nullptr;
    guarded([&]() -> int {
        handle = create(width, height); // sets vlqt_last_error on nullptr
        return handle ? VLQT_OK : VLQT_ERR_INTERNAL;
    });
    return handle;
}

VLQT_API int vlqt_offscreen_bind(vlqt_scene* scene, void* device, void* ntHandle, int width, int height, int format, int samples,
    int* usedSamples)
{
    return guarded([&]() -> int {
        if (usedSamples)
            *usedSamples = 0;
        OffscreenScene* state = sceneOrFail(scene, "offscreen bind");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
#ifdef _WIN32
        // bind unbinds first and takes the new binding only after every check: a failure leaves the scene unbound.
        return bind(state, device, ntHandle, width, height, format, samples, usedSamples);
#else
        (void)device;
        (void)ntHandle;
        (void)width;
        (void)height;
        (void)format;
        (void)samples;
        return vlqt::fail(VLQT_ERR_UNSUPPORTED, "offscreen bind: needs Direct3D 11 (Windows)");
#endif
    });
}

VLQT_API int vlqt_offscreen_unbind(vlqt_scene* scene)
{
    return guarded([&]() -> int {
        OffscreenScene* state = sceneOrFail(scene, "offscreen unbind");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
#ifdef _WIN32
        onRenderThread(state, [state] { unbind(state); });
#endif
        return VLQT_OK;
    });
}

VLQT_API int vlqt_offscreen_set_render_threads(int threadCount)
{
    return guarded([&] { return vlqt::render_thread::setThreadCount(threadCount); });
}

VLQT_API int vlqt_offscreen_is_bound(vlqt_scene* scene)
{
    return guarded([&]() -> int {
        OffscreenScene* state = sceneOrFail(scene, "offscreen is_bound");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return state->bound ? 1 : 0;
    });
}

VLQT_API int vlqt_offscreen_render_batch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds)
{
    return guarded([&]() -> int {
        if (!context || count < 0 || (!scenes && count > 0))
            return vlqt::fail(VLQT_ERR_BAD_ARGUMENT, "offscreen render_batch: null context, null scenes or negative count");
#ifdef _WIN32
        return renderBatch(context, scenes, count, driverSeconds);
#else
        (void)driverSeconds;
        return vlqt::fail(VLQT_ERR_UNSUPPORTED, "offscreen render_batch: needs Direct3D 11 (Windows)");
#endif
    });
}

VLQT_API int vlqt_offscreen_send_pointer(vlqt_scene* scene, int device, int type, int id, float x, float y, int buttons,
                                         int modifiers)
{
    return guarded([&]() -> int {
        OffscreenScene* state = sceneOrFail(scene, "offscreen send_pointer");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendPointer(state, device, type, id, QPointF(x, y), buttons, modifiers);
    });
}

VLQT_API int vlqt_offscreen_send_wheel(vlqt_scene* scene, float x, float y, int dx, int dy, int modifiers)
{
    return guarded([&]() -> int {
        OffscreenScene* state = sceneOrFail(scene, "offscreen send_wheel");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendWheel(state, QPointF(x, y), dx, dy, modifiers);
    });
}

VLQT_API int vlqt_offscreen_send_key(vlqt_scene* scene, int type, int key, int modifiers, int autoRepeat)
{
    return guarded([&]() -> int {
        OffscreenScene* state = sceneOrFail(scene, "offscreen send_key");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendKey(state, type, key, modifiers, autoRepeat != 0);
    });
}

VLQT_API int vlqt_offscreen_send_text(vlqt_scene* scene, const char* utf8, int length)
{
    return guarded([&]() -> int {
        OffscreenScene* state = sceneOrFail(scene, "offscreen send_text");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        return sendText(state, utf8, length);
    });
}

VLQT_API int vlqt_offscreen_set_focus(vlqt_scene* scene, int focused)
{
    return guarded([&]() -> int {
        OffscreenScene* state = sceneOrFail(scene, "offscreen set_focus");
        if (!state)
            return VLQT_ERR_NULL_HANDLE;
        setFocus(state, focused != 0, Qt::ActiveWindowFocusReason);
        return VLQT_OK;
    });
}

} // extern "C"
