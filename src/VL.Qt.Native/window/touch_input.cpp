// Injected touch of offscreen scenes and of the test exports (sendTouch, cancelTouch, declared in offscreen/offscreen.h). Qt GUI
// thread. Private Qt API: QMutableEventPoint and QPointingDevicePrivate, check on Qt updates (list: elements/elements.h).
#include "offscreen/offscreen.h"
#include "app/last_error.h"

#include <QtCore/QCoreApplication>
#include <QtGui/QPointingDevice>
#include <QtGui/QTouchEvent>
#include <QtQuick/QQuickWindow>
// Injected touch (sendTouch): event points with an item-local position, the device's point registration.
#include <QtGui/private/qeventpoint_p.h>
#include <QtGui/private/qpointingdevice_p.h>

#include <algorithm>
#include <string>

namespace vlqt::offscreen {

bool sendTouch(QQuickWindow* window, const QPointingDevice* device, std::vector<std::pair<int, QPointF>>& active,
    QEventPoint::State state, int id, QPointF pos, ulong timestamp, Qt::KeyboardModifiers modifiers, const char* function)
{
    auto it = std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; });
    const bool known = it != active.end();
    const bool press = state == QEventPoint::State::Pressed;
    if (known == press) {
        vlqt::setLastError(std::string(function) + ": point " + std::to_string(id) + (known ? " is already pressed" : " is not pressed"));
        return false;
    }
    if (known)
        it->second = pos;
    else
        active.emplace_back(id, pos);

    auto* devicePrivate = QPointingDevicePrivate::get(const_cast<QPointingDevice*>(device));
    // What QGuiApplication does before delivering a press: the device registers the point. Without it the grabs
    // of the delivery are not stored ("point is not in activePoints"), the release goes to every handler under the
    // point and stacked Tap handlers all tap.
    if (press)
        devicePrivate->pointById(id);
    // The complete touch state: every active point, the changed one with its state, the others stationary.
    QList<QEventPoint> points;
    for (const auto& a : active) {
        const QEventPoint::State st = a.first == id ? state : QEventPoint::State::Stationary;
        QEventPoint ep = QMutableEventPoint::withTimeStamp(timestamp, a.first, st, a.second, a.second, window->mapToGlobal(a.second));
        QMutableEventPoint::setDevice(ep, device);
        QMutableEventPoint::setWindow(ep, window);
        points.append(ep);
    }
    const bool release = state == QEventPoint::State::Released;
    const QEvent::Type t = press && active.size() == 1 ? QEvent::TouchBegin
        : release && active.size() == 1               ? QEvent::TouchEnd
                                                      : QEvent::TouchUpdate;
    QTouchEvent ev(t, device, modifiers, points);
    ev.setTimestamp(timestamp);
    QCoreApplication::sendEvent(window, &ev);
    if (release) {
        active.erase(std::find_if(active.begin(), active.end(), [id](const auto& a) { return a.first == id; }));
        // What QGuiApplication does after delivering a release: the device forgets the point (and its grabbers).
        devicePrivate->removePointById(id);
    }
    return true;
}

void cancelTouch(QQuickWindow* window, const QPointingDevice* device, std::vector<std::pair<int, QPointF>>& active,
    ulong timestamp, Qt::KeyboardModifiers modifiers)
{
    if (active.empty())
        return;
    // Without points, like the platform's: Qt Quick cancels every grab of the device's active points.
    QTouchEvent ev(QEvent::TouchCancel, device, modifiers);
    ev.setTimestamp(timestamp);
    QCoreApplication::sendEvent(window, &ev);
    auto* devicePrivate = QPointingDevicePrivate::get(const_cast<QPointingDevice*>(device));
    for (const auto& a : active)
        devicePrivate->removePointById(a.first);
    active.clear();
}

} // namespace vlqt::offscreen
