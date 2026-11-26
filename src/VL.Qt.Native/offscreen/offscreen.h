// offscreen.h - offscreen scenes (QtTexture): a scene handle whose QQuickWindow a QQuickRenderControl drives, rendering
// into a texture of a foreign device. This part owns the render control and the binding; window/window.cpp creates what
// every handle shares. Threads: main; a scene's scene graph and QRhi live on its render thread (device/render_thread.h).
// C ABI: the vlqt_offscreen_* exports in vlqt.h. Design: docs/design/render-thread.md, docs/design/stride-output.md.
#pragma once

#include <QtCore/QPointF>
#include <QtGui/QEventPoint>

#include <utility>
#include <vector>

class QPointingDevice;
class QQuickWindow;
struct vlqt_scene;

namespace vlqt {

// The offscreen part of a scene handle (offscreen_scene.h).
struct OffscreenScene;

} // namespace vlqt

namespace vlqt::render_thread {
class RenderThread;
}

namespace vlqt::offscreen {

// --- window/: window.cpp (createHandle, stateOf), focus_release.cpp, touch_input.cpp ---

// Window handle of a new scene around window, a QQuickWindow of state's render control: QML engine, scene (apply,
// poll), change count, perf monitor and graphics configuration on the shared device (device::attach), the
// FocusRelease filter and no other input filter. The handle owns window and state from here on;
// vlqt_scene_destroy frees them.
vlqt_scene* createHandle(QQuickWindow* window, OffscreenScene* state);
// The offscreen part of handle; nullptr for a null handle or a window.
OffscreenScene* stateOf(vlqt_scene* handle);
// Clears the focus of the outermost focused item below window's root, so the active focus goes to the root: a running
// edit commits as on any focus loss. Shared by the FocusRelease filter and the scene's focus loss.
void releaseFocus(QQuickWindow* window);
// Injected touch, shared with vlqt_test_send_touch: sends the complete touch state of window after point id of device
// changed to state (Pressed, Updated or Released) at pos (window DIP), like a touchscreen driver: one QTouchEvent
// (Begin/Update/End) with every point of active, the others stationary, via QCoreApplication::sendEvent. Updates
// active (id, position) and the device's point registration. False with vlqt_last_error ("<function>: ...") if id is
// pressed twice or is not pressed.
bool sendTouch(QQuickWindow* window, const QPointingDevice* device, std::vector<std::pair<int, QPointF>>& active,
    QEventPoint::State state, int id, QPointF pos, ulong timestamp, Qt::KeyboardModifiers modifiers, const char* function);
// TouchCancel for every point of active (nothing if empty); the device forgets them, active is emptied.
void cancelTouch(QQuickWindow* window, const QPointingDevice* device, std::vector<std::pair<int, QPointF>>& active,
    ulong timestamp, Qt::KeyboardModifiers modifiers);

// --- offscreen_scene.cpp, called by vlqt_scene_destroy ---

// Before the QQuickWindow is deleted: frees the binding (render target, opened texture, fences); a scene on a render
// thread also drops its scene graph and QRhi there (after every render posted before).
void release(OffscreenScene* state);
// After the QQuickWindow is deleted: deletes the render control (and with it the scene's QRhi) and state.
void destroy(OffscreenScene* state);

// --- offscreen_scene.cpp, for the poll (window/window_exports.cpp) ---

// True if the last render_batch polished scene and nothing changed its items since (commands, bind, input); clears the
// answer, so the next poll decides by itself. A scene has no render gate: the poll polishes it only when this is false.
bool polishedSinceLastPoll(OffscreenScene* scene);
// A change the batch's polish has not seen (vlqt_scene_apply): the next poll polishes by itself.
void invalidatePolish(OffscreenScene* scene);

// --- offscreen_scene.cpp, for snapshots (snapshot_resource.cpp) ---

// True if window is the QQuickWindow of an offscreen scene (never exposed: a snapshot asks for a render instead).
bool isScene(const QQuickWindow* window);
// A grab of an element of window's scene finishes in the scene's next render: marks a bound scene dirty, so the next
// render_batch renders it also when nothing changed. Returns false if the scene is unbound (it does not render).
bool requestFrame(const QQuickWindow* window);

// --- offscreen_scene.cpp, for render_thread::retire ---

// The render thread of window's scene; nullptr for a scene on the main thread and for any other window. Main thread.
render_thread::RenderThread* renderThreadOf(const QQuickWindow* window);

// test hook: backend of vlqt_test_offscreen_state (test/test_probes.cpp): fence values and render count of a bound scene.
// Out pointers may be null. Returns VLQT_OK, VLQT_ERR_NULL_HANDLE or VLQT_ERR_UNBOUND (vlqt_last_error set).
int testState(vlqt_scene* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders);

} // namespace vlqt::offscreen
