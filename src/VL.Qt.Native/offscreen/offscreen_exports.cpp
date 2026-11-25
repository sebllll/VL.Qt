// The vlqt_offscreen_* exports (vlqt.h): argument checks and the guard around the scene functions of offscreen/. Qt GUI
// thread.
#include "offscreen/offscreen_scene.h"
#include "app/exports.h"
#include "render_thread.h"
#include "vlqt.h"

#include <QtCore/QPointF>

using namespace vlqt::offscreen;
using vlqt::guardedGui;
using vlqt::OffscreenScene;
using vlqt::withOffscreen;

extern "C" {

VLQT_API vlqt_scene* vlqt_offscreen_create(int width, int height)
{
    vlqt_scene* handle = nullptr;
    guardedGui("vlqt_offscreen_create", [&]() -> int {
        handle = create(width, height); // sets vlqt_last_error on nullptr
        return handle ? VLQT_OK : VLQT_ERR_INTERNAL;
    });
    return handle;
}

VLQT_API int vlqt_offscreen_bind(vlqt_scene* scene, void* device, void* ntHandle, int width, int height, int format, int samples,
    int* usedSamples)
{
    if (usedSamples)
        *usedSamples = 0;
    return withOffscreen(scene, "vlqt_offscreen_bind", [&](OffscreenScene& state) -> int {
#ifdef _WIN32
        // bind unbinds first and takes the new binding only after every check: a failure leaves the scene unbound.
        return bind(&state, device, ntHandle, width, height, format, samples, usedSamples);
#else
        (void)state;
        (void)device;
        (void)ntHandle;
        (void)width;
        (void)height;
        (void)format;
        (void)samples;
        return vlqt::fail(VLQT_ERR_UNSUPPORTED, "vlqt_offscreen_bind: needs Direct3D 11 (Windows)");
#endif
    });
}

VLQT_API int vlqt_offscreen_unbind(vlqt_scene* scene)
{
    return withOffscreen(scene, "vlqt_offscreen_unbind", [&](OffscreenScene& state) -> int {
#ifdef _WIN32
        OffscreenScene* s = &state;
        onRenderThread(s, [s] { unbind(s); });
#else
        (void)state;
#endif
        return VLQT_OK;
    });
}

VLQT_API int vlqt_offscreen_set_render_threads(int threadCount)
{
    return guardedGui("vlqt_offscreen_set_render_threads", [&] { return vlqt::render_thread::setThreadCount(threadCount); });
}

VLQT_API int vlqt_offscreen_is_bound(vlqt_scene* scene)
{
    return withOffscreen(scene, "vlqt_offscreen_is_bound", [&](OffscreenScene& state) -> int {
        return state.bound ? 1 : 0;
    });
}

VLQT_API int vlqt_offscreen_render_batch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds)
{
    return guardedGui("vlqt_offscreen_render_batch", [&]() -> int {
        if (!context || count < 0 || (!scenes && count > 0))
            return vlqt::fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_offscreen_render_batch: null context, null scenes or negative count");
#ifdef _WIN32
        return renderBatch(context, scenes, count, driverSeconds);
#else
        (void)driverSeconds;
        return vlqt::fail(VLQT_ERR_UNSUPPORTED, "vlqt_offscreen_render_batch: needs Direct3D 11 (Windows)");
#endif
    });
}

VLQT_API int vlqt_offscreen_send_pointer(vlqt_scene* scene, int device, int type, int id, float x, float y, int buttons,
                                         int modifiers)
{
    return withOffscreen(scene, "vlqt_offscreen_send_pointer", [&](OffscreenScene& state) -> int {
        return sendPointer(&state, device, type, id, QPointF(x, y), buttons, modifiers);
    });
}

VLQT_API int vlqt_offscreen_send_wheel(vlqt_scene* scene, float x, float y, int dx, int dy, int modifiers)
{
    return withOffscreen(scene, "vlqt_offscreen_send_wheel", [&](OffscreenScene& state) -> int {
        return sendWheel(&state, QPointF(x, y), dx, dy, modifiers);
    });
}

VLQT_API int vlqt_offscreen_send_key(vlqt_scene* scene, int type, int key, int modifiers, int autoRepeat)
{
    return withOffscreen(scene, "vlqt_offscreen_send_key", [&](OffscreenScene& state) -> int {
        return sendKey(&state, type, key, modifiers, autoRepeat != 0);
    });
}

VLQT_API int vlqt_offscreen_send_text(vlqt_scene* scene, const char* utf8, int length)
{
    return withOffscreen(scene, "vlqt_offscreen_send_text", [&](OffscreenScene& state) -> int {
        return sendText(&state, utf8, length);
    });
}

VLQT_API int vlqt_offscreen_set_focus(vlqt_scene* scene, int focused)
{
    return withOffscreen(scene, "vlqt_offscreen_set_focus", [&](OffscreenScene& state) -> int {
        setFocus(&state, focused != 0, Qt::ActiveWindowFocusReason);
        return VLQT_OK;
    });
}

} // extern "C"
