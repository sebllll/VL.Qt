// The resource exports (vlqt.h: res, image, shader, snapshot, audio tap): thin wrappers, the manager logs its own
// warnings (category VL.Qt.Resources). Main thread unless vlqt.h says "any thread".
#include "window/scene_handle.h"
#include "app/exports.h"
#include "scene/scene.h"
#include "resources/image_resource.h"
#include "image_writer.h"
#include "resources/audio_tap.h"
#include "resources/resources.h"
#include "shader_resource.h"
#include "snapshot_resource.h"
#include "vlqt.h"

#include <cstdint>

using vlqt::asWindow;
using vlqt::fail;
using vlqt::guarded;

extern "C" {

VLQT_API int vlqt_res_create(int kind, unsigned long long* outId)
{
    return guarded([&] {
        uint64_t id = 0;
        const int rc = vlqt::res::create(kind, &id);
        if (outId)
            *outId = id;
        return rc;
    });
}

VLQT_API int vlqt_res_release(unsigned long long id)
{
    return guarded([&] { return vlqt::res::release(id); });
}

VLQT_API int vlqt_res_set_budget(int bytes)
{
    return guarded([&] { return vlqt::res::setBudget(bytes); });
}

VLQT_API int vlqt_res_poll(unsigned char* out, int cap, int* written, int* required)
{
    return guarded([&] { return vlqt::res::poll(out, cap, written, required); });
}

VLQT_API int vlqt_res_wait(unsigned long long id, int timeoutMs)
{
    return guarded([&] { return vlqt::res::wait(id, timeoutMs); });
}

VLQT_API int vlqt_res_list(vlqt_res_info* out, int cap)
{
    return guarded([&] { return vlqt::res::list(out, cap); });
}

VLQT_API void vlqt_res_shutdown(void)
{
    guarded([&] {
        vlqt::res::shutdown();
        return 0;
    });
}

VLQT_API int vlqt_image_load(unsigned long long id, const char* utf8Path, unsigned flags, int upload)
{
    return guarded([&] { return vlqt::res::image::load(id, utf8Path, flags, upload); });
}

VLQT_API int vlqt_image_set_pixels(unsigned long long id, const vlqt_pixel_desc* desc, const void* data, int len, int upload)
{
    return guarded([&] { return vlqt::res::image::setPixels(id, desc, data, len, upload); });
}

VLQT_API int vlqt_shader_load(unsigned long long id, const char* utf8Path, int stage)
{
    return guarded([&] { return vlqt::res::shader::load(id, utf8Path, stage); });
}

VLQT_API int vlqt_shader_load_source(unsigned long long id, const char* utf8Source, int stage)
{
    return guarded([&] { return vlqt::res::shader::loadSource(id, utf8Source, stage); });
}

VLQT_API int vlqt_shader_reflect(const char* utf8Source, int stage, char* buf, int bufLen, int* written, int* required)
{
    return guarded([&] { return vlqt::res::shader::reflect(utf8Source, stage, buf, bufLen, written, required); });
}

VLQT_API int vlqt_snapshot_start(unsigned long long id, vlqt_scene* p, unsigned elementId, float scale)
{
    return guarded([&] {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "snapshot: null window");
        return vlqt::res::snapshot::start(id, *w->scene, elementId, scale);
    });
}

VLQT_API int vlqt_snapshot_read(unsigned long long id, void* dst, int cap, vlqt_pixel_desc* out, int* written, int* required)
{
    return guarded([&] { return vlqt::res::snapshot::readPixels(id, dst, cap, out, written, required); });
}

VLQT_API int vlqt_image_read_preview(unsigned long long id, unsigned char* dst, int cap, vlqt_pixel_desc* desc, int* written,
    int* required)
{
    return guarded([&] { return vlqt::res::image::readPreview(id, dst, cap, desc, written, required); });
}

// Audio taps: resources/audio_tap.h; read runs on VL.Audio's audio thread (no lock, one reader per tap).
VLQT_API int vlqt_audio_tap_init(unsigned long long id, int channels, int sampleRate)
{
    return guarded([&] { return vlqt::res::audio::init(id, channels, sampleRate); });
}

VLQT_API int vlqt_audio_tap_read(unsigned long long id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state)
{
    return guarded([&] { return vlqt::res::audio::read(id, dst, maxFrames, channels, state); });
}

VLQT_API long long vlqt_audio_tap_now_ns(void)
{
    return vlqt::res::audio::nowNs();
}

VLQT_API int vlqt_image_write_file(const vlqt_pixel_desc* desc, const void* data, int len, const char* utf8Path, int quality)
{
    return guarded([&] { return vlqt::imagewriter::write(desc, data, len, utf8Path, quality); });
}

VLQT_API int vlqt_snapshot_start_fit(unsigned long long id, vlqt_scene* p, unsigned elementId, int maxEdgePx)
{
    return guarded([&] {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "snapshot_fit: null window");
        return vlqt::res::snapshot::startFit(id, *w->scene, elementId, maxEdgePx);
    });
}

} // extern "C"
