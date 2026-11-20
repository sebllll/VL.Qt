/* touch.h - Touch Points and Pinch handlers of QtNative.
 *
 * Touch Points has no QML counterpart: TouchPointsHandler is an own pointer handler (QQuickPointerDeviceHandler
 * subclass, Qt private API). Pinch is the QML PinchHandler (target null), PinchState composes its values into one
 * gesture that does not jump when the finger count changes. Protocol: protocol.h, events: events.h.
 */
#pragma once

#include <QtCore/QMetaProperty>
#include <QtCore/QObject>
#include <QtCore/QPointF>
#include <QtCore/QSizeF>
#include <QtCore/QtTypes>

// QT-UPDATE: Qt private API (Qt6::QuickPrivate; accepted because Qt has no public API for these handlers). Check on every Qt update: header path,
// QQuickPointerDeviceHandler / QQuickPointerHandler virtuals (wantsEventPoint, handlePointerEventImpl, onGrabChanged),
// setPassiveGrab, parentContains. A failing build or SmokeTest touch-points shows a break.
#include <QtQuick/private/qquickpointerdevicehandler_p.h>

#include <map>

class QQuickItem;

namespace vlqt {

/* Touch Points: passive grab on every point pressed inside the parent item, follows it until the release
 * (also outside the item), reports all of them. Does not take the points away from Tap, Point, Drag or Pinch handlers
 * at the same item. Occlusion on (default): a press that another item's handler (an element on top with Tap/Drag) or
 * item (Button) already grabs exclusively is ignored; exclusive grabs of handlers at the same item do not count.
 * acceptedDevices: QQuickPointerDeviceHandler (ACCEPTED_DEVICES, the node sets TouchScreen | Pen). */
class TouchPointsHandler : public QQuickPointerDeviceHandler
{
    Q_OBJECT
public:
    struct Point {
        const QPointingDevice* device; // with qtId the key of the point: ids are per device
        int qtId;                      // QEventPoint::id (pen and mouse always 0, touch from 0 per screen)
        QPointF pos;     // item-local, logical pixels
        float pressure;  // QEventPoint::pressure (device-dependent, often a constant)
        QSizeF size;     // QEventPoint::ellipseDiameters (often 0x0)
    };

    explicit TouchPointsHandler(QQuickItem* parent);

    const std::map<quint32, Point>& points() const { return points_; } // by own id, see points_
    bool occlusion() const { return occlusion_; }
    void setOcclusion(bool on) { occlusion_ = on; } // applies from the next press on

Q_SIGNALS:
    void pointsChanged();

protected:
    bool wantsEventPoint(const QPointerEvent* event, const QEventPoint& point) override;
    void handlePointerEventImpl(QPointerEvent* event) override;
    void onGrabChanged(QQuickPointerHandler* grabber, QPointingDevice::GrabTransition transition, QPointerEvent* event,
        QEventPoint& point) override;

private:
    bool takesPress(const QPointerEvent* event, const QEventPoint& point) const;
    std::map<quint32, Point>::iterator find(const QEventPoint& point);
    quint32 newId(int qtId) const;

    // By own id: QEventPoint::id is only unique per device (pen 0, first finger 0), so a pen and a finger
    // would share one entry. A new point keeps Qt's id when no other point uses it (touch alone: Qt's ids unchanged),
    // otherwise it gets the smallest free id; either way it keeps the id until its release.
    std::map<quint32, Point> points_;
    bool occlusion_ = true;
};

/* Pinch: follows the QML PinchHandler (target null) and composes its active values into one gesture.
 * Qt ends the pinch (active false, values back to 1/0/0) as soon as the number of points differs from the one it
 * started with (a third finger down or up) and starts a new one later. PinchState keeps the gesture running while at
 * least minimumPointCount points lie on the item (counted by an internal TouchPointsHandler without occlusion): values
 * freeze while Qt is inactive and go on from there when Qt activates again (the transform of the new activation is
 * composed with the frozen one), so a changing finger count does not jump. The values describe
 * p' = pivot + translation + scale * R(rotation) * (p - pivot) in the parent's space, with the centroid of the first
 * activation as the pivot: applied like that, the content follows the fingers. QObject child of the QML PinchHandler
 * (deleted with it). */
class PinchState : public QObject
{
    Q_OBJECT
public:
    PinchState(QObject* pinch, TouchPointsHandler* points);

    float value[6] = {1, 0, 0, 0, 0, 0}; // scale, rotation (degrees, clockwise), translation x, y, pivot x, y (parent space) of the gesture
    bool running = false;          // gesture running (reported as EV_ACTIVE)
    quint32 gestures = 0;          // gestures started: a new one sends EV_PINCH even with the same value

public Q_SLOTS:
    void onActiveChanged();
    void onValuesChanged();
    void onPointsChanged();

private:
    QPointF centroid(); // scene position of the pinch's centroid

    QObject* pinch_;
    TouchPointsHandler* points_;
    float base_[4] = {1, 0, 0, 0}; // value of the gesture when Qt activated the last time
    QPointF start_;                // centroid (scene) at the last activation
    QPointF pivot_;                // centroid (scene) at the first activation of the gesture
    QMetaProperty centroidPos_;    // QQuickHandlerPoint::scenePosition, resolved on first use
};

} // namespace vlqt
