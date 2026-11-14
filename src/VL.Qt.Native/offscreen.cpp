#include "offscreen.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickRenderControl>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>

#include <algorithm>
#include <exception>

// TODO: Stubs of #116 task 1.1 left: 1.3 bind/unbind with the fences, 1.4 the batch render, 1.5 the adapter check and
// device loss.

namespace vlqt::offscreen {

// Lifetime: vlqt_offscreen_create -> (bind -> render_batch ... -> unbind)* -> vlqt_window_destroy (release, then
// destroy after the QQuickWindow). The QRhi of the scene belongs to renderControl and lives on QtNative's shared device
// from create on, so the scene is never bound to another device than the one its QRhi uses.
//
// TODO: Task 1.3 (bind): add the binding here: the opened ID3D11Texture2D (OpenSharedResource1 on the device of
// nativeDevice(window->rhi())), the shared write and read fences with their last values, the render count, and
// window->setRenderTarget(QQuickRenderTarget::fromD3D11Texture(texture, rtvFormat, size)) with setDevicePixelRatio(1)
// (1 DIP = 1 texture pixel, also on a high-DPI screen); window->setGeometry(0, 0, width, height) takes the texture
// size; dirty = true so the next batch renders the new target. release() calls the same unbind (render target ->
// opened texture -> fences), while the QRhi still exists.
//
// TODO: Task 1.4 (render_batch): per bound scene with dirty set: clear dirty first (a change made while rendering
// renders next frame), then
//   renderControl->polishItems(); renderControl->beginFrame(); renderControl->sync(); renderControl->render();
//   renderControl->endFrame();
// QRhi D3D11 submits the recorded commands in endFrame, so Wait(readFence) on QtNative's context goes before
// beginFrame and Signal(writeFence) + Flush after endFrame. The perf monitor sees beforeSynchronizing,
// afterSynchronizing, beforeRendering and afterRendering as in a window; frameSwapped is not emitted and the GPU time
// comes from renderControl->commandBuffer() instead of the swap chain (perf.cpp afterRendering).
//
// TODO: Task 1.5 (device loss): device::rebuild finds the scene's QQuickWindow among the top-level windows and hands
// it the new device; a render control needs renderControl->invalidate() before and initialize() after that, and the
// binding is dropped (is_bound 0, the caller binds again).
struct State
{
    QQuickRenderControl* renderControl = nullptr;
    // The scene wants a frame: sceneChanged (items changed: polish, sync, render) or renderRequested (render only,
    // e.g. QQuickWindow::update() in vlqt_window_apply). Starts set; render_batch (1.4) renders a bound scene only
    // while it is set, so an unchanged scene costs nothing.
    bool dirty = true;
};

namespace {

void* create(int width, int height)
{
    if (!qGuiApp) {
        setError("offscreen scene: app not initialized");
        return nullptr;
    }
    // The fences and the opened texture (bind) are D3D11 objects on QtNative's shared device.
    if (QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11) {
        setError("offscreen scene: needs Direct3D 11 (Windows)");
        return nullptr;
    }
    auto* state = new State();
    state->renderControl = new QQuickRenderControl();
    auto* window = new QQuickWindow(state->renderControl);
    // 1 DIP = 1 texture pixel; the size until the first bind, which sets the texture's.
    window->setGeometry(0, 0, std::max(width, 1), std::max(height, 1));
    void* handle = createHandle(window, state);
    // device::attach marks a window that got the shared device; without it initialize would create a device of its own.
    if (!window->property("_vlqt_shared_device").toBool()) {
        vlqt_window_destroy(handle);
        setError("offscreen scene: QtNative has no shared D3D11 device (VLQT_SHARED_DEVICE=0 or creation failed, see the log)");
        return nullptr;
    }
    // The QRhi now, not at bind: vlqt_test_device_info, snapshots and resources see the scene's device at once.
    if (!state->renderControl->initialize()) {
        vlqt_window_destroy(handle);
        setError("offscreen scene: QQuickRenderControl::initialize failed (see the log)");
        return nullptr;
    }
    QObject::connect(state->renderControl, &QQuickRenderControl::sceneChanged, state->renderControl,
        [state] { state->dirty = true; });
    QObject::connect(state->renderControl, &QQuickRenderControl::renderRequested, state->renderControl,
        [state] { state->dirty = true; });
    return handle;
}

} // namespace

void release(State* state)
{
    // TODO: Task 1.3: unbind (render target -> opened texture -> fences).
    (void)state;
}

void destroy(State* state)
{
    delete state->renderControl;
    delete state;
}

int testState(void* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders)
{
    (void)readFence;
    (void)writeFence;
    (void)renders;
    if (!stateOf(scene))
        return VLQT_OFFSCREEN_NULL_SCENE;
    // TODO: Task 1.3/1.4: fence values and render count of a bound scene.
    return VLQT_OFFSCREEN_UNBOUND;
}

} // namespace vlqt::offscreen

using namespace vlqt::offscreen;

extern "C" {

VLQT_API void* vlqt_offscreen_create(int width, int height)
{
    try {
        return create(width, height);
    } catch (const std::exception& e) {
        setError(std::string("exception: ") + e.what());
    } catch (...) {
        setError("unknown exception");
    }
    return nullptr;
}

VLQT_API int vlqt_offscreen_bind(void* scene, void* device, void* ntHandle, int width, int height, int rtvFormat)
{
    (void)device;
    (void)ntHandle;
    (void)width;
    (void)height;
    (void)rtvFormat;
    if (!stateOf(scene))
        return VLQT_OFFSCREEN_NULL_SCENE;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_offscreen_unbind(void* scene)
{
    State* state = stateOf(scene);
    if (!state)
        return VLQT_OFFSCREEN_NULL_SCENE;
    release(state);
    return VLQT_OFFSCREEN_OK;
}

VLQT_API int vlqt_offscreen_is_bound(void* scene)
{
    if (!stateOf(scene))
        return VLQT_OFFSCREEN_NULL_SCENE;
    return 0; // TODO: Task 1.3: 1 while a texture is bound.
}

VLQT_API int vlqt_offscreen_render_batch(void* context, void* const* scenes, int count, double driverSeconds)
{
    (void)driverSeconds;
    if (!context || count < 0 || (!scenes && count > 0))
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

} // extern "C"
