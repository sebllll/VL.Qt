// Private Qt API: QAbstractAnimationTimer, QUnifiedTimer (keep-alive); check on Qt updates (list: elements/elements.h).
#include "app/animation_clock.h"
#include "app/exports.h"
#include "vlqt.h"

#include <QtCore/QAbstractAnimation>
#include <QtCore/QAnimationDriver>
#include <QtCore/QCoreApplication>
// QAbstractAnimationTimer / QUnifiedTimer: the keep-alive timer of the vvvv animation clock (VlqtAnimationDriver).
#include <QtCore/private/qabstractanimation_p.h>

#include <cmath>

namespace {

using vlqt::fail;
using vlqt::guardedGui;

// Animation time comes from vvvv (IFrameClock), not from a wall clock (docs/design/timing.md, "Clock and driver"; docs/design/user-qml.md, "Time").
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
    // seconds is finite and in range (vlqt::clock::advance): the cast to qint64 is undefined behaviour otherwise.
    // The driver time never runs backwards: a time below the current one is ignored (animations hold until the clock
    // passes the old time again), whichever caller advances the clock (several apps, offscreen renders).
    void setTime(double seconds)
    {
        const qint64 ms = qint64(seconds * 1000.0);
        if (ms > m_ms)
            m_ms = ms;
    }
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
// The driver and the keep-alive timer live as long as the process, like the QGuiApplication: never deleted.
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

int advance(double seconds)
{
    if (!g_driver)
        return fail(VLQT_ERR_NOT_INITIALIZED, "advance_animations: app not initialized");
    // A NaN, an infinite or a huge time of a broken clock would overflow the conversion to milliseconds.
    const double ms = seconds * 1000.0;
    if (!std::isfinite(ms) || std::fabs(ms) >= 9.0e18)
        return fail(VLQT_ERR_BAD_ARGUMENT, "advance_animations: seconds is not a finite time");
    // An animation started since the last tick (in the apply, e.g. a QML Behavior retargeted by a property set every
    // frame) enters the running list of QAnimationTimer only through a queued startAnimations() call. Without this the
    // call ran in processEvents after the tick, so an animation restarted every frame was never advanced and stood
    // still. Delivering the queued calls first gives it this tick, like Qt's own loop does before a vsync tick.
    QCoreApplication::sendPostedEvents(nullptr, QEvent::MetaCall);
    g_driver->setTime(seconds);
    g_driver->advance();
    return VLQT_OK;
}

} // namespace vlqt::clock

extern "C" {

VLQT_API int vlqt_app_advance_animations(double seconds)
{
    return guardedGui("vlqt_app_advance_animations", [&]() -> int { return vlqt::clock::advance(seconds); });
}

} // extern "C"
