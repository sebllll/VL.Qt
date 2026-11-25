// The resource exports (vlqt.h: res, image, shader, snapshot, audio tap): thin wrappers, the manager logs its own
// warnings (category VL.Qt.Resources). Qt GUI thread (guardedGui, withScene), except the four that vlqt.h documents for
// any thread (guarded): vlqt_shader_reflect, vlqt_image_write_file, vlqt_audio_tap_read, vlqt_audio_tap_now_ns.
#include "window/scene_handle.h"
#include "app/exports.h"
#include "audio_tap.h"
#include "scene/scene.h"
#include "image_resource.h"
#include "image_writer.h"
#include "resources/resources.h"
#include "shader_resource.h"
#include "snapshot_resource.h"
#include "vlqt.h"

#include <cstdint>

using vlqt::guarded;
using vlqt::guardedGui;
using vlqt::guardedGuiBuffer;
using vlqt::SceneHandle;
using vlqt::withScene;

extern "C" {

VLQT_API int vlqt_res_create(int kind, unsigned long long* outId)
{
    if (outId)
        *outId = 0;
    return guardedGui("vlqt_res_create", [&] {
        uint64_t id = 0;
        const int rc = vlqt::res::create(kind, &id);
        if (outId)
            *outId = id;
        return rc;
    });
}

VLQT_API int vlqt_res_release(unsigned long long id)
{
    return guardedGui("vlqt_res_release", [&] { return vlqt::res::release(id); });
}

VLQT_API int vlqt_res_set_budget(int bytes)
{
    return guardedGui("vlqt_res_set_budget", [&] { return vlqt::res::setBudget(bytes); });
}

VLQT_API int vlqt_res_poll(unsigned char* out, int cap, int* written, int* required)
{
    return guardedGuiBuffer("vlqt_res_poll", out, cap, written, required,
        [&] { return vlqt::res::poll(out, cap, written, required); });
}

VLQT_API int vlqt_res_wait(unsigned long long id, int timeoutMs)
{
    return guardedGui("vlqt_res_wait", [&] { return vlqt::res::wait(id, timeoutMs); });
}

VLQT_API int vlqt_res_list(vlqt_res_info* out, int cap)
{
    return guardedGui("vlqt_res_list", [&] { return vlqt::res::list(out, cap); });
}

VLQT_API void vlqt_res_shutdown(void)
{
    guardedGui("vlqt_res_shutdown", [&] {
        vlqt::res::shutdown();
        return 0;
    });
}

VLQT_API int vlqt_image_load(unsigned long long id, const char* utf8Path, unsigned flags, int upload)
{
    return guardedGui("vlqt_image_load", [&] { return vlqt::res::image::load(id, utf8Path, flags, upload); });
}

VLQT_API int vlqt_image_set_pixels(unsigned long long id, const vlqt_pixel_desc* desc, const void* data, int len, int upload)
{
    return guardedGui("vlqt_image_set_pixels", [&] { return vlqt::res::image::setPixels(id, desc, data, len, upload); });
}

VLQT_API int vlqt_shader_load(unsigned long long id, const char* utf8Path, int stage)
{
    return guardedGui("vlqt_shader_load", [&] { return vlqt::res::shader::load(id, utf8Path, stage); });
}

VLQT_API int vlqt_shader_load_source(unsigned long long id, const char* utf8Source, int stage)
{
    return guardedGui("vlqt_shader_load_source", [&] { return vlqt::res::shader::loadSource(id, utf8Source, stage); });
}

// Any thread: the shader node factory reflects outside the main thread.
VLQT_API int vlqt_shader_reflect(const char* utf8Source, int stage, char* buf, int bufLen, int* written, int* required)
{
    return guarded([&] { return vlqt::res::shader::reflect(utf8Source, stage, buf, bufLen, written, required); });
}

VLQT_API int vlqt_snapshot_start(unsigned long long id, vlqt_scene* p, unsigned elementId, float scale)
{
    return withScene(p, "vlqt_snapshot_start", [&](SceneHandle& w) {
        return vlqt::res::snapshot::start(id, *w.scene, elementId, scale);
    });
}

VLQT_API int vlqt_snapshot_read(unsigned long long id, void* dst, int cap, vlqt_pixel_desc* out, int* written, int* required)
{
    return guardedGuiBuffer("vlqt_snapshot_read", dst, cap, written, required,
        [&] { return vlqt::res::snapshot::readPixels(id, dst, cap, out, written, required); });
}

VLQT_API int vlqt_image_read_preview(unsigned long long id, unsigned char* dst, int cap, vlqt_pixel_desc* desc, int* written,
    int* required)
{
    return guardedGuiBuffer("vlqt_image_read_preview", dst, cap, written, required,
        [&] { return vlqt::res::image::readPreview(id, dst, cap, desc, written, required); });
}

// Audio taps: audio_tap.h. init on the GUI thread, read on VL.Audio's audio thread.
VLQT_API int vlqt_audio_tap_init(unsigned long long id, int channels, int sampleRate)
{
    return guardedGui("vlqt_audio_tap_init", [&] { return vlqt::res::audio::init(id, channels, sampleRate); });
}

// Any thread, allocates nothing: no thread check (the guard allocates only for an exception).
VLQT_API int vlqt_audio_tap_read(unsigned long long id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state)
{
    return guarded([&] { return vlqt::res::audio::read(id, dst, maxFrames, channels, state); });
}

// Any thread.
VLQT_API long long vlqt_audio_tap_now_ns(void)
{
    return vlqt::res::audio::nowNs();
}

// Any thread: the FileWriter nodes write from a task.
VLQT_API int vlqt_image_write_file(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality)
{
    return guarded([&] { return vlqt::imagewriter::write(desc, data, len, utf8Path, quality); });
}

VLQT_API int vlqt_snapshot_start_fit(unsigned long long id, vlqt_scene* p, unsigned elementId, int maxEdgePx)
{
    return withScene(p, "vlqt_snapshot_start_fit", [&](SceneHandle& w) {
        return vlqt::res::snapshot::startFit(id, *w.scene, elementId, maxEdgePx);
    });
}

} // extern "C"
