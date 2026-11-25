// The offscreen scene: what a scene handle of an offscreen scene holds besides the parts every handle shares
// (window/scene_handle.h), and what the files of offscreen/ share. Internal to offscreen/; the rest of QtNative uses
// offscreen.h.
#pragma once

#include "offscreen/offscreen.h"
#include "device/render_thread.h"

#include <QtCore/QElapsedTimer>
#include <QtCore/QLoggingCategory>
#include <QtCore/QPointF>
#include <QtCore/qnamespace.h>
#include <QtGui/QEventPoint>

#include <atomic>
#include <utility>
#include <vector>

#ifdef Q_OS_WIN
#include "offscreen/linear_pass.h"

#include <d3d11_4.h>
#include <wrl/client.h>
#endif

class QPointingDevice;
class QQuickRenderControl;
class QQuickWindow;
struct vlqt_scene;

namespace vlqt {

// An offscreen scene from create to destroy: its binding (the foreign texture opened on QtNative's device, two shared
// fences, QtNative's context), its executor (a render thread or the main thread) and its input. render_batch renders it
// through the executor. Lifetime, device rebuild and threads: docs/design/render-thread.md, "Lifetime of a scene".
struct OffscreenScene
{
    QQuickRenderControl* renderControl = nullptr;
    QQuickWindow* window = nullptr; // the scene's window, owned by the handle (window/scene_handle.h)
    // The scene wants a frame: sceneChanged (items changed: polish, sync, render) or renderRequested (render only,
    // e.g. QQuickWindow::update() in vlqt_scene_apply); bind sets it too. render_batch renders a bound scene only
    // while it is set, so an unchanged scene costs nothing.
    bool dirty = true;
    // The last render_batch polished the scene and nothing has changed its items since (apply, bind, input): the poll then
    // needs no polish of its own (polishedSinceLastPoll).
    bool polished = false;
    bool bound = false;
    unsigned long long readValue = 0;  // last value signalled on the read fence
    unsigned long long writeValue = 0; // last value signalled on the write fence
    std::atomic<long long> renders = 0; // renders into the texture since bind (the render thread counts them)
    render_thread::Executor executor; // where its scene graph runs: a render thread, or inline on the main thread
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
#ifdef Q_OS_WIN
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture; // the foreign texture, opened on QtNative's device
    // The device passed to bind, to compare only (render_batch: the context it gets must be of this device); no
    // reference, the fences opened on it keep it alive.
    ID3D11Device* foreignDevice = nullptr;
    // A consumer that samples linear values (SRGB view, float): Qt renders into the pass's source, the pass writes the
    // texture after each frame. Empty for a UNORM view: Qt renders into the texture itself.
    vlqt::linear_pass::Pass linearPass;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext4> qtContext; // immediate context of QtNative's device
    Microsoft::WRL::ComPtr<ID3D11Fence> qtRead;             // the fences on QtNative's device ...
    Microsoft::WRL::ComPtr<ID3D11Fence> qtWrite;
    Microsoft::WRL::ComPtr<ID3D11Fence> foreignRead;        // ... and opened on the foreign device
    Microsoft::WRL::ComPtr<ID3D11Fence> foreignWrite;
    // A device of the binding is a software adapter (WARP): its context deadlocks waiting for a value that is signalled
    // only later, so render_batch waits until the render thread submitted the signal.
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

#ifdef Q_OS_WIN

// --- offscreen_binding.cpp ---

// vlqt::fail with the HRESULT appended; the message starts with "offscreen: ".
int fail(int code, const char* what, HRESULT hr = S_OK);
// QtNative's device of the scene's QRhi; nullptr without one.
ID3D11Device* qtDevice(const OffscreenScene* s);
// Frees the binding: render target, opened texture, fences. While the QRhi still exists (before the QQuickWindow is
// deleted). Through the scene's executor (executor.call).
void unbind(OffscreenScene* s);
// Main thread; unbinds first and takes the new binding only after every check, so a failure leaves the scene unbound.
int bind(OffscreenScene* s, void* device, void* ntHandle, int width, int height, int format, int samples, int* usedSamples);

// --- offscreen_render.cpp ---

// vlqt_offscreen_render_batch: renders every bound, dirty scene of scenes.
int renderBatch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds);

#endif

} // namespace vlqt::offscreen
