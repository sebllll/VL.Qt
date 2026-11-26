// Input at the host frame start: vlqt_app_deliver_input delivers the input Qt has queued, without rendering. Qt GUI
// thread. Private Qt API: QWindowSystemInterface (qpa) and the delayed touch of a window's delivery agent, check on Qt
// updates (list: elements/elements.h).
#include "app/app.h"
#include "app/exports.h"
#include "app/render_gate.h"
#include "vlqt.h"

#include <QtCore/QEventLoop>
#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickWindow>
// vlqt_app_deliver_input: QWindowSystemInterface::flushWindowSystemEvents.
#include <QtGui/qpa/qwindowsysteminterface.h>
// Touch moves held back until the next render: QQuickDeliveryAgentPrivate::delayedTouch.
#include <QtQuick/private/qquickdeliveryagent_p_p.h>
#include <QtQuick/private/qquickwindow_p.h>

using vlqt::fail;
using vlqt::g_app;
using vlqt::guardedGui;
using vlqt::RenderGate;

namespace {

// Qt Quick compresses touch moves and delivers the last one right before the next render (flushFrameSynchronousEvents).
// The delivery at the frame start renders nothing, so it hands them over itself; the render finds none left. Every Qt
// Quick window: the windows and offscreen scenes of the handles and the QML windows of popups. allWindows() shares
// Qt's list, no allocation per frame.
void deliverDelayedTouch()
{
    const QWindowList windows = QGuiApplication::allWindows();
    for (QWindow* window : windows) {
        auto* quick = qobject_cast<QQuickWindow*>(window);
        if (!quick)
            continue;
        QQuickDeliveryAgentPrivate* agent = QQuickWindowPrivate::get(quick)->deliveryAgentPrivate();
        if (agent && agent->delayedTouch)
            agent->deliverDelayedTouchEvent();
    }
}

} // namespace

extern "C" {

VLQT_API int vlqt_app_deliver_input(void)
{
    return guardedGui("vlqt_app_deliver_input", [&]() -> int {
        if (!g_app)
            return fail(VLQT_ERR_NOT_INITIALIZED, "vlqt_app_deliver_input: app not initialized");
        // From inside the pump (a host callback in processEvents) the pump itself delivers.
        if (RenderGate::inPump())
            return 0;
        // Windows: vvvv's message loop between two frames has run Qt's window procedure, which queued the input here
        // (or already delivered it on Qt's posted-events message). Delivery goes to the target window of each event;
        // events of a destroyed window are dropped by Qt (QPointer), so the window list of the pump is not needed.
        const int queued = QWindowSystemInterface::windowSystemEventsQueued();
        const RenderGate::DeliveryScope delivery;
        if (queued > 0)
            QWindowSystemInterface::flushWindowSystemEvents(QEventLoop::AllEvents);
        // Also touch moves that the message loop between two frames already delivered to Qt Quick.
        deliverDelayedTouch();
        return queued;
    });
}

} // extern "C"
