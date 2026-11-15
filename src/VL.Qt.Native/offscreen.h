// Offscreen scenes (#116, QtTexture): a Qt scene without a native window (QQuickRenderControl) on the shared D3D11
// device, rendering into a texture of a foreign device (Stride's), ordered by two shared fences. C ABI: the
// vlqt_offscreen_* exports in vlqt.h; design: docs/decisions/render-texture.md. Main thread only.
//
// A scene is a window handle (struct Window of vlqt.cpp) whose QQuickWindow is driven by a QQuickRenderControl:
// vlqt.cpp owns the parts every handle shares (engine, scene, gate, perf, graphics configuration), this part owns the
// render control and the binding.
#pragma once

#include <QtCore/QPointF>
#include <QtGui/QEventPoint>

#include <string>
#include <utility>
#include <vector>

class QPointingDevice;
class QQuickWindow;

namespace vlqt::offscreen {

// The offscreen part of a window handle (offscreen.cpp).
struct State;

// --- vlqt.cpp ---

// Window handle of a new scene around window, a QQuickWindow of state's render control: QML engine, scene (apply,
// poll), change count, perf monitor and graphics configuration on the shared device (device::attach), no input
// filters. The handle owns window and state from here on; vlqt_window_destroy frees them.
void* createHandle(QQuickWindow* window, State* state);
// The offscreen part of handle; nullptr for a null handle or a window.
State* stateOf(void* handle);
QQuickWindow* quickWindow(void* handle);
// vlqt_last_error.
void setError(const std::string& message);
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

// --- offscreen.cpp, called by vlqt_window_destroy ---

// Before the QQuickWindow is deleted: frees the binding (render target, opened texture, fences).
void release(State* state);
// After the QQuickWindow is deleted: deletes the render control (and with it the scene's QRhi) and state.
void destroy(State* state);

// --- offscreen.cpp, for snapshots (snapshot_resource.cpp) ---

// True if window is the QQuickWindow of an offscreen scene (never exposed: a snapshot asks for a render instead).
bool isScene(const QQuickWindow* window);
// A grab of an element of window's scene finishes in the scene's next render: marks a bound scene dirty, so the next
// render_batch renders it also when nothing changed. Returns false if the scene is unbound (it does not render).
bool requestFrame(const QQuickWindow* window);

// Backend of vlqt_test_offscreen_state (test_offscreen.cpp): fence values and render count of a bound scene.
// Out pointers may be null. Returns a vlqt_offscreen_result code (protocol.h).
int testState(void* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders);

} // namespace vlqt::offscreen
