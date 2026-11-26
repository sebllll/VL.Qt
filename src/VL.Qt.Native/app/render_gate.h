// The render gate of the pump: at most one render per window and pump, and only inside it. Qt GUI thread, windows
// only: an offscreen scene renders when offscreen/offscreen_render.cpp drives it. The change count of a scene is in
// app/scene_change.h.
#pragma once

#include <QtCore/QObject>

#include <vector>

class QQuickWindow;

namespace vlqt {

// At most one render per pump, and only inside the pump. With the basic loop Qt renders on UpdateRequest,
// which vvvv's Win32 message loop also delivers between two pumps. A MultiEffect source (layer) requests a second,
// redundant render right after each one (frame interval ~0.9 ms, "1100 fps"). Requests outside the pump or after
// this pump's render are swallowed and delivered once at the end of the next pump. The pump never waits for VSync.
// several vvvv apps (entry points) pump the one QGuiApplication, each once per vvvv frame. Only the windows of the
// pumping app may render (the others are blocked and keep their request for their own app's pump), so each window
// still renders at most once per vvvv frame instead of once per app.
class RenderGate : public QObject
{
public:
    // Child of the window; the caller installs it as the window's event filter.
    explicit RenderGate(QQuickWindow* window);
    ~RenderGate() override;

    // Qt rendered this window in the last pump (and so polished its items). Valid until the next pump starts.
    bool rendered() const { return m_rendered; }

    // all = every window may render; else only the given gates (the windows of the pumping app).
    static void beginPump(bool all, RenderGate* const* active, int count);
    static void endPump();
    static bool inPump() { return s_inPump; }

    // Input delivered at the host frame start (vlqt_app_deliver_input) renders nothing: update requests are
    // swallowed as outside every pump, and an Expose is deferred to the next pump as well.
    class DeliveryScope
    {
    public:
        DeliveryScope() { s_delivering = true; }
        ~DeliveryScope() { s_delivering = false; }
        DeliveryScope(const DeliveryScope&) = delete;
        DeliveryScope& operator=(const DeliveryScope&) = delete;
    };

protected:
    bool eventFilter(QObject* o, QEvent* e) override;

private:
    QQuickWindow* m_window;
    bool m_pending = false, m_rendered = false, m_everRendered = false;
    bool m_blocked = false; // this pump belongs to another app
    static inline bool s_inPump = false;
    static inline bool s_delivering = false; // vlqt_app_deliver_input
    static inline std::vector<RenderGate*> s_gates;
};

} // namespace vlqt
