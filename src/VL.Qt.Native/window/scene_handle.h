// The scene handle behind every vlqt_scene* of the C ABI: a window, or an offscreen scene. Qt GUI thread.
#pragma once

#include <QtCore/QPoint>
#include <QtCore/QPointer>
#include <QtCore/QSize>
#include <QtCore/qnamespace.h>

#include <memory>
#include <optional>

class QQmlEngine;
class QQuickWindow;
struct vlqt_scene;

namespace vlqt {

class Scene;
class CloseFilter;
class PerfMonitor;
class RenderGate;
class SceneChangeCounter;

struct OffscreenScene;

namespace test {
struct TestInput;
}

// Deletes the offscreen part of a handle (offscreen::destroy): the render control and with it the scene's QRhi.
struct OffscreenSceneDeleter {
    void operator()(OffscreenScene* scene) const;
};

// Geometry of a window before fullscreen (DIP, frame position like vlqt_window_create), restored when leaving it.
struct FullscreenRestore {
    QPoint framePos;
    QSize size;
    Qt::WindowStates states;
};

// What a vlqt_scene* points to: a window, or an offscreen scene (offscreen/offscreen_scene.h) whose QQuickWindow is
// driven by a QQuickRenderControl. Both own engine, window, scene and perf and have a change counter; a scene has
// only the FocusRelease input filter (closeFilter and gate stay null) and its own part in offscreen. Created by
// vlqt_window_create or offscreen::createHandle, deleted by vlqt_scene_destroy.
//
// Destruction order: the destructor first releases the binding of an offscreen scene (offscreen::release), whose render
// target and opened texture belong to the QRhi of its render control. The members then go in reverse declaration
// order: perf before scene (the overlay's Scene page reads it), scene before window (its items are in the window),
// window before engine (the engine created the QML objects in it), the render control of offscreen after the window.
// closeFilter, gate and changes are children of the window and die with it.
struct SceneHandle {
    // Both in window/window.cpp, where the owned types are complete.
    SceneHandle();
    ~SceneHandle();
    SceneHandle(const SceneHandle&) = delete;
    SceneHandle& operator=(const SceneHandle&) = delete;

    bool isOffscreen() const { return offscreen != nullptr; }

    std::unique_ptr<OffscreenScene, OffscreenSceneDeleter> offscreen; // render control and binding; null for a window
    std::unique_ptr<QQmlEngine> engine;
    std::unique_ptr<QQuickWindow> window;
    std::unique_ptr<Scene> scene;      // apply/poll
    std::unique_ptr<PerfMonitor> perf; // child of window, deleted before it
    QPointer<CloseFilter> closeFilter; // child of window; null for an offscreen scene
    QPointer<RenderGate> gate;         // child of window; null for an offscreen scene
    QPointer<SceneChangeCounter> changes; // child of window
    std::unique_ptr<test::TestInput> testInput; // test hook: synthetic input state, created by test/test_input.cpp
    unsigned mode = 0;                          // vlqt_window_set_mode: requested vlqt_window_mode bits
    std::optional<FullscreenRestore> fullscreenRestore; // set while fullscreen
};

// The C handle: a vlqt_scene* points to a SceneHandle, of a window or of an offscreen scene. Exports reach it through
// withScene / withWindow / withOffscreen (app/exports.h), which check it and the thread.
inline SceneHandle* asSceneHandle(vlqt_scene* handle)
{
    return reinterpret_cast<SceneHandle*>(handle);
}

inline vlqt_scene* asHandle(SceneHandle* scene)
{
    return reinterpret_cast<vlqt_scene*>(scene);
}

} // namespace vlqt
