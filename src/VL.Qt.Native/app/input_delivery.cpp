// Input at the host frame start: vlqt_app_deliver_input delivers the input Qt has queued, without rendering. Qt GUI
// thread. Private Qt API: QWindowSystemInterface (qpa), check on Qt updates.
#include "app/app.h"
#include "app/exports.h"
#include "app/render_gate.h"
#include "vlqt.h"

#include <QtCore/QEventLoop>
// vlqt_app_deliver_input: QWindowSystemInterface::flushWindowSystemEvents.
#include <QtGui/qpa/qwindowsysteminterface.h>

using vlqt::fail;
using vlqt::g_app;
using vlqt::guarded;
using vlqt::UpdateGate;

extern "C" {

VLQT_API int vlqt_app_deliver_input(void)
{
    return guarded([&]() -> int {
        if (!g_app)
            return fail(VLQT_ERR_NOT_INITIALIZED, "deliver_input: app not initialized");
        // From inside the pump (a host callback in processEvents) the pump itself delivers.
        if (UpdateGate::inPump())
            return 0;
        // Windows: vvvv's message loop between two frames has run Qt's window procedure, which queued the input here
        // (or already delivered it on Qt's posted-events message). Delivery goes to the target window of each event;
        // events of a destroyed window are dropped by Qt (QPointer), so the window list of the pump is not needed.
        const int queued = QWindowSystemInterface::windowSystemEventsQueued();
        struct Delivery {
            Delivery() { UpdateGate::beginDelivery(); }
            ~Delivery() { UpdateGate::endDelivery(); }
        } delivery;
        if (queued > 0)
            QWindowSystemInterface::flushWindowSystemEvents(QEventLoop::AllEvents);
        // Also touch moves that the message loop between two frames already delivered to Qt Quick.
        UpdateGate::deliverDelayedTouch();
        return queued;
    });
}

} // extern "C"
