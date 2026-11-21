// The scene handle behind every vlqt_scene* of the C ABI: a window, or an offscreen scene. Qt GUI thread.
#pragma once

#include "test/test_hooks.h"

#include <QtCore/QPoint>
#include <QtCore/QSize>
#include <QtCore/qnamespace.h>

class QQmlEngine;
class QQuickWindow;
struct vlqt_scene;

namespace vlqt {

class Scene;
class CloseFilter;
class PerfMonitor;
class UpdateGate;

namespace offscreen {
struct State;
}

// A window handle (void* of the vlqt_window_* exports): a window, or an offscreen scene (offscreen.cpp) whose
// QQuickWindow is driven by a QQuickRenderControl. Both share engine, scene, gate and perf; a scene has only the
// FocusRelease input filter (closeFilter etc. stay null) and its own part in offscreen.
struct SceneHandle {
    QQuickWindow* window = nullptr;
    vlqt::offscreen::State* offscreen = nullptr; // offscreen scene: render control and binding; null for a window
    CloseFilter* closeFilter = nullptr;
    QQmlEngine* engine = nullptr;
    vlqt::Scene* scene = nullptr; // apply/poll
    UpdateGate* gate = nullptr;
    PerfMonitor* perf = nullptr;
    vlqt::test::InputState testInput;            // test hook: synthetic input state (test/test_input.cpp)
    unsigned mode = 0;                           // vlqt_window_set_mode: requested vlqt_window_mode bits
    // geometry before fullscreen (DIP, frame position like vlqt_window_create), restored when leaving it
    bool fsSaved = false;
    QPoint fsFramePos;
    QSize fsSize;
    Qt::WindowStates fsStates;
};

// The C handle: a vlqt_scene* points to a SceneHandle.
inline SceneHandle* asWindow(vlqt_scene* handle)
{
    return reinterpret_cast<SceneHandle*>(handle);
}

inline vlqt_scene* asHandle(SceneHandle* window)
{
    return reinterpret_cast<vlqt_scene*>(window);
}

} // namespace vlqt
