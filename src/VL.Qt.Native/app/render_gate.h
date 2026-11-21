// The render gate of the pump (at most one render per pump, only inside it) and the change count of a scene, both in
// UpdateGate. Qt GUI thread; the sync hook of an offscreen scene's gate runs on that scene's render thread. Private Qt
// API in render_gate.cpp: the dirty item list of a window and the delayed touch of its delivery agent.
#pragma once

#include <QtCore/QObject>

#include <vector>

class QQuickItem;
class QQuickWindow;

namespace vlqt {

// At most one render per pump, and only inside the pump. With the basic loop Qt renders on UpdateRequest,
// which vvvv's Win32 message loop also delivers between two pumps. A MultiEffect source (layer) requests a second,
// redundant render right after each one (frame interval ~0.9 ms, "1100 fps"). Requests outside the pump or after
// this pump's render are swallowed and delivered once at the end of the next pump. The pump never waits for VSync.
// several vvvv apps (entry points) pump the one QGuiApplication, each once per vvvv frame. Only the windows of the
// pumping app may render (the others are blocked and keep their request for their own app's pump), so each window
// still renders at most once per vvvv frame instead of once per app.
class UpdateGate : public QObject
{
public:
    explicit UpdateGate(QQuickWindow* window);
    ~UpdateGate() override;

    // Qt rendered this window in the last pump (and so polished its items). Valid until the next pump starts.
    bool rendered() const { return m_rendered; }

    // Frames with a change of the scene (vlqt_scene_get_change_count): counted where Qt syncs a render, so a
    // static scene keeps the count however often the pump runs or Expose re-renders it.
    long long changeCount() const { return m_changeCount; }
    // The scene changed without a dirty item (e.g. the window color): count the next render.
    void markChanged() { m_changed = true; }
    // Window swap: the gate of the new window goes on with the change count of the old one, so the count never
    // goes back; the first render of the new window counts as a change (other sample count).
    void continueFrom(const UpdateGate& old)
    {
        m_changeCount = old.m_changeCount;
        m_changed = true;
    }

    // all = every window may render; else only the given gates (the windows of the pumping app).
    static void beginPump(bool all, UpdateGate* const* active, int count);
    static void endPump();
    static bool inPump() { return s_inPump; }
    // input delivered at the host frame start (vlqt_app_deliver_input) renders nothing: update requests are
    // swallowed as outside every pump, and an Expose is deferred to the next pump as well.
    static void beginDelivery() { s_delivering = true; }
    static void endDelivery() { s_delivering = false; }
    // Qt Quick compresses touch moves and delivers the last one right before the next render (flushFrameSynchronousEvents).
    // The delivery at the frame start renders nothing, so it hands them over itself; the render finds none left.
    static void deliverDelayedTouch();
protected:
    bool eventFilter(QObject* o, QEvent* e) override;
private:
    bool sceneDirty();
    bool isGrabSource(QQuickItem* i) const;
    bool inPerfOverlay(QQuickItem* i) const;
    static bool isPerfOverlay(QQuickItem* i);
    static bool hasRefChild(QQuickItem* parent);
    bool contentChildrenChanged();

    QQuickWindow* m_window;
    long long m_changeCount = 0;
    bool m_changed = false;
    std::vector<QQuickItem*> m_contentChildren; // the content item's children at the last render, without frame sources
    static inline std::vector<QQuickItem*> s_children; // scratch, no allocation per render
    bool m_pending = false, m_rendered = false, m_everRendered = false;
    bool m_blocked = false; // this pump belongs to another app
    static inline bool s_inPump = false;
    static inline bool s_delivering = false; // vlqt_app_deliver_input
    static inline std::vector<UpdateGate*> s_gates;
};

} // namespace vlqt
