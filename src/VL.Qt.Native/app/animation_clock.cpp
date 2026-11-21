#include "app/animation_clock.h"
#include "app/exports.h"
#include "vlqt.h"

#include <QtCore/QAbstractAnimation>
#include <QtCore/QAnimationDriver>
#include <QtCore/QCoreApplication>
// QAbstractAnimationTimer / QUnifiedTimer: the keep-alive timer of the vvvv animation clock (VlqtAnimationDriver).
#include <QtCore/private/qabstractanimation_p.h>

namespace {

using vlqt::fail;
using vlqt::guardedGui;

// Animation time comes from vvvv (IFrameClock), not from a wall clock (decisions/user-qml.md, timing.md).
// QUnifiedTimer (Qt 6, qabstractanimation.cpp) computes its time as driverStartTime + driver->elapsed() while the driver
// runs, so elapsed() must count from start() like QAnimationDriver's own timer: the absolute vvvv time made every
// animation after the clock had moved jump by that time (one step to the end value). When the driver is stopped,
// QUnifiedTimer takes the wall clock instead: it stops the driver when no animation runs (stopTimer, the time base is
// reset) and when only pauses run (localRestart, a wall-clock pause timer), and takes driverStartTime from the wall clock
// at the next start. VlqtKeepAlive below keeps it running, so every delta is a difference of vvvv times.
class VlqtAnimationDriver : public QAnimationDriver
{
public:
    qint64 elapsed() const override { return m_ms - m_startMs; }
    void setTime(double seconds) { m_ms = qint64(seconds * 1000.0); }
protected:
    void start() override
    {
        m_startMs = m_ms;
        QAnimationDriver::start();
    }
private:
    qint64 m_ms = 0;
    qint64 m_startMs = 0;
};
VlqtAnimationDriver* g_driver = nullptr;

// An animation timer that is always registered and never paused: QUnifiedTimer then never stops the driver (no
// "no animations" stop, no pause-only phase on the wall clock) and never resets its time base. It animates nothing and
// counts no running animation (perf overlay "animations").
class VlqtKeepAlive : public QAbstractAnimationTimer
{
public:
    void updateAnimationsTime(qint64) override {}
    void restartAnimationTimer() override {}
    qsizetype runningAnimationCount() const override { return 0; }
};

} // namespace

namespace vlqt::clock {

void install()
{
    g_driver = new VlqtAnimationDriver();
    g_driver->install();
    // Starts the driver at the next event processing and keeps it running (VlqtAnimationDriver); lives as long as g_driver.
    QUnifiedTimer::startAnimationTimer(new VlqtKeepAlive());
}

} // namespace vlqt::clock

extern "C" {

VLQT_API int vlqt_app_advance_animations(double seconds)
{
    return guardedGui("vlqt_app_advance_animations", [&]() -> int {
        if (!g_driver)
            return fail(VLQT_ERR_NOT_INITIALIZED, "vlqt_app_advance_animations: app not initialized");
        // An animation started since the last tick (in the apply, e.g. a QML Behavior retargeted by a property set every
        // frame) enters the running list of QAnimationTimer only through a queued startAnimations() call. Without this the
        // call ran in processEvents after the tick, so an animation restarted every frame was never advanced and stood
        // still. Delivering the queued calls first gives it this tick, like Qt's own loop does before a vsync tick.
        QCoreApplication::sendPostedEvents(nullptr, QEvent::MetaCall);
        g_driver->setTime(seconds);
        g_driver->advance();
        return 0;
    });
}

} // extern "C"
