#include "events.h"
#include "commands.h"
#include "touch.h"

#include <QtCore/QByteArray>
#include <QtCore/QLoggingCategory>
#include <QtGui/QInputDevice>
#include <QtGui/QVector2D>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlContext>
#include <QtQml/QQmlEngine>
#include <QtQml/QQmlListReference>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>

#include <algorithm>
#include <cstring>

namespace vlqt {
namespace {

Q_LOGGING_CATEGORY(lcEvents, "vlqt.events")

constexpr const char* kHoverQml = "import QtQuick\nHoverHandler {}\n";
// ReleaseWithinBounds: exclusive grab on press (button semantics, the topmost tap handler wins), tap when released
// inside even after moving out and back.
constexpr const char* kTapQml = "import QtQuick\nTapHandler { gesturePolicy: TapHandler.ReleaseWithinBounds }\n";
// Passive grab: the press goes on to the handlers below (a Tap handler under a Point handler still taps).
constexpr const char* kPointQml = "import QtQuick\nPointHandler {}\n";
// target null: the handler moves/scales nothing, it only reports. The wheel event (QQuickWheelEvent) is no public C++
// API, so the notches are summed up in QML and taken by Handlers::takeWheel.
constexpr const char* kWheelQml = "import QtQuick\nWheelHandler {\n    target: null\n    property point vlqtDelta\n"
                                  "    onWheel: (event) => vlqtDelta = Qt.point(vlqtDelta.x + event.angleDelta.x / 120,"
                                  " vlqtDelta.y + event.angleDelta.y / 120)\n}\n";
// target null: the handler moves nothing itself, DragState writes the drag offset of the item (#209).
constexpr const char* kDragQml = "import QtQuick\nDragHandler { target: null }\n";
// target null: the handler scales/rotates/moves nothing, PinchState reports its values (#321).
constexpr const char* kPinchQml = "import QtQuick\nPinchHandler { target: null }\n";
// EV_TOUCH_POINTS: at most this many points (u16 record length; 8 + 4 + 64 * 24 bytes).
constexpr int kMaxTouchPoints = 64;

QMetaProperty metaProperty(const QObject* o, const char* name)
{
    const QMetaObject* mo = o->metaObject();
    const int i = mo->indexOfProperty(name);
    return i >= 0 ? mo->property(i) : QMetaProperty();
}

void pushFlag(Scene& scene, quint16 ev, quint32 handle, bool on)
{
    const quint8 v = on ? 1 : 0;
    scene.pushEvent(ev, handle, 0, &v, 1);
}

/* The sink loses the handler (new handle, DESTROY): flags that were 1 go back to 0, wheel motion ends with (0, 0).
   Forgets the sent state, so a new sink gets the current state with the next poll. */
void resetSent(Scene& scene, HandlerNode& hn)
{
    if (hn.sink != 0) {
        if (hn.sentHover)
            pushFlag(scene, VLQT_EV_HOVER, hn.sink, false);
        if (hn.sentPress)
            pushFlag(scene, VLQT_EV_PRESS, hn.sink, false);
        if (hn.sentActive)
            pushFlag(scene, VLQT_EV_ACTIVE, hn.sink, false);
        if (hn.sentWheel) {
            const float zero[2] = {};
            scene.pushEvent(VLQT_EV_WHEEL, hn.sink, 0, zero, quint16(sizeof zero));
        }
        if (hn.sentTouch.size() > 4) { // points were sent: the old sink sees them go
            const quint32 zero = 0;
            scene.pushEvent(VLQT_EV_TOUCH_POINTS, hn.sink, 0, &zero, quint16(sizeof zero));
        }
    }
    hn.sentTouch.clear();
    hn.sentGestures = 0;
    hn.sentHover = hn.sentPress = hn.sentActive = hn.sentWheel = hn.hasPointer = false;
    hn.sentTapCount = 0;
    hn.sentDrag[0] = hn.sentDrag[1] = 0;
}

bool supportsButtons(quint16 type)
{
    return type == VLQT_TYPE_TAP_HANDLER || type == VLQT_TYPE_POINT_HANDLER || type == VLQT_TYPE_DRAG_HANDLER;
}

/* DRAG_AXIS on the QML handler: xAxis/yAxis.enabled. Qt then also ignores a drag that runs more than 45 degrees off
   the enabled axis, so a parent's drag handler can take it (see events.h). */
void applyDragAxis(QObject* obj, qint32 axis)
{
    QObject* x = obj->property("xAxis").value<QObject*>();
    QObject* y = obj->property("yAxis").value<QObject*>();
    if (!x || !y || !x->setProperty("enabled", axis != 2) || !y->setProperty("enabled", axis != 1))
        qCWarning(lcEvents, "drag handler axis could not be set to %d", int(axis));
}

bool supportsDevices(quint16 type)
{
    return supportsButtons(type) || type == VLQT_TYPE_WHEEL_HANDLER || type == VLQT_TYPE_TOUCH_POINTS_HANDLER
        || type == VLQT_TYPE_PINCH_HANDLER;
}

/* ACCEPTED_BUTTONS / ACCEPTED_DEVICES on the QML handler (QFlags properties need their exact type in the QVariant). */
bool applyAccepted(QObject* obj, quint16 prop, qint32 v)
{
    const bool ok = prop == VLQT_PROP_ACCEPTED_BUTTONS
        ? obj->setProperty("acceptedButtons", QVariant::fromValue(Qt::MouseButtons(v)))
        : obj->setProperty("acceptedDevices", QVariant::fromValue(QInputDevice::DeviceTypes(v)));
    if (!ok)
        qCWarning(lcEvents, "input handler property %d could not be set to %d", int(prop), int(v));
    return ok;
}

/* Point handler with ACCEPTED_BUTTONS 0 (#320): an internal HoverHandler makes the mouse (and touchpad cursor) of
   ACCEPTED_DEVICES active while it is over the item; the PointHandler itself then only takes touch and pen. Creates,
   updates or removes it after a change of the item, the buttons or the devices. */
void syncPointHover(Scene& scene, HandlerNode& hn, QQuickItem* item)
{
    if (hn.type != VLQT_TYPE_POINT_HANDLER || !hn.object)
        return;
    constexpr qint32 cursorDevices = qint32(QInputDevice::DeviceType::Mouse) | qint32(QInputDevice::DeviceType::TouchPad);
    const qint32 devices = (hn.acceptedDevices ? *hn.acceptedDevices : qint32(QInputDevice::DeviceType::AllDevices))
        & cursorDevices;
    const bool want = hn.acceptedButtons && *hn.acceptedButtons == 0 && devices != 0;
    if (!want) {
        delete hn.hover; // the next poll sends ACTIVE 0 if only the hover made the handler active
        hn.hover = nullptr;
    } else if (!hn.hover) {
        hn.hover = scene.handlers.createHover(item, devices);
    } else {
        applyAccepted(hn.hover, VLQT_PROP_ACCEPTED_DEVICES, devices);
    }
}

/* Point handlers first (#320 manual test): Qt delivers to the handlers of an item from the last attached to the first
   (QQuickItemPrivate::addPointerHandler prepends), and a PointHandler ignores a mouse point that another handler already
   grabs exclusively. A Tap handler attached after a Point handler took the left press first (exclusive grab, button
   semantics) and the Point handler stayed inactive. So the Point handlers of the item are attached again after any
   other handler: they take their passive grab first, the Tap handler still grabs exclusively and taps. */
void raisePointHandlers(Scene& scene, const Node& owner, quint32 except)
{
    for (quint32 id : owner.handlers) {
        auto it = scene.handlerNodes.find(id);
        if (id == except || it == scene.handlerNodes.end() || it->second.type != VLQT_TYPE_POINT_HANDLER || !it->second.object)
            continue;
        QObject* obj = it->second.object;
        // QQuickPointerHandler::parent (setParentItem): remove from the item, add again in front of the list.
        if (!obj->setProperty("parent", QVariant::fromValue<QQuickItem*>(nullptr))
            || !obj->setProperty("parent", QVariant::fromValue(owner.item)))
            qCWarning(lcEvents, "point handler %u could not be re-attached to its item", unsigned(id));
    }
}

} // namespace

void TapState::cancel(bool pressed)
{
    taps = longPresses = tapCount = 0; // the tap count belongs to the old sink, the new one starts at the next tap
    cancelled = cancelled || pressed;
}

void TapState::onTapped()
{
    if (cancelled)
        return;
    ++taps;
    tapCount = tap_->property("tapCount").toUInt(); // updated before tapped
}

void TapState::onLongPressed()
{
    if (!cancelled)
        ++longPresses;
}

void TapState::onPressedChanged()
{
    // A new press ends a cancel; the release of the cancelled press does not.
    if (tap_->property("pressed").toBool())
        cancelled = false;
}

void DragState::onActiveChanged()
{
    if (!drag_->property("active").toBool())
        return; // the offset stays, delta keeps the last drag until the next one starts
    auto hit = scene_.handlerNodes.find(id_);
    auto nit = hit == scene_.handlerNodes.end() ? scene_.nodes.end() : scene_.nodes.find(hit->second.owner);
    if (nit == scene_.nodes.end())
        return;
    const Node& node = nit->second;
    // Several drag handlers at one item share its one offset: each starts from the offset as it is now.
    start_[0] = node.xf ? node.xf->dx : 0.f;
    start_[1] = node.xf ? node.xf->dy : 0.f;
    raw_[0] = raw_[1] = 0;
    delta[0] = delta[1] = 0;
    ++drags;
    epoch_ = node.transformEpoch;
    onTranslationChanged(); // activation already has a translation (the drag threshold)
}

void DragState::onTranslationChanged()
{
    if (!drag_->property("active").toBool())
        return;
    auto hit = scene_.handlerNodes.find(id_);
    auto nit = hit == scene_.handlerNodes.end() ? scene_.nodes.end() : scene_.nodes.find(hit->second.owner);
    if (nit == scene_.nodes.end())
        return;
    HandlerNode& hn = hit->second;
    Node& node = nit->second;

    // activeTranslation is the centroid's movement in scene (window) coordinates; the offset lives in the parent's
    // space (applied after the item's own TRANSFORM), so map the vector into the parent.
    const QPointF t = drag_->property("activeTranslation").value<QVector2D>().toPointF();
    QPointF d = t;
    if (const QQuickItem* parent = node.item->parentItem())
        d = parent->mapFromScene(t) - parent->mapFromScene(QPointF());
    if (hn.dragAxis == 2)
        d.setX(0);
    if (hn.dragAxis == 1)
        d.setY(0);

    // SET TRANSFORM during the drag (pin wins): the offset went to 0, the drag goes on from there.
    if (node.transformEpoch != epoch_) {
        epoch_ = node.transformEpoch;
        start_[0] = -raw_[0];
        start_[1] = -raw_[1];
    }
    raw_[0] = float(d.x());
    raw_[1] = float(d.y());
    const float ox = std::clamp(start_[0] + raw_[0], hn.dragLimits[0], hn.dragLimits[2]);
    const float oy = std::clamp(start_[1] + raw_[1], hn.dragLimits[1], hn.dragLimits[3]);
    delta[0] = ox - start_[0];
    delta[1] = oy - start_[1];
    std::string err;
    if (hn.movesElement && !scene_.elements.setDragOffset(node, ox, oy, err))
        qCWarning(lcEvents, "drag offset of item %u could not be set: %s", unsigned(hn.owner), err.c_str());
}

TextMask::TextMask(QQuickItem* item) : QObject(item), item_(item) {}

bool TextMask::contains(const QPointF& point) const
{
    return item_->boundingRect().contains(point);
}

Handlers::~Handlers()
{
    for (QQmlComponent* c : comps)
        delete c;
}

QObject* Handlers::create(QQmlComponent*& comp, const char* qml, const char* url, QQuickItem* item)
{
    if (!comp) {
        comp = new QQmlComponent(engine);
        comp->setData(QByteArray(qml), QUrl(QString::fromLatin1(url)));
    }
    // beginCreate + data.append + completeCreate: HoverHandler registers hover at its parent item in
    // componentComplete, so the parent must be set before (a plain create() leaves it without hover).
    QObject* obj = comp->isError() ? nullptr : comp->beginCreate(engine->rootContext());
    if (!obj) {
        qCWarning(lcEvents, "%s could not be created: %s", url, qPrintable(comp->errorString()));
        return nullptr;
    }
    QQmlEngine::setObjectOwnership(obj, QQmlEngine::CppOwnership);
    // Like `Item { HoverHandler {} TapHandler {} }`: the data list registers pointer handlers at the item.
    QQmlListReference data(item, "data");
    data.append(obj);
    comp->completeCreate();
    return obj;
}

QObject* Handlers::createHandler(quint16 type, QQuickItem* item)
{
    QObject* obj = nullptr;
    switch (type) {
    case VLQT_TYPE_HOVER_HANDLER:
        obj = create(comps[0], kHoverQml, "vlqt_hoverhandler.qml", item);
        if (obj && !props.hovered.isValid())
            props.hovered = metaProperty(obj, "hovered");
        break;
    case VLQT_TYPE_TAP_HANDLER:
        obj = create(comps[1], kTapQml, "vlqt_taphandler.qml", item);
        if (obj && !props.pressed.isValid())
            props.pressed = metaProperty(obj, "pressed");
        break;
    case VLQT_TYPE_POINT_HANDLER:
        obj = create(comps[2], kPointQml, "vlqt_pointhandler.qml", item);
        break;
    case VLQT_TYPE_WHEEL_HANDLER:
        obj = create(comps[3], kWheelQml, "vlqt_wheelhandler.qml", item);
        if (obj && !props.wheelDelta.isValid())
            props.wheelDelta = metaProperty(obj, "vlqtDelta");
        break;
    case VLQT_TYPE_DRAG_HANDLER:
        obj = create(comps[4], kDragQml, "vlqt_draghandler.qml", item);
        break;
    case VLQT_TYPE_TOUCH_POINTS_HANDLER:
        obj = new TouchPointsHandler(item); // no QML type: own C++ handler (touch.h)
        break;
    case VLQT_TYPE_PINCH_HANDLER:
        obj = create(comps[5], kPinchQml, "vlqt_pinchhandler.qml", item);
        break;
    default:
        return nullptr;
    }
    // Declared on QQuickPointerHandler / QQuickSinglePointHandler: one index for every handler type.
    if (obj && !props.active.isValid())
        props.active = metaProperty(obj, "active");
    if (obj && !props.point.isValid()
        && (type == VLQT_TYPE_HOVER_HANDLER || type == VLQT_TYPE_POINT_HANDLER || type == VLQT_TYPE_WHEEL_HANDLER))
        props.point = metaProperty(obj, "point");
    return obj;
}

QObject* Handlers::createHover(QQuickItem* item, qint32 devices)
{
    QObject* obj = create(comps[0], kHoverQml, "vlqt_hoverhandler.qml", item);
    if (!obj)
        return nullptr;
    if (!props.hovered.isValid())
        props.hovered = metaProperty(obj, "hovered");
    if (!props.point.isValid())
        props.point = metaProperty(obj, "point");
    applyAccepted(obj, VLQT_PROP_ACCEPTED_DEVICES, devices);
    return obj;
}

bool Handlers::pointerPosition(QObject* handler, float out[2])
{
    const QVariant pv = props.point.read(handler);
    if (!props.position.isValid()) {
        const QMetaObject* gmo = pv.metaType().metaObject();
        const int i = gmo ? gmo->indexOfProperty("position") : -1;
        if (i < 0)
            return false;
        props.position = gmo->property(i);
    }
    const QPointF p = props.position.readOnGadget(pv.constData()).toPointF();
    out[0] = float(p.x());
    out[1] = float(p.y());
    return true;
}

bool Handlers::takeWheel(QObject* wheel, float out[2])
{
    const QPointF d = props.wheelDelta.read(wheel).toPointF();
    if (d.isNull())
        return false;
    props.wheelDelta.write(wheel, QPointF());
    out[0] = float(d.x());
    out[1] = float(d.y());
    return true;
}

void bindNode(Scene& scene, Node& node, quint32 handle)
{
    if (node.sink != handle) {
        node.sent = SentState{}; // new sink (or unbind): the next poll sends the current values again
        scene.elements.takeClicks(node); // Button clicks not yet polled belonged to the old sink
        QVariant dropped;
        scene.elements.takeUserValue(node, dropped); // same for a user change of a value input (#210)
    }
    node.sink = handle;
}

void updateTextMask(Node& node)
{
    const bool want = node.type == VLQT_TYPE_TEXT && !node.handlers.empty();
    if (want && !node.mask) {
        node.mask = new TextMask(node.item);
        node.item->setContainmentMask(node.mask);
    } else if (!want && node.mask) {
        node.item->setContainmentMask(nullptr);
        delete node.mask;
        node.mask = nullptr;
    }
}

bool isHandlerType(quint16 type)
{
    return type == VLQT_TYPE_HOVER_HANDLER || type == VLQT_TYPE_TAP_HANDLER || type == VLQT_TYPE_POINT_HANDLER
        || type == VLQT_TYPE_WHEEL_HANDLER || type == VLQT_TYPE_DRAG_HANDLER || type == VLQT_TYPE_TOUCH_POINTS_HANDLER
        || type == VLQT_TYPE_PINCH_HANDLER;
}

int reparentHandler(Scene& scene, HandlerNode& handler, quint32 handlerId, quint32 parentId, std::string& err)
{
    if (handler.owner != 0) {
        err = "input handler " + std::to_string(handlerId) + " is already attached to " + std::to_string(handler.owner)
            + " (re-attach = DESTROY + CREATE)";
        return ApplyInvalid;
    }
    if (parentId == VLQT_ROOT_ID) {
        err = "input handler " + std::to_string(handlerId) + " needs an element as parent, not the window root";
        return ApplyInvalid;
    }
    if (scene.handlerNodes.count(parentId)) {
        err = "input handler " + std::to_string(handlerId) + " needs an element as parent, " + std::to_string(parentId)
            + " is an input handler";
        return ApplyInvalid;
    }
    auto pit = scene.nodes.find(parentId);
    if (pit == scene.nodes.end()) {
        err = "unknown parent id " + std::to_string(parentId);
        return ApplyUnknownId;
    }
    Node& owner = pit->second;
    QObject* obj = scene.handlers.createHandler(handler.type, owner.item);
    if (!obj) {
        err = "input handler of type " + std::to_string(handler.type) + " could not be created (see log)";
        return ApplyInvalid;
    }
    // Properties stored before the REPARENT.
    if (handler.acceptedButtons)
        applyAccepted(obj, VLQT_PROP_ACCEPTED_BUTTONS, *handler.acceptedButtons);
    if (handler.acceptedDevices)
        applyAccepted(obj, VLQT_PROP_ACCEPTED_DEVICES, *handler.acceptedDevices);
    if (handler.type == VLQT_TYPE_TAP_HANDLER) {
        handler.tap = new TapState(obj);
        QObject::connect(obj, SIGNAL(tapped(QEventPoint, Qt::MouseButton)), handler.tap, SLOT(onTapped()));
        QObject::connect(obj, SIGNAL(longPressed()), handler.tap, SLOT(onLongPressed()));
        QObject::connect(obj, SIGNAL(pressedChanged()), handler.tap, SLOT(onPressedChanged()));
    }
    if (handler.type == VLQT_TYPE_DRAG_HANDLER) {
        applyDragAxis(obj, handler.dragAxis);
        handler.drag = new DragState(obj, scene, handlerId);
        QObject::connect(obj, SIGNAL(activeChanged()), handler.drag, SLOT(onActiveChanged()));
        QObject::connect(obj, SIGNAL(translationChanged(QVector2D)), handler.drag, SLOT(onTranslationChanged()));
    }
    if (handler.type == VLQT_TYPE_TOUCH_POINTS_HANDLER) {
        handler.points = static_cast<TouchPointsHandler*>(obj);
        handler.points->setOcclusion(handler.occlusion);
    }
    if (handler.type == VLQT_TYPE_PINCH_HANDLER) {
        if (handler.minimumPointCount)
            obj->setProperty("minimumPointCount", *handler.minimumPointCount);
        if (handler.maximumPointCount)
            obj->setProperty("maximumPointCount", *handler.maximumPointCount);
        // Internal point counter: keeps the gesture running while the finger count changes (PinchState, touch.h).
        handler.points = new TouchPointsHandler(owner.item);
        handler.points->setOcclusion(false);
        if (handler.acceptedDevices)
            applyAccepted(handler.points, VLQT_PROP_ACCEPTED_DEVICES, *handler.acceptedDevices);
        handler.pinch = new PinchState(obj, handler.points);
        QObject::connect(obj, SIGNAL(activeChanged()), handler.pinch, SLOT(onActiveChanged()));
        QObject::connect(obj, SIGNAL(scaleChanged(qreal)), handler.pinch, SLOT(onValuesChanged()));
        QObject::connect(obj, SIGNAL(rotationChanged(qreal)), handler.pinch, SLOT(onValuesChanged()));
        QObject::connect(obj, SIGNAL(translationChanged(QVector2D)), handler.pinch, SLOT(onValuesChanged()));
        QObject::connect(obj, SIGNAL(centroidChanged()), handler.pinch, SLOT(onValuesChanged()));
        QObject::connect(handler.points, SIGNAL(pointsChanged()), handler.pinch, SLOT(onPointsChanged()));
    }
    handler.object = obj;
    handler.owner = parentId;
    syncPointHover(scene, handler, owner.item);
    if (handler.type != VLQT_TYPE_POINT_HANDLER)
        raisePointHandlers(scene, owner, handlerId);
    owner.handlers.push_back(handlerId);
    updateTextMask(owner);
    return ApplyOk;
}

int setHandler(Scene& scene, HandlerNode& handler, quint16 prop, const Value& v, std::string& err)
{
    // 768 (REPORT_POSITION, Hover position) is reserved since #320: unknown like any other id.
    const quint16 t = handler.type;
    const bool drag = t == VLQT_TYPE_DRAG_HANDLER;
    const bool pinch = t == VLQT_TYPE_PINCH_HANDLER;
    const bool known = (prop == VLQT_PROP_ACCEPTED_BUTTONS && supportsButtons(t))
        || (prop == VLQT_PROP_ACCEPTED_DEVICES && supportsDevices(t))
        || (drag && (prop == VLQT_PROP_MOVES_ELEMENT || prop == VLQT_PROP_DRAG_AXIS || prop == VLQT_PROP_DRAG_LIMITS))
        || (pinch && (prop == VLQT_PROP_MINIMUM_POINT_COUNT || prop == VLQT_PROP_MAXIMUM_POINT_COUNT))
        || (prop == VLQT_PROP_OCCLUSION && t == VLQT_TYPE_TOUCH_POINTS_HANDLER);
    if (!known) {
        err = "property " + std::to_string(prop) + " is not supported by input handler type " + std::to_string(t);
        return ApplyUnknownProperty;
    }
    const quint16 expected = prop == VLQT_PROP_MOVES_ELEMENT || prop == VLQT_PROP_OCCLUSION ? quint16(VLQT_VT_BOOL)
        : prop == VLQT_PROP_DRAG_LIMITS ? quint16(VLQT_VT_RECTF) : quint16(VLQT_VT_I32);
    if (v.type != expected) {
        err = "property " + std::to_string(prop) + " expects value type " + std::to_string(int(expected)) + ", got "
            + std::to_string(v.type);
        return ApplyBadValue;
    }
    switch (prop) {
    case VLQT_PROP_MOVES_ELEMENT:
        handler.movesElement = v.b; // the current offset stays; off: the next drags only report
        return ApplyOk;
    case VLQT_PROP_DRAG_AXIS:
        if (v.i < 0 || v.i > 2) {
            err = "drag axis " + std::to_string(v.i) + " is not 0 (both), 1 (x) or 2 (y)";
            return ApplyBadValue;
        }
        handler.dragAxis = v.i;
        if (handler.object)
            applyDragAxis(handler.object, v.i);
        return ApplyOk;
    case VLQT_PROP_DRAG_LIMITS:
        // rectf slots: minX, minY, maxX, maxY (not x, y, w, h). NaN fails both comparisons.
        if (!(v.f[0] <= v.f[2]) || !(v.f[1] <= v.f[3])) {
            err = "drag limits need minimum <= maximum on both axes";
            return ApplyBadValue;
        }
        for (int i = 0; i < 4; ++i)
            handler.dragLimits[i] = v.f[i];
        return ApplyOk; // applies from the next movement on
    case VLQT_PROP_OCCLUSION:
        handler.occlusion = v.b;
        if (handler.points)
            handler.points->setOcclusion(v.b); // from the next press on
        return ApplyOk;
    case VLQT_PROP_MINIMUM_POINT_COUNT:
    case VLQT_PROP_MAXIMUM_POINT_COUNT: {
        // Not checked against each other: the host sets them one by one (Qt never activates while min > max).
        if (v.i < 2) {
            err = "point count " + std::to_string(v.i) + " of a pinch handler is less than 2";
            return ApplyBadValue;
        }
        const bool min = prop == VLQT_PROP_MINIMUM_POINT_COUNT;
        (min ? handler.minimumPointCount : handler.maximumPointCount) = v.i;
        if (handler.object && !handler.object->setProperty(min ? "minimumPointCount" : "maximumPointCount", v.i)) {
            err = "property " + std::to_string(prop) + " could not be set to " + std::to_string(v.i);
            return ApplyBadValue;
        }
        if (handler.pinch)
            handler.pinch->onPointsChanged(); // a paused gesture ends when the new minimum is not reached
        return ApplyOk;
    }
    default:
        break;
    }
    (prop == VLQT_PROP_ACCEPTED_BUTTONS ? handler.acceptedButtons : handler.acceptedDevices) = v.i;
    if (handler.object && !applyAccepted(handler.object, prop, v.i)) {
        err = "property " + std::to_string(prop) + " could not be set to " + std::to_string(v.i);
        return ApplyBadValue;
    }
    if (handler.pinch) // the internal point counter of a pinch takes the same devices
        applyAccepted(handler.points, prop, v.i);
    auto oit = scene.nodes.find(handler.owner);
    if (oit != scene.nodes.end())
        syncPointHover(scene, handler, oit->second.item);
    return ApplyOk;
}

void bindHandler(Scene& scene, HandlerNode& handler, quint32 handle)
{
    if (handle == handler.sink)
        return;
    resetSent(scene, handler);
    // What happened for the old sink stays there: a running press is cancelled, unpolled taps and motion are dropped.
    if (handler.tap)
        handler.tap->cancel(scene.handlers.isPressed(handler.object));
    float drop[2];
    if (handler.type == VLQT_TYPE_WHEEL_HANDLER && handler.object)
        scene.handlers.takeWheel(handler.object, drop);
    // The last drag belonged to the old sink; a running one goes on for the new sink.
    if (handler.drag && !scene.handlers.isActive(handler.object))
        handler.drag->delta[0] = handler.drag->delta[1] = 0;
    handler.sink = handle;
}

void destroyHandler(Scene& scene, quint32 handlerId)
{
    auto it = scene.handlerNodes.find(handlerId);
    if (it == scene.handlerNodes.end())
        return;
    HandlerNode& handler = it->second;
    resetSent(scene, handler);
    auto oit = scene.nodes.find(handler.owner);
    if (oit != scene.nodes.end()) {
        auto& list = oit->second.handlers;
        list.erase(std::remove(list.begin(), list.end(), handlerId), list.end());
        delete handler.hover;  // internal HoverHandler of a Point handler (#320), also a child of the item
        if (handler.pinch)
            delete handler.points; // internal point counter of a Pinch handler (#321); Touch Points: points == object
        delete handler.object; // QObject child of the item, deleted before it (takes the TapState along)
        updateTextMask(oit->second);
    }
    scene.handlerNodes.erase(it);
}

namespace {

void pushPointer(Scene& scene, HandlerNode& hn, QObject* source)
{
    float p[2];
    if (scene.handlers.pointerPosition(source, p) && (!hn.hasPointer || p[0] != hn.sentPointer[0] || p[1] != hn.sentPointer[1])) {
        hn.sentPointer[0] = p[0];
        hn.sentPointer[1] = p[1];
        hn.hasPointer = true;
        scene.pushEvent(VLQT_EV_POINTER, hn.sink, 0, p, quint16(sizeof p));
    }
}

void collectHover(Scene& scene, HandlerNode& hn)
{
    const bool hovered = scene.handlers.isHovered(hn.object);
    if (hovered != hn.sentHover) {
        hn.sentHover = hovered;
        pushFlag(scene, VLQT_EV_HOVER, hn.sink, hovered);
    }
}

void collectTap(Scene& scene, HandlerNode& hn)
{
    TapState& ts = *hn.tap;
    const bool pressed = scene.handlers.isPressed(hn.object) && !ts.cancelled;
    if (pressed != hn.sentPress) {
        hn.sentPress = pressed;
        pushFlag(scene, VLQT_EV_PRESS, hn.sink, pressed);
    }
    if (ts.taps) {
        const quint32 c = ts.taps;
        ts.taps = 0;
        scene.pushEvent(VLQT_EV_CLICK, hn.sink, 0, &c, quint16(sizeof c));
    }
    // Never 0 (#320): the count of the last tap stays until the next tap (a sink change starts at 0 = nothing sent).
    if (ts.tapCount != 0 && ts.tapCount != hn.sentTapCount) {
        hn.sentTapCount = ts.tapCount;
        scene.pushEvent(VLQT_EV_TAP_COUNT, hn.sink, 0, &ts.tapCount, quint16(sizeof ts.tapCount));
    }
    if (ts.longPresses) {
        const quint32 c = ts.longPresses;
        ts.longPresses = 0;
        scene.pushEvent(VLQT_EV_LONG_PRESS, hn.sink, 0, &c, quint16(sizeof c));
    }
}

void collectPoint(Scene& scene, HandlerNode& hn)
{
    // A pressed point (touch, pen, accepted mouse button) wins over the hovering mouse (ACCEPTED_BUTTONS 0, #320).
    const bool pressed = scene.handlers.isActive(hn.object);
    const bool hovered = !pressed && hn.hover && scene.handlers.isHovered(hn.hover);
    const bool active = pressed || hovered;
    if (active != hn.sentActive) {
        hn.sentActive = active;
        pushFlag(scene, VLQT_EV_ACTIVE, hn.sink, active);
    }
    if (active)
        pushPointer(scene, hn, pressed ? hn.object : hn.hover);
    else
        hn.hasPointer = false; // active again: the next poll sends the position right away
}

void collectWheel(Scene& scene, HandlerNode& hn)
{
    float d[2] = {};
    if (scene.handlers.takeWheel(hn.object, d)) {
        hn.sentWheel = true;
        scene.pushEvent(VLQT_EV_WHEEL, hn.sink, 0, d, quint16(sizeof d));
    } else if (hn.sentWheel) {
        hn.sentWheel = false; // once (0, 0) after motion
        scene.pushEvent(VLQT_EV_WHEEL, hn.sink, 0, d, quint16(sizeof d));
    }
}

void collectDrag(Scene& scene, HandlerNode& hn)
{
    const bool active = scene.handlers.isActive(hn.object);
    if (active != hn.sentActive) {
        hn.sentActive = active;
        pushFlag(scene, VLQT_EV_ACTIVE, hn.sink, active);
    }
    const float* d = hn.drag->delta;
    if (d[0] != hn.sentDrag[0] || d[1] != hn.sentDrag[1] || (hn.drag->drags != hn.sentDrags && (d[0] != 0 || d[1] != 0))) {
        hn.sentDrag[0] = d[0];
        hn.sentDrag[1] = d[1];
        hn.sentDrags = hn.drag->drags;
        scene.pushEvent(VLQT_EV_DRAG, hn.sink, 0, d, quint16(2 * sizeof(float)));
    }
}

void collectTouchPoints(Scene& scene, HandlerNode& hn)
{
    const auto& points = hn.points->points();
    const quint32 count = quint32(std::min<size_t>(points.size(), kMaxTouchPoints));
    // No point and none sent: nothing to send (a new sink starts empty).
    if (count == 0 && hn.sentTouch.size() <= 4)
        return;
    QByteArray payload;
    payload.reserve(qsizetype(4 + count * 24));
    payload.append(reinterpret_cast<const char*>(&count), 4);
    quint32 n = 0;
    for (const auto& [id, p] : points) {
        if (n++ == count)
            break;
        const quint32 uid = id;
        const float f[5] = {float(p.pos.x()), float(p.pos.y()), p.pressure, float(p.size.width()), float(p.size.height())};
        payload.append(reinterpret_cast<const char*>(&uid), 4);
        payload.append(reinterpret_cast<const char*>(f), sizeof f);
    }
    if (payload == hn.sentTouch)
        return;
    hn.sentTouch = payload;
    scene.pushEvent(VLQT_EV_TOUCH_POINTS, hn.sink, 0, payload.constData(), quint16(payload.size()));
}

void collectPinch(Scene& scene, HandlerNode& hn)
{
    const PinchState& ps = *hn.pinch;
    // Gesture start: ACTIVE 1, then the values; end: the last values (if not sent yet), then ACTIVE 0 (#321 (e)).
    if (ps.running && !hn.sentActive)
        pushFlag(scene, VLQT_EV_ACTIVE, hn.sink, true);
    const float* v = ps.value;
    const bool changed = v[0] != hn.sentPinch[0] || v[1] != hn.sentPinch[1] || v[2] != hn.sentPinch[2] || v[3] != hn.sentPinch[3];
    if ((ps.running || hn.sentActive) && (changed || ps.gestures != hn.sentGestures)) {
        for (int i = 0; i < 4; ++i)
            hn.sentPinch[i] = v[i];
        hn.sentGestures = ps.gestures;
        scene.pushEvent(VLQT_EV_PINCH, hn.sink, 0, v, quint16(4 * sizeof(float)));
    }
    if (!ps.running && hn.sentActive)
        pushFlag(scene, VLQT_EV_ACTIVE, hn.sink, false);
    hn.sentActive = ps.running;
}

/* Events of the input handlers (#319, #320, #209, #321), only while bound. */
void collectHandlers(Scene& scene)
{
    for (auto& kv : scene.handlerNodes) {
        HandlerNode& hn = kv.second;
        if (hn.sink == 0 || !hn.object)
            continue;
        switch (hn.type) {
        case VLQT_TYPE_HOVER_HANDLER: collectHover(scene, hn); break;
        case VLQT_TYPE_TAP_HANDLER: collectTap(scene, hn); break;
        case VLQT_TYPE_POINT_HANDLER: collectPoint(scene, hn); break;
        case VLQT_TYPE_WHEEL_HANDLER: collectWheel(scene, hn); break;
        case VLQT_TYPE_DRAG_HANDLER: collectDrag(scene, hn); break;
        case VLQT_TYPE_TOUCH_POINTS_HANDLER: collectTouchPoints(scene, hn); break;
        case VLQT_TYPE_PINCH_HANDLER: collectPinch(scene, hn); break;
        default: break;
        }
    }
}

/* EV_VALUE (#210): u16 value type, u16 0, value encoded like SET (i32, f32, bool u8, string u32 len + UTF-8). The
 * value type follows the QML type of vlqtUserValue (int, real, bool, string); others are not sent. Strings are capped
 * at kMaxValueString bytes on a UTF-8 character boundary (the record length is u16). Only on user changes, so the
 * string copy does not allocate per frame. */
constexpr qsizetype kMaxValueString = 60000;

void pushValue(Scene& scene, quint32 sink, const QVariant& v)
{
    quint16 vt = 0;
    switch (v.typeId()) {
    case QMetaType::Int: vt = VLQT_VT_I32; break;
    case QMetaType::Double:
    case QMetaType::Float: vt = VLQT_VT_F32; break;
    case QMetaType::Bool: vt = VLQT_VT_BOOL; break;
    case QMetaType::QString: vt = VLQT_VT_STRING; break;
    default: return;
    }
    if (vt != VLQT_VT_STRING) {
        unsigned char buf[8] = {};
        std::memcpy(buf, &vt, 2);
        quint16 len = 4;
        if (vt == VLQT_VT_I32) {
            const qint32 i = v.toInt();
            std::memcpy(buf + 4, &i, 4);
            len += 4;
        } else if (vt == VLQT_VT_F32) {
            const float f = float(v.toDouble());
            std::memcpy(buf + 4, &f, 4);
            len += 4;
        } else {
            buf[4] = v.toBool() ? 1 : 0;
            len += 1;
        }
        scene.pushEvent(VLQT_EV_VALUE, sink, 0, buf, len);
        return;
    }
    const QByteArray utf8 = v.toString().toUtf8();
    qsizetype n = utf8.size();
    if (n > kMaxValueString) {
        n = kMaxValueString;
        while (n > 0 && (quint8(utf8[n]) & 0xC0) == 0x80) // utf8[n] is the first byte cut off: not inside a character
            --n;
    }
    QByteArray payload(8 + n, Qt::Uninitialized);
    const quint16 zero = 0;
    const quint32 n32 = quint32(n);
    std::memcpy(payload.data(), &vt, 2);
    std::memcpy(payload.data() + 2, &zero, 2);
    std::memcpy(payload.data() + 4, &n32, 4);
    std::memcpy(payload.data() + 8, utf8.constData(), size_t(n));
    scene.pushEvent(VLQT_EV_VALUE, sink, 0, payload.constData(), quint16(payload.size()));
}

} // namespace

void collectMeasurements(Scene& scene)
{
    for (auto& kv : scene.nodes) {
        Node& n = kv.second;
        if (n.sink == 0)
            continue;
        SentState& s = n.sent;
        const QQuickItem* item = n.item;

        const float m[2] = {float(item->implicitWidth()), float(item->implicitHeight())};
        if (!s.hasMeasured || m[0] != s.measured[0] || m[1] != s.measured[1]) {
            s.measured[0] = m[0];
            s.measured[1] = m[1];
            s.hasMeasured = true;
            scene.pushEvent(VLQT_EV_MEASURED, n.sink, 0, m, quint16(sizeof m));
        }

        // Item geometry (frame, #469: an auto-sized Group may start in negative coordinates), not boundingRect():
        // QQuickText overrides that with the painted text rect.
        const QRectF r = item->mapRectToScene(n.xf ? itemFrame(*n.xf, item) : QRectF(0, 0, item->width(), item->height()));
        const float b[4] = {float(r.x()), float(r.y()), float(r.width()), float(r.height())};
        if (!s.hasBounds || b[0] != s.bounds[0] || b[1] != s.bounds[1] || b[2] != s.bounds[2] || b[3] != s.bounds[3]) {
            for (int i = 0; i < 4; ++i)
                s.bounds[i] = b[i];
            s.hasBounds = true;
            scene.pushEvent(VLQT_EV_SCENE_BOUNDS, n.sink, 0, b, quint16(sizeof b));
        }

        float md[2];
        if (scene.elements.readMedia(n, md) && (!s.hasMedia || md[0] != s.media[0] || md[1] != s.media[1])) {
            s.media[0] = md[0];
            s.media[1] = md[1];
            s.hasMedia = true;
            scene.pushEvent(VLQT_EV_MEDIA, n.sink, 0, md, quint16(sizeof md));
        }

        // Error text (#273): only on change; empty = cleared. Capped (record length is u16).
        const QString error = scene.elements.readError(n);
        if (error != s.error) {
            s.error = error;
            const QByteArray utf8 = error.toUtf8();
            scene.pushEvent(VLQT_EV_ERROR, n.sink, 0, utf8.constData(), quint16(qMin<qsizetype>(utf8.size(), 4096)));
        }

        // Button (#213): clicked signals, always on (Bang output).
        if (const quint32 bc = scene.elements.takeClicks(n))
            scene.pushEvent(VLQT_EV_CLICK, n.sink, 0, &bc, quint16(sizeof bc));

        // Value inputs (#210): the value of the last user change, captured at the signal (protocol.h EV_VALUE).
        QVariant uv;
        if (scene.elements.takeUserValue(n, uv))
            pushValue(scene, n.sink, uv);
    }
    collectHandlers(scene);
}

void watchWindowGeometry(Scene& scene)
{
    QQuickWindow* w = scene.window;
    QObject* ctx = &scene.geometryWatch;
    auto dirty = [&scene] { scene.geometryDirty = true; };
    QObject::connect(w, &QWindow::xChanged, ctx, dirty);
    QObject::connect(w, &QWindow::yChanged, ctx, dirty);
    QObject::connect(w, &QWindow::widthChanged, ctx, dirty);
    QObject::connect(w, &QWindow::heightChanged, ctx, dirty);
    QObject::connect(w, &QWindow::windowStateChanged, ctx, dirty);
    QObject::connect(w, &QWindow::screenChanged, ctx, dirty); // frame margins / DIP mapping may change
    QObject::connect(w, &QWindow::visibleChanged, ctx, dirty);
}

void collectWindowGeometry(Scene& scene)
{
    QQuickWindow* w = scene.window;
    // Same convention as vlqt_window_create_at: outer top-left (frame position), client size, DIP.
    // Fullscreen has no frame, but Qt (Windows) keeps reporting the old frame margins: client position (#234).
    const Qt::WindowStates st = w->windowStates();
    const QPoint pos = st.testFlag(Qt::WindowFullScreen) ? w->position() : w->framePosition();
    if (!scene.geometryDirty && pos == scene.reportedFramePos)
        return;
    scene.geometryDirty = false;
    scene.reportedFramePos = pos;
    const QPoint client = w->position();
    struct { float x, y, w, h; quint32 flags; float cx, cy; } g{
        float(pos.x()), float(pos.y()), float(w->width()), float(w->height()),
        (st.testFlag(Qt::WindowMinimized) ? quint32(VLQT_WINDOW_MINIMIZED) : 0u)
            | (st.testFlag(Qt::WindowMaximized) ? quint32(VLQT_WINDOW_MAXIMIZED) : 0u)
            | (st.testFlag(Qt::WindowFullScreen) ? quint32(VLQT_WINDOW_FULLSCREEN) : 0u)
            | (w->isVisible() ? quint32(VLQT_WINDOW_VISIBLE) : 0u),
        float(client.x()), float(client.y())};
    static_assert(sizeof g == 28, "EV_WINDOW_GEOMETRY payload");
    scene.pushEvent(VLQT_EV_WINDOW_GEOMETRY, 0, 0, &g, quint16(sizeof g));
}

} // namespace vlqt
