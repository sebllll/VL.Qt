// Test exports of vlqt_test.h: probes into a window handle, the perf computations, the shared device, image resources,
// fonts and offscreen scenes (SmokeTest, unit tests). Qt GUI thread, except where an export says "any thread".
#include "../vlqt_test.h"
#include "scene/scene.h"
#include "../device/context_lock.h"
#include "../device/device.h"
#include "../resources/image_resource.h"
#include "../app/exports.h"
#include "offscreen/offscreen.h"
#include "diagnostics/perf.h"
#include "diagnostics/perf_pages.h"
#include "../protocol.h"
#include "test_hooks.h"
#ifdef Q_OS_WIN
#include "../device/render_thread.h"
#include "test_device.h"
#else
#include "offscreen/offscreen_glx.h"
#endif

#include <QtCore/QLoggingCategory>
#include <QtGui/QImage>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
#include <rhi/qrhi.h>
#ifndef Q_OS_WIN
#include <QtGui/QGuiApplication>
#include <QtGui/QOpenGLContext>
#include <QtGui/QOpenGLFunctions>
#endif

#include <string>

using vlqt::asSceneHandle;
using vlqt::fail;
using vlqt::guarded;
using vlqt::guardedGui;
using vlqt::guardedGuiBuffer;
using vlqt::perfEval;
using vlqt::perfHostStats;
using vlqt::perfSummaryEval;
using vlqt::SceneHandle;
using vlqt::withScene;
using vlqt::withWindow;

namespace {

Q_LOGGING_CATEGORY(lcGrab, "vlqt.grab")

#ifndef Q_OS_WIN
// W1-21: on Qt's offscreen platform with OpenGL, Mesa's software winsys can keep an old size of the window's default
// framebuffer (offscreen_glx.h), so a grab would read only part of the frame. Before a grab, make the window's context
// current (Qt sizes its X window to the window) and invalidate the drawable, so the grab's frame takes the window's size.
// Returns the GL context, or null on other platforms and backends.
QOpenGLContext* refreshOffscreenGlx(QQuickWindow* window)
{
    if (QGuiApplication::platformName() != QLatin1String("offscreen"))
        return nullptr;
    QRhi* rhi = window->rhi();
    if (!rhi || rhi->backend() != QRhi::OpenGLES2)
        return nullptr;
    auto* handles = static_cast<const QRhiGles2NativeHandles*>(rhi->nativeHandles());
    QOpenGLContext* ctx = handles ? handles->context : nullptr;
    if (!ctx || !ctx->makeCurrent(window))
        return nullptr;
    static bool s_warned = false;
    if (!vlqt::glx::invalidateCurrentDrawable() && !s_warned) {
        s_warned = true;
        qCWarning(lcGrab, "offscreen GLX: no current drawable or libGL not found, a window grab may read a framebuffer of an old size");
    }
    return ctx;
}

// Returns false if the default framebuffer of the current context does not reach the window's last pixel (a grab then
// read memory that was never written).
bool glxCoversWindow(QOpenGLContext* ctx, QQuickWindow* window)
{
    if (!ctx->makeCurrent(window))
        return true;
    const QSize px = window->size() * window->devicePixelRatio();
    auto* f = ctx->functions();
    f->glBindFramebuffer(GL_FRAMEBUFFER, ctx->defaultFramebufferObject());
    quint32 v = 0xDEADBEEF;
    f->glReadPixels(px.width() - 1, px.height() - 1, 1, 1, GL_RGBA, GL_UNSIGNED_BYTE, &v);
    return v != 0xDEADBEEF;
}
#endif

} // namespace

