// The change count of a scene (vlqt_scene_get_change_count): frames whose render synced a change of the scene, for
// windows and offscreen scenes. Private Qt API in scene_change.cpp: the dirty item list of a window
// (list: elements/elements.h).
#pragma once

#include <QtCore/QObject>

#include <vector>

class QQuickItem;
class QQuickWindow;

namespace vlqt {

// objectName of the overlay items directly under the content item (the perf overlay of diagnostics/perf_monitor.cpp,
// the debug overlay of diagnostics/debug_overlay.cpp): they draw on top of the scene and are no part of it, so their
// repaints are no scene change.
inline constexpr char overlayObjectName[] = "vlqt-overlay";

// Created and read on the Qt GUI thread. The sync hook runs where Qt syncs the window: on the GUI thread for a window
// (basic loop) and for an offscreen scene in the main thread mode, on the scene's render thread in the threaded mode.
// There the GUI thread waits in the blocking sync job of the batch (offscreen/offscreen_render.cpp), so the hook never
// runs at the same time as count() or markChanged(); syncs of scenes on different render threads may run at the same
// time, so a counter keeps no state shared with another one.
class SceneChangeCounter : public QObject
{
public:
    // Child of the window.
    explicit SceneChangeCounter(QQuickWindow* window);

    // Frames with a change of the scene: counted where Qt syncs a render, so a static scene keeps the count however
    // often the pump runs or Expose re-renders it.
    long long count() const { return m_count; }
    // The scene changed without a dirty item (e.g. the window color): count the next render.
    void markChanged() { m_changed = true; }
    // Window swap: the counter of the new window goes on with the count of the old one, so the count never goes back;
    // the first render of the new window counts as a change (other sample count).
    void continueFrom(const SceneChangeCounter& old)
    {
        m_count = old.m_count;
        m_changed = true;
    }

    // Returns true if an item outside the overlays waits for its sync (a property change, a polish result, a resize).
    // Between polish and sync (afterAnimating); GUI thread.
    static bool itemsDirty(QQuickWindow* window);

private:
    bool sceneDirty();
    bool isGrabSource(QQuickItem* i) const;
    static bool inOverlay(QQuickItem* i, const QQuickItem* content);
    static bool isOverlay(const QQuickItem* i);
    static bool hasRefChild(QQuickItem* parent);
    bool contentChildrenChanged();

    QQuickWindow* m_window;
    long long m_count = 0;
    bool m_changed = false;
    std::vector<QQuickItem*> m_contentChildren; // the content item's children at the last render, without frame sources
    std::vector<QQuickItem*> m_children;        // scratch of contentChildrenChanged, swapped with m_contentChildren
};

} // namespace vlqt
