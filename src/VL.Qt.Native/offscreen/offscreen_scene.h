// The offscreen scene: what a scene handle of an offscreen scene holds besides the parts every handle shares
// (window/scene_handle.h), and what the files of offscreen/ share. Internal to offscreen/; the rest of QtNative uses
// offscreen.h.
#pragma once

#include "offscreen/offscreen.h"
#include "render_thread.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtCore/QPointF>
#include <QtCore/qnamespace.h>
#include <QtGui/QEventPoint>

#include <atomic>
#include <utility>
#include <vector>

#ifdef _WIN32
#include "offscreen/linear_pass.h"

#include <d3d11_4.h>
#endif

class QPointingDevice;
class QQuickRenderControl;
class QQuickWindow;
struct vlqt_scene;

namespace vlqt {

// Lifetime: vlqt_offscreen_create -> (bind -> render_batch ... -> unbind)* -> vlqt_scene_destroy (release, then
// destroy after the QQuickWindow). The QRhi of the scene belongs to renderControl and lives on QtNative's shared device
// from create on, so the scene is never bound to another device than the one its QRhi uses.
//
// A binding is the texture opened on QtNative's device (Qt's render target), QtNative's immediate context and two
// shared fences, each as created on QtNative's device and as opened on the foreign one. render_batch orders the GPUs
// with them: readFence = the foreign device is done reading the texture (Qt may write), writeFence = Qt is done
// writing (the foreign device may read). The values count up from 0 per binding.
//
// Device rebuild (loss, or a move to the foreign adapter in bind): a device::Listener unbinds every scene and drops its
// scene graph and QRhi (lost); device::rebuild hands the scene's window, which then has no QRhi, the new device like
// any window not yet exposed; restored initializes the render control again. The caller sees is_bound 0 and binds again.
//
// Foreign device: QtNative holds no reference to it of its own; the two fences opened on it do, until unbind. So a
// bound scene keeps it alive after its owner released it, and unbind or destroy then frees it.
//
// Render thread (render-texture.md "Qt render thread"): a scene of a threaded process gets a render thread at create
// (render_thread::assign) and its scene graph and QRhi live there: initialize, beginFrame, sync, render, endFrame,
// invalidate and unbind run there, the main thread waits for each of them except render + endFrame + Signal + Flush
// (posted by render_batch). Items, polish, input, apply and bind stay on the main thread; the render thread touches
// items only in sync, while the main thread waits. Every blocking job runs after the jobs posted before it, so unbind,
// destroy, device loss and adapter move drain the thread first. Without a render thread
// (vlqt_offscreen_set_render_threads(0), VLQT_OFFSCREEN_THREAD=0) everything runs on the main thread as before.
struct OffscreenScene
{
    QQuickRenderControl* renderControl = nullptr;
    QQuickWindow* window = nullptr; // the scene's window, owned by the handle (window/scene_handle.h)
    // The scene wants a frame: sceneChanged (items changed: polish, sync, render) or renderRequested (render only,
    // e.g. QQuickWindow::update() in vlqt_scene_apply); bind sets it too. render_batch renders a bound scene only
    // while it is set, so an unchanged scene costs nothing.
    bool dirty = true;
    bool bound = false;
    unsigned long long readValue = 0;  // last value signalled on the read fence
    unsigned long long writeValue = 0; // last value signalled on the write fence
    std::atomic<long long> renders = 0; // renders into the texture since bind (the render thread counts them)
    render_thread::RenderThread* thread = nullptr; // the scene's render thread; null = main thread
    // Input. Devices of their own per scene, children of renderControl, created at the first event: point ids
    // and grabs live in the device, so two scenes fed the same host input (a press lands in both) must not share one.
    QPointingDevice* mouse = nullptr;
    QPointingDevice* touchscreen = nullptr;
    QPointingDevice* pen = nullptr; // a stylus, as Qt's Windows plugin reports a pen (WM_POINTER)
    // The scene has the keyboard focus (FocusIn sent): a scene window never becomes QGuiApplication's focus window.
    bool focused = false;
    Qt::MouseButtons buttons = Qt::NoButton;          // held after the last mouse event
    std::vector<std::pair<int, QPointF>> touchPoints; // active points (id, DIP) of touchscreen
    bool penDown = false;                             // the pen touches (between PRESS and RELEASE)
    QPointF penPos;                                   // its last position
    QElapsedTimer clock;                              // event timestamps
    // The last press that may start a double click (QGuiApplication's rule); button NoButton after a double click.
    ulong lastPressTime = 0;
    QPointF lastPressPos;
    Qt::MouseButton lastPressButton = Qt::NoButton;
#ifdef _WIN32
    ID3D11Texture2D* texture = nullptr;        // the foreign texture, opened on QtNative's device
    // A consumer that samples linear values (SRGB view, float): Qt renders into the pass's source, the pass writes the
    // texture after each frame. Empty for a UNORM view: Qt renders into the texture itself.
    vlqt::linear_pass::Pass linearPass;
    ID3D11DeviceContext4* qtContext = nullptr; // immediate context of QtNative's device
    ID3D11Fence* qtRead = nullptr;             // the fences on QtNative's device ...
    ID3D11Fence* qtWrite = nullptr;
    ID3D11Fence* foreignRead = nullptr;        // ... and opened on the foreign device
    ID3D11Fence* foreignWrite = nullptr;
    // A device of the binding is a software adapter (WARP): its context deadlocks waiting for a value that is signalled
    // only later (spike 5.1), so render_batch waits until the render thread submitted the signal.
    bool software = false;
#endif
};
} // namespace vlqt

