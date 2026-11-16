#include "touch.h"

#include <QtCore/QMetaObject>
#include <QtCore/QVariant>
#include <QtGui/QVector2D>
#include <QtQuick/QQuickItem>
#include <cmath>

namespace vlqt {

TouchPointsHandler::TouchPointsHandler(QQuickItem* parent) : QQuickPointerDeviceHandler(nullptr)
{
    // Like data_append of `Item { Handler {} }`: QObject parent + registration at the item (addPointerHandler).
    setParentItem(parent);
}

bool TouchPointsHandler::takesPress(const QPointerEvent* event, const QEventPoint& point) const
{
    if (!parentContains(point))
        return false;
    if (!occlusion_)
        return true;
    // Occlusion: an element on top took the press (its Tap/Drag handler or a Button item grabbed it exclusively).
    // Handlers of the same item do not occlude: Qt may deliver a Tap handler of this item before this handler.
    QObject* grabber = event->exclusiveGrabber(point);
    if (!grabber)
        return true;
    const auto* handler = qobject_cast<const QQuickPointerHandler*>(grabber);
    return handler && handler->parentItem() == parentItem();
}

std::map<quint32, TouchPointsHandler::Point>::iterator TouchPointsHandler::find(const QEventPoint& point)
{
    const QPointingDevice* device = point.device();
    const int id = point.id();
    for (auto it = points_.begin(); it != points_.end(); ++it) {
        if (it->second.device == device && it->second.qtId == id)
            return it;
    }
    return points_.end();
}

quint32 TouchPointsHandler::newId(int qtId) const
{
    if (qtId >= 0 && points_.count(quint32(qtId)) == 0)
        return quint32(qtId);
    quint32 id = 0;
    for (const auto& entry : points_) { // sorted by id
        if (entry.first != id)
            break;
        ++id;
    }
    return id;
}

bool TouchPointsHandler::wantsEventPoint(const QPointerEvent* event, const QEventPoint& point)
{
    if (point.state() != QEventPoint::Pressed)
        return find(point) != points_.end();
    return takesPress(event, point);
}

void TouchPointsHandler::handlePointerEventImpl(QPointerEvent* event)
{
    bool changed = false;
    for (auto& p : event->points()) {
        auto it = find(p);
        const bool known = it != points_.end();
        if (p.state() == QEventPoint::Pressed) {
            if (known || !takesPress(event, p))
                continue;
            setPassiveGrab(event, p, true);
            it = points_.emplace(newId(p.id()), Point{p.device(), p.id()}).first;
        } else if (!known || p.state() == QEventPoint::Stationary) {
            continue;
        }
        if (p.state() == QEventPoint::Released) {
            points_.erase(it);
        } else {
            it->second.pos = parentItem()->mapFromScene(p.scenePosition());
            it->second.pressure = float(p.pressure());
            it->second.size = p.ellipseDiameters();
        }
        changed = true;
    }
    if (changed)
        Q_EMIT pointsChanged();
}

void TouchPointsHandler::onGrabChanged(QQuickPointerHandler* grabber, QPointingDevice::GrabTransition transition,
    QPointerEvent* event, QEventPoint& point)
{
    QQuickPointerDeviceHandler::onGrabChanged(grabber, transition, event, point);
    // Passive grab lost (cancel, window gone): forget the point, nothing stays stuck.
    if (grabber != this
        || (transition != QPointingDevice::CancelGrabPassive && transition != QPointingDevice::OverrideGrabPassive))
        return;
    const auto it = find(point);
    if (it == points_.end())
        return;
    points_.erase(it);
    Q_EMIT pointsChanged();
}

PinchState::PinchState(QObject* pinch, TouchPointsHandler* points) : QObject(pinch), pinch_(pinch), points_(points) {}

QPointF PinchState::centroid()
{
    const QVariant v = pinch_->property("centroid"); // QQuickHandlerPoint gadget
    if (!centroidPos_.isValid()) {
        const QMetaObject* mo = v.metaType().metaObject();
        const int i = mo ? mo->indexOfProperty("scenePosition") : -1;
        if (i < 0)
            return {};
        centroidPos_ = mo->property(i);
    }
    return centroidPos_.readOnGadget(v.constData()).toPointF();
}

void PinchState::onActiveChanged()
{
    if (pinch_->property("active").toBool()) {
        if (!running) {
            running = true;
            ++gestures;
            for (int i = 0; i < 4; ++i)
                value[i] = base_[i] = i == 0 ? 1.f : 0.f;
            pivot_ = centroid();
        } else {
            // Qt starts again after a change of the finger count: go on from the frozen value.
            for (int i = 0; i < 4; ++i)
                base_[i] = value[i];
        }
        // The centroid is current at the activation (set before Qt activates).
        start_ = centroid();
        onValuesChanged();
    } else {
        // Qt resets activeScale/Rotation/Translation right after this: value stays frozen.
        onPointsChanged();
    }
}

void PinchState::onValuesChanged()
{
    if (!running || !pinch_->property("active").toBool())
        return;
    // Translation from the centroid, not activeTranslation: after a change of the finger count Qt 6.12 reports
    // activeTranslation as the centroid's scene position (press position lost, SmokeTest pinch), a jump of hundreds of
    // pixels. Centroid movement since the activation in scene coordinates, mapped into the parent's space like the Drag
    // handler (events.h).
    const QQuickItem* item = points_->parentItem(); // the tracker sits at the pinch's item
    const QQuickItem* parent = item ? item->parentItem() : nullptr;
    const auto toParent = [parent](const QPointF& scene) { return parent ? parent->mapFromScene(scene) : scene; };
    const QPointF c = toParent(centroid());
    const QPointF c1 = toParent(start_);   // centroid at the last activation
    const QPointF c0 = toParent(pivot_);   // pivot of the whole gesture
    const double s1 = pinch_->property("activeScale").toDouble();
    const double a1 = pinch_->property("activeRotation").toDouble();
    // Qt scales and rotates about the centroid of its last activation (c1), the reported values about the pivot c0:
    // the transform so far (about c0) followed by the one of Qt (about c1), written as one about c0. Without a change of
    // the finger count c1 = c0 and the translation is the centroid movement.
    const double rad = a1 * 3.14159265358979323846 / 180.0;
    const double cs = s1 * std::cos(rad), sn = s1 * std::sin(rad);
    const QPointF q = c0 + QPointF(base_[2], base_[3]) - c1;
    const QPointF t = QPointF(cs * q.x() - sn * q.y(), sn * q.x() + cs * q.y()) + c - c0;
    value[0] = base_[0] * float(s1);
    value[1] = base_[1] + float(a1);
    value[2] = float(t.x());
    value[3] = float(t.y());
    value[4] = float(c0.x());
    value[5] = float(c0.y());
}

void PinchState::onPointsChanged()
{
    // The gesture ends when Qt is inactive and fewer points than needed lie on the item (Qt or the points first).
    if (running && !pinch_->property("active").toBool()
        && qint64(points_->points().size()) < pinch_->property("minimumPointCount").toInt())
        running = false;
}

} // namespace vlqt