extern "C" {

VLQT_API int vlqt_test_window_grab(vlqt_scene* p, const char* pngPath)
{
    return withWindow(p, "vlqt_test_window_grab", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& h) -> int {
        QQuickWindow* window = h.window.get();
        QImage img;
#ifndef Q_OS_WIN
        QOpenGLContext* glx = refreshOffscreenGlx(window);
#endif
        {
            // The frame of grabWindow ends without afterFrameEnd (QSGGuiThreadRenderLoop::grab).
            vlqt::device::ContextLock lock;
            img = window->grabWindow();
            vlqt::device::releaseFrameLocks();
        }
#ifndef Q_OS_WIN
        if (glx && !glxCoversWindow(glx, window)) {
            int w3, h3;
            vlqt::glx::currentDrawableSize(&w3, &h3);
            const QSize px = window->size() * window->devicePixelRatio();
            qCWarning(lcGrab, "offscreen GLX framebuffer smaller than the window (%d x %d px, X window %d x %d px): grab %s is incomplete",
                px.width(), px.height(), w3, h3, pngPath);
        }
#endif
        if (img.isNull())
            return fail(VLQT_ERR_NO_DATA, "test_window_grab: grabWindow returned a null image");
        if (!img.save(QString::fromUtf8(pngPath), "PNG"))
            return fail(VLQT_ERR_IO, "test_window_grab: saving the png failed");
        return VLQT_OK;
    });
}

// Pure computations: any thread.
VLQT_API int vlqt_test_perf_eval(const vlqt_perf_sample* samples, const double* t_ms, int n, double budget_ms, vlqt_perf_stats* out)
{
    return guarded([&]() -> int {
        if (!out || n < 0 || (n > 0 && (!samples || !t_ms)))
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_perf_eval: null or negative argument");
        perfEval(samples, t_ms, n, budget_ms, out);
        return 0;
    });
}

VLQT_API int vlqt_test_perf_summary_eval(const vlqt_perf_sample* samples, int n, vlqt_perf* out)
{
    return guarded([&]() -> int {
        if (!out || n < 0 || (n > 0 && !samples))
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_perf_summary_eval: null or negative argument");
        perfSummaryEval(samples, n, out);
        return 0;
    });
}

VLQT_API int vlqt_test_perf_host_eval(const vlqt_host_pump* pumps, int n, vlqt_host_pump_stats* out)
{
    return guarded([&]() -> int {
        if (!out || n < 0 || (n > 0 && !pumps))
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_perf_host_eval: null or negative argument");
        perfHostStats(pumps, n, out);
        return 0;
    });
}

VLQT_API int vlqt_test_handler_count(vlqt_scene* p, unsigned int id)
{
    return withScene(p, "vlqt_test_handler_count", [&](SceneHandle& h) -> int {
        vlqt::Scene* scene = h.scene.get();
        auto it = scene->nodes.find(id);
        if (it == scene->nodes.end())
            return fail(VLQT_ERR_UNKNOWN_OBJECT, "test_handler_count: unknown id " + std::to_string(id));
        int n = 0;
        for (QObject* c : it->second.item->children())
            if (c->inherits("QQuickPointerHandler"))
                ++n;
        return n;
    });
}

VLQT_API int vlqt_test_set_overlay(vlqt_scene* p, int flags)
{
    return withScene(p, "vlqt_test_set_overlay", [&](SceneHandle& h) -> int {
        h.perf->setOverlay(flags);
        return VLQT_OK;
    });
}

VLQT_API int vlqt_test_perf_spike(vlqt_scene* p, int phase, float ms)
{
    return withScene(p, "vlqt_test_perf_spike", [&](SceneHandle& h) -> int {
        h.perf->injectSpike(phase, ms);
        return VLQT_OK;
    });
}

VLQT_API int vlqt_test_perf_text(vlqt_scene* p, int page, char* out, int cap, int* written, int* required)
{
    return guarded([&]() -> int {
        if (const int rc = vlqt::beginBuffer(out, cap, written, required); rc != VLQT_OK)
            return rc;
        if (const int rc = vlqt::checkScene(p, "vlqt_test_perf_text"); rc != VLQT_OK)
            return rc;
        const QByteArray utf8 = vlqt::perfPageText(*asSceneHandle(p)->perf, page).toUtf8();
        return vlqt::copyText(utf8.constData(), int(utf8.size()), out, cap, written, required);
    });
}