namespace vlqt::offscreen {

Q_DECLARE_LOGGING_CATEGORY(lcOffscreen)

// Every scene between create and destroy, for the device listener. Main thread.
const std::vector<OffscreenScene*>& scenes();

// The scene whose QQuickWindow is window; nullptr for any other window.
OffscreenScene* sceneOf(const QQuickWindow* window);

// f on the scene's render thread, blocking and after every job posted before it (drains it); here without one.
template <class F> void onRenderThread(OffscreenScene* s, F&& f)
{
    if (s->thread)
        s->thread->call(std::forward<F>(f));
    else
        f();
}

// --- offscreen_scene.cpp ---

// A new scene on a handle of its own; nullptr with vlqt_last_error set (vlqt_offscreen_create).
vlqt_scene* create(int width, int height);

// --- offscreen_input.cpp ---

// The render control of scene: Qt's, plus the keyboard focus of a scene (SceneRenderControlPrivate).
QQuickRenderControl* newRenderControl(const OffscreenScene* scene);

// The input exports (vlqt.h, render-texture.md "Input"): events go with QCoreApplication::sendEvent to the scene's
// QQuickWindow. VLQT_OK, or an error code with vlqt_last_error set.
int sendPointer(OffscreenScene* s, int device, int type, int id, QPointF pos, int buttons, int modifiers);
int sendWheel(OffscreenScene* s, QPointF pos, int dx, int dy, int modifiers);
int sendKey(OffscreenScene* s, int type, int key, int modifiers, bool autoRepeat);
int sendText(OffscreenScene* s, const char* utf8, int length);
void setFocus(OffscreenScene* s, bool focused, Qt::FocusReason reason);

#ifdef _WIN32

// --- offscreen_binding.cpp ---

// vlqt::fail with the HRESULT appended; the message starts with "offscreen: ".
int fail(int code, const char* what, HRESULT hr = S_OK);
// QtNative's device of the scene's QRhi; nullptr without one.
ID3D11Device* qtDevice(const OffscreenScene* s);
// Frees the binding: render target, opened texture, fences. While the QRhi still exists (before the QQuickWindow is
// deleted). On the scene's render thread (onRenderThread).
void unbind(OffscreenScene* s);
// Main thread; unbinds first and takes the new binding only after every check, so a failure leaves the scene unbound.
int bind(OffscreenScene* s, void* device, void* ntHandle, int width, int height, int format, int samples, int* usedSamples);

// --- offscreen_render.cpp ---

// vlqt_offscreen_render_batch: renders every bound, dirty scene of scenes.
int renderBatch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds);

#endif

} // namespace vlqt::offscreen
