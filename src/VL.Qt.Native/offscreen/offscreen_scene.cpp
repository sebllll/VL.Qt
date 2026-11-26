// Private Qt API: QQuickRenderControlPrivate::resetRhi, QQuickWindowPrivate (dropRhi); check on Qt updates (list: elements/elements.h).
#include "offscreen/offscreen_scene.h"
#include "app/exports.h"
#include "device/device.h"
#include "vlqt.h"

#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickRenderControl>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>

#include <algorithm>
#include <utility>

#ifdef Q_OS_WIN
// dropRhi: QQuickRenderControlPrivate and QQuickWindowPrivate. Private API, check on Qt updates.
#include <QtQuick/private/qquickrendercontrol_p.h>
#include <QtQuick/private/qquickwindow_p.h>
#endif

namespace vlqt::offscreen {

Q_LOGGING_CATEGORY(lcOffscreen, "vlqt.offscreen")

namespace {

std::vector<OffscreenScene*> s_scenes;

#ifdef Q_OS_WIN
// Drops scene graph and QRhi as ~QQuickRenderControl does: initialize() reuses an existing QRhi, so a new one (on a
// new device) needs resetRhi. The window has no QRhi afterwards. On the scene's render thread.
void dropRhi(OffscreenScene* s)
{
    s->renderControl->invalidate();
    QQuickWindowPrivate* wd = QQuickWindowPrivate::get(s->window);
    wd->rhi = nullptr;
    QQuickRenderControlPrivate::get(s->renderControl)->resetRhi(wd->graphicsConfig);
}

// device::Listener lost: the old device goes away. Unbind (render target, opened texture, fences on both sides), then
// drop scene graph and QRhi; device::rebuild hands the window the new device.
void deviceLost()
{
    for (OffscreenScene* s : s_scenes) {
        const bool wasBound = s->bound;
        s->executor.call([s] {
            unbind(s);
            dropRhi(s);
        });
        s->dirty = true;
        if (wasBound)
            qCInfo(lcOffscreen, "offscreen scene %p unbound: QtNative's device is rebuilt, the caller binds again",
                static_cast<void*>(s));
    }
}

// device::Listener restored: a QRhi on the new device (the window got it from device::rebuild).
void deviceRestored()
{
    for (OffscreenScene* s : s_scenes) {
        // No shared device (creation failed, device.h): initialize would create a device of its own. bind reports
        // DEVICE_LOST until the retry hands the window the new device and calls this again.
        if (!vlqt::device::isOnSharedDevice(s->window))
            continue;
        bool initialized = false;
        s->executor.call([s, &initialized] { initialized = s->renderControl->initialize(); });
        if (!initialized)
            qCWarning(lcOffscreen, "offscreen scene %p: QQuickRenderControl::initialize failed after the device "
                "rebuild, bind retries it", static_cast<void*>(s));
    }
}
#endif

} // namespace

const std::vector<OffscreenScene*>& scenes()
{
    return s_scenes;
}

vlqt_scene* create(int width, int height)
{
    if (!qGuiApp) {
        vlqt::setLastError("offscreen scene: app not initialized");
        return nullptr;
    }
    // The fences and the opened texture (bind) are D3D11 objects on QtNative's shared device.
    if (QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11) {
        vlqt::setLastError("offscreen scene: needs Direct3D 11 (Windows)");
        return nullptr;
    }
    auto* state = new OffscreenScene();
    state->renderControl = newRenderControl(state);
    auto* window = new QQuickWindow(state->renderControl);
    state->window = window;
    // 1 DIP = 1 texture pixel; the size until the first bind, which sets the texture's.
    window->setGeometry(0, 0, std::max(width, 1), std::max(height, 1));
    vlqt_scene* handle = createHandle(window, state);
    // device::attach marks a window that got the shared device; without it initialize would create a device of its own.
    if (!vlqt::device::isOnSharedDevice(window)) {
        vlqt_scene_destroy(handle);
        vlqt::setLastError("offscreen scene: QtNative has no shared D3D11 device (VLQT_SHARED_DEVICE=0, or creation failed and is retried, see the log)");
        return nullptr;
    }
    // From here on the scene graph and the QRhi belong to the executor's thread (prepareThread before initialize).
    state->executor = render_thread::assign();
    if (render_thread::RenderThread* t = state->executor.thread())
        state->renderControl->prepareThread(t->thread());
    // The QRhi now, not at bind: vlqt_test_device_info, snapshots and resources see the scene's device at once.
    bool initialized = false;
    state->executor.call([state, &initialized] { initialized = state->renderControl->initialize(); });
    if (!initialized) {
        vlqt_scene_destroy(handle);
        vlqt::setLastError("offscreen scene: QQuickRenderControl::initialize failed (see the log)");
        return nullptr;
    }
    QObject::connect(state->renderControl, &QQuickRenderControl::sceneChanged, state->renderControl,
        [state] { state->dirty = true; });
    QObject::connect(state->renderControl, &QQuickRenderControl::renderRequested, state->renderControl,
        [state] { state->dirty = true; });
#ifdef Q_OS_WIN
    static const bool listening = [] {
        vlqt::device::addListener({ deviceLost, deviceRestored });
        return true;
    }();
    (void)listening;
#endif
    s_scenes.push_back(state);
    return handle;
}

void release(OffscreenScene* state)
{
#ifdef Q_OS_WIN
    state->executor.call([state] {
        unbind(state);
        // The render thread's scene graph and QRhi go there, before the main thread deletes window and render control.
        if (state->executor.thread())
            dropRhi(state);
    });
#else
    (void)state;
#endif
}

void destroy(OffscreenScene* state)
{
    s_scenes.erase(std::remove(s_scenes.begin(), s_scenes.end(), state), s_scenes.end());
    delete state->renderControl;
    delete state;
}

OffscreenScene* sceneOf(const QQuickWindow* window)
{
    const auto it = std::find_if(s_scenes.begin(), s_scenes.end(), [window](const OffscreenScene* s) { return s->window == window; });
    return it != s_scenes.end() ? *it : nullptr;
}

bool polishedSinceLastPoll(OffscreenScene* scene)
{
    return std::exchange(scene->polished, false);
}

void invalidatePolish(OffscreenScene* scene)
{
    scene->polished = false;
}

bool isScene(const QQuickWindow* window)
{
    return window && sceneOf(window);
}

render_thread::RenderThread* renderThreadOf(const QQuickWindow* window)
{
    const OffscreenScene* s = window ? sceneOf(window) : nullptr;
    return s ? s->executor.thread() : nullptr;
}

bool requestFrame(const QQuickWindow* window)
{
    OffscreenScene* s = sceneOf(window);
    if (!s || !s->bound)
        return false;
    s->dirty = true;
    return true;
}

// test hook: vlqt_test_offscreen_state (test/test_probes.cpp).
int testState(vlqt_scene* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders)
{
    OffscreenScene* s = stateOf(scene);
    if (!s)
        return vlqt::fail(VLQT_ERR_NULL_HANDLE, "test_offscreen_state: null handle or not an offscreen scene");
    // The renders posted by the last batch are counted.
    s->executor.drain();
    if (!s->bound)
        return vlqt::fail(VLQT_ERR_UNBOUND, "test_offscreen_state: the scene is not bound");
    if (readFence)
        *readFence = s->readValue;
    if (writeFence)
        *writeFence = s->writeValue;
    if (renders)
        *renders = s->renders;
    return VLQT_OK;
}

} // namespace vlqt::offscreen