VLQT_API int vlqt_test_device_info(vlqt_scene* p, void** device, void** rhi)
{
    return guardedGui("vlqt_test_device_info", [&]() -> int {
        QRhi* r = p ? asSceneHandle(p)->window->rhi() : vlqt::device::uploadRhi();
        if (device)
            *device = vlqt::device::nativeDevice(r);
        if (rhi)
            *rhi = r;
        return r ? VLQT_OK : fail(VLQT_ERR_NO_DATA, "test_device_info: no QRhi (scene graph not initialized, or no shared device)");
    });
}

VLQT_API unsigned long long vlqt_test_device_luid(void)
{
    unsigned long long luid = 0;
    guardedGui("vlqt_test_device_luid", [&]() -> int {
        luid = vlqt::device::adapterLuid();
        return VLQT_OK;
    });
    return luid;
}

VLQT_API int vlqt_test_device_lost(int mode)
{
    return guardedGui("vlqt_test_device_lost", [&]() -> int {
        if (mode != VLQT_DEVICE_LOST_REMOVE && mode != VLQT_DEVICE_LOST_HANG && mode != VLQT_DEVICE_LOST_REMOVE_FAIL_ONCE)
            return fail(VLQT_ERR_BAD_ARGUMENT, "test_device_lost: unknown mode " + std::to_string(mode));
#ifdef Q_OS_WIN
        if (mode == VLQT_DEVICE_LOST_HANG) {
            // Not implemented: a TDR resets the whole GPU (every application). Manual check: dxcap -forcetdr (admin).
            return fail(VLQT_ERR_UNSUPPORTED, "test_device_lost(HANG) not implemented, use 'dxcap -forcetdr' for a real TDR");
        }
        auto* dev12 = static_cast<ID3D12Device*>(vlqt::device::testDevice12());
        if (!dev12)
            return fail(VLQT_ERR_UNSUPPORTED, "test_device_lost(REMOVE) needs the shared device on D3D11On12 "
                                              "(VLQT_TEST_D3D11ON12=1 before the first window or resource)");
        // The device goes between two frames, not under a render job a render_batch just posted: D3D11On12 then keeps an
        // acquire of the scene's shared texture in its batch without a fence, and destroying the old device (releaseRetired)
        // runs it as ID3D12CommandQueue::Wait(nullptr) - an access violation in D3D12Core (1 in about 10 threaded runs).
        vlqt::render_thread::drainAll();
        const int rc = vlqt::device::test::removeDevice(dev12);
        if (rc == 0 && mode == VLQT_DEVICE_LOST_REMOVE_FAIL_ONCE)
            vlqt::device::test::failNextCreation();
        return rc;
#else
        return fail(VLQT_ERR_UNSUPPORTED, "test_device_lost: needs Direct3D 11 (Windows)");
#endif
    });
}

VLQT_API int vlqt_test_res_read_pixels(unsigned long long id, unsigned char* out, int cap, int* width, int* height,
    int* written, int* required)
{
    return guardedGuiBuffer("vlqt_test_res_read_pixels", out, cap, written, required,
        [&] { return vlqt::res::image::testRead(id, out, cap, width, height, written, required); });
}

VLQT_API int vlqt_test_font_refs(unsigned long long handle)
{
    return guardedGui("vlqt_test_font_refs", [&] { return vlqt::test::fontRefs(handle); });
}

VLQT_API int vlqt_test_offscreen_state(vlqt_scene* scene, unsigned long long* readFence, unsigned long long* writeFence,
                                       long long* renders)
{
    return guardedGui("vlqt_test_offscreen_state", [&] { return vlqt::offscreen::testState(scene, readFence, writeFence, renders); });
}

} // extern "C"