#include "scene/handlers.h"
#include "scene/scene.h"
#include "scene/touch_handler.h"

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

// The handler QML files (qml/handlers/*Element.qml) hold the comments on how each handler is set up.
constexpr const char* kHoverUrl = "qrc:/vlqt/qml/handlers/HoverHandlerElement.qml";
constexpr const char* kTapUrl = "qrc:/vlqt/qml/handlers/TapHandlerElement.qml";
constexpr const char* kPointUrl = "qrc:/vlqt/qml/handlers/PointHandlerElement.qml";
constexpr const char* kWheelUrl = "qrc:/vlqt/qml/handlers/WheelHandlerElement.qml";
constexpr const char* kDragUrl = "qrc:/vlqt/qml/handlers/DragHandlerElement.qml";
constexpr const char* kPinchUrl = "qrc:/vlqt/qml/handlers/PinchHandlerElement.qml";
// EVENT_TOUCH_POINTS: at most this many points (a count of fingers, not a length limit).
constexpr int kMaxTouchPoints = 64;

QMetaProperty metaProperty(const QObject* o, const char* name)
{
    const QMetaObject* mo = o->metaObject();
    const int i = mo->indexOfProperty(name);
    return i >= 0 ? mo->property(i) : QMetaProperty();
}

/* The sink loses the handler (new handle, DESTROY): flags that were 1 go back to 0, wheel motion ends with (0, 0).
   Forgets the sent state, so a new sink gets the current state with the next poll. */
void resetSent(Scene& scene, HandlerEntry& hn)
{
    const HandlerSent& sent = hn.sent;
    if (hn.feedbackId != 0) {
        if (sent.hover)
            scene.events.pushFlag(VLQT_EVENT_HOVER, hn.feedbackId, false);
        if (sent.press)
            scene.events.pushFlag(VLQT_EVENT_PRESS, hn.feedbackId, false);
        if (sent.active)
            scene.events.pushFlag(VLQT_EVENT_ACTIVE, hn.feedbackId, false);
        if (sent.wheel) {
            const float zero[2] = {};
            scene.events.push(VLQT_EVENT_WHEEL, hn.feedbackId, zero, sizeof zero);
        }
        if (sent.touch.size() > 4) { // points were sent: the old sink sees them go
            const quint32 zero = 0;
            scene.events.push(VLQT_EVENT_TOUCH_POINTS, hn.feedbackId, &zero, sizeof zero);
        }
    }
    hn.sent = {};
}

/* The TouchPointsHandler of a Touch Points handler (its object, no QML type), else nullptr. */
TouchPointsHandler* touchPoints(const HandlerEntry& hn)
{
    return hn.type == VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER ? static_cast<TouchPointsHandler*>(hn.objects.object.data()) : nullptr;
}

/* Bit of an input-handler type (VLQT_ELEMENT_TYPE_*_HANDLER) in HandlerProp::types. */
constexpr quint32 handlerBit(int type)
{
    return 1u << (type - VLQT_ELEMENT_TYPE_HOVER_HANDLER);
}
static_assert(VLQT_ELEMENT_TYPE_DRAG_HANDLER - VLQT_ELEMENT_TYPE_HOVER_HANDLER < 32, "handler types fit HandlerProp::types");

/* The properties of the input handlers: one row per property. setHandler checks a SET against this table (handler
 * type, value type), then applies it in one switch. A new property adds its id to protocol.h and Protocol.cs, one row
 * here and its case in setHandler. */
struct HandlerProp {
    quint16 prop;
    quint16 valueType;
    quint32 types; // handlerBit of every handler type that takes it
};

constexpr quint32 kButtonHandlers = handlerBit(VLQT_ELEMENT_TYPE_TAP_HANDLER) | handlerBit(VLQT_ELEMENT_TYPE_POINT_HANDLER)
    | handlerBit(VLQT_ELEMENT_TYPE_DRAG_HANDLER);
constexpr quint32 kDrag = handlerBit(VLQT_ELEMENT_TYPE_DRAG_HANDLER);
constexpr quint32 kPinch = handlerBit(VLQT_ELEMENT_TYPE_PINCH_HANDLER);

// 768 (the former REPORT_POSITION of the Hover handler) is reserved: unknown like any other id.
constexpr HandlerProp kHandlerProps[] = {
    {VLQT_PROPERTY_ACCEPTED_BUTTONS, VLQT_VALUE_TYPE_I32, kButtonHandlers},
    {VLQT_PROPERTY_ACCEPTED_DEVICES, VLQT_VALUE_TYPE_I32, kButtonHandlers | kPinch | handlerBit(VLQT_ELEMENT_TYPE_WHEEL_HANDLER)
            | handlerBit(VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER)},
    {VLQT_PROPERTY_MOVES_ELEMENT, VLQT_VALUE_TYPE_BOOL, kDrag},
    {VLQT_PROPERTY_DRAG_AXIS, VLQT_VALUE_TYPE_I32, kDrag},
    {VLQT_PROPERTY_DRAG_LIMITS, VLQT_VALUE_TYPE_RECTF, kDrag},
    {VLQT_PROPERTY_MINIMUM_POINT_COUNT, VLQT_VALUE_TYPE_I32, kPinch},
    {VLQT_PROPERTY_MAXIMUM_POINT_COUNT, VLQT_VALUE_TYPE_I32, kPinch},
    {VLQT_PROPERTY_OCCLUSION, VLQT_VALUE_TYPE_BOOL, handlerBit(VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER)},
};

/* The row of prop if handler type takes it, else nullptr. */
const HandlerProp* findHandlerProp(quint16 prop, quint16 type)
{
    for (const HandlerProp& p : kHandlerProps)
        if (p.prop == prop)
            return (p.types & handlerBit(type)) ? &p : nullptr;
    return nullptr;
}

/* DRAG_AXIS on the QML handler: xAxis/yAxis.enabled. Qt then also ignores a drag that runs more than 45 degrees off
   the enabled axis, so a parent's drag handler can take it (see scene/handlers.h). */
void applyDragAxis(QObject* obj, qint32 axis)
{
    QObject* x = obj->property("xAxis").value<QObject*>();
    QObject* y = obj->property("yAxis").value<QObject*>();
    if (!x || !y || !x->setProperty("enabled", axis != 2) || !y->setProperty("enabled", axis != 1))
        qCWarning(lcEvents, "drag handler axis could not be set to %d", int(axis));
}

/* MINIMUM_POINT_COUNT / MAXIMUM_POINT_COUNT on the QML PinchHandler. */
bool applyPointCount(QObject* obj, quint16 prop, qint32 v)
{
    const bool ok = obj->setProperty(prop == VLQT_PROPERTY_MINIMUM_POINT_COUNT ? "minimumPointCount" : "maximumPointCount", v);
    if (!ok)
        qCWarning(lcEvents, "input handler property %d could not be set to %d", int(prop), int(v));
    return ok;
}

/* ACCEPTED_BUTTONS / ACCEPTED_DEVICES on the QML handler (QFlags properties need their exact type in the QVariant). */
bool applyAccepted(QObject* obj, quint16 prop, qint32 v)
{
    const bool ok = prop == VLQT_PROPERTY_ACCEPTED_BUTTONS
        ? obj->setProperty("acceptedButtons", QVariant::fromValue(Qt::MouseButtons(v)))
        : obj->setProperty("acceptedDevices", QVariant::fromValue(QInputDevice::DeviceTypes(v)));
    if (!ok)
        qCWarning(lcEvents, "input handler property %d could not be set to %d", int(prop), int(v));
    return ok;
}

/* Point handler with ACCEPTED_BUTTONS 0: an internal HoverHandler makes the mouse (and touchpad cursor) of
   ACCEPTED_DEVICES active while it is over the item; the PointHandler itself then only takes touch and pen. Creates,
   updates or removes it after a change of the item, the buttons or the devices. */
void syncPointHover(Scene& scene, HandlerEntry& hn, QQuickItem* item)
{
    HandlerObjects& qt = hn.objects;
    if (hn.type != VLQT_ELEMENT_TYPE_POINT_HANDLER || !qt.object)
        return;
    constexpr qint32 cursorDevices = qint32(QInputDevice::DeviceType::Mouse) | qint32(QInputDevice::DeviceType::TouchPad);
    const qint32 devices = (hn.config.acceptedDevices ? *hn.config.acceptedDevices : qint32(QInputDevice::DeviceType::AllDevices))
        & cursorDevices;
    const bool want = hn.config.acceptedButtons && *hn.config.acceptedButtons == 0 && devices != 0;
    if (!want)
        delete qt.hover.data(); // the QPointer reads null; the next poll sends ACTIVE 0 if only the hover made the handler active
    else if (!qt.hover)
        qt.hover = scene.handlers.createHover(item, devices);
    else
        applyAccepted(qt.hover, VLQT_PROPERTY_ACCEPTED_DEVICES, devices);
}

/* Point handlers first (manual test): Qt delivers to the handlers of an item from the last attached to the first
   (QQuickItemPrivate::addPointerHandler prepends), and a PointHandler ignores a mouse point that another handler already
   grabs exclusively. A Tap handler attached after a Point handler took the left press first (exclusive grab, button
   semantics) and the Point handler stayed inactive. So the Point handlers of the item are attached again after any
   other handler: they take their passive grab first, the Tap handler still grabs exclusively and taps. */
void raisePointHandlers(Scene& scene, const ElementEntry& owner, quint32 except)
{
    for (quint32 id : owner.handlers) {
        auto it = scene.handlerNodes.find(id);
        if (id == except || it == scene.handlerNodes.end() || it->second.type != VLQT_ELEMENT_TYPE_POINT_HANDLER || !it->second.objects.object)
            continue;
        QObject* obj = it->second.objects.object;
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
    const ElementEntry& node = nit->second;
    // Several drag handlers at one item share its one offset: each starts from the offset as it is now.
    start_[0] = node.xf->dx;
    start_[1] = node.xf->dy;
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
    const HandlerConfig& config = hit->second.config;
    ElementEntry& node = nit->second;

    // activeTranslation is the centroid's movement in scene (window) coordinates; the offset lives in the parent's
    // space (applied after the item's own TRANSFORM), so map the vector into the parent.
    const QPointF t = drag_->property("activeTranslation").value<QVector2D>().toPointF();
    QPointF d = t;
    if (const QQuickItem* parent = node.item->parentItem())
        d = parent->mapFromScene(t) - parent->mapFromScene(QPointF());
    if (config.dragAxis == 2)
        d.setX(0);
    if (config.dragAxis == 1)
        d.setY(0);

    // SET TRANSFORM during the drag (pin wins): the offset went to 0, the drag goes on from there.
    if (node.transformEpoch != epoch_) {
        epoch_ = node.transformEpoch;
        start_[0] = -raw_[0];
        start_[1] = -raw_[1];
    }
    raw_[0] = float(d.x());
    raw_[1] = float(d.y());
    const float ox = std::clamp(start_[0] + raw_[0], config.dragLimits[0], config.dragLimits[2]);
    const float oy = std::clamp(start_[1] + raw_[1], config.dragLimits[1], config.dragLimits[3]);
    delta[0] = ox - start_[0];
    delta[1] = oy - start_[1];
    if (config.movesElement)
        scene_.elements.setDragOffset(node, ox, oy);
}

HandlerObjects::~HandlerObjects()
{
    release();
}

void HandlerObjects::release()
{
    delete object.data();
    delete pinchTracker.data();
    delete hover.data();
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

QObject* Handlers::create(QQmlComponent*& comp, const char* url, QQuickItem* item)
{
    if (!comp)
        comp = new QQmlComponent(engine, QUrl(QString::fromLatin1(url)));
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
    case VLQT_ELEMENT_TYPE_HOVER_HANDLER:
        obj = create(component(HandlerComponent::Hover), kHoverUrl, item);
        if (obj && !props.hovered.isValid())
            props.hovered = metaProperty(obj, "hovered");
        break;
    case VLQT_ELEMENT_TYPE_TAP_HANDLER:
        obj = create(component(HandlerComponent::Tap), kTapUrl, item);
        if (obj && !props.pressed.isValid())
            props.pressed = metaProperty(obj, "pressed");
        break;
    case VLQT_ELEMENT_TYPE_POINT_HANDLER:
        obj = create(component(HandlerComponent::Point), kPointUrl, item);
        break;
    case VLQT_ELEMENT_TYPE_WHEEL_HANDLER:
        obj = create(component(HandlerComponent::Wheel), kWheelUrl, item);
        if (obj && !props.wheelDelta.isValid())
            props.wheelDelta = metaProperty(obj, "vlqtDelta");
        break;
    case VLQT_ELEMENT_TYPE_DRAG_HANDLER:
        obj = create(component(HandlerComponent::Drag), kDragUrl, item);
        break;
    case VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER:
        obj = new TouchPointsHandler(item); // no QML type: own C++ handler (scene/touch_handler.h)
        break;
    case VLQT_ELEMENT_TYPE_PINCH_HANDLER:
        obj = create(component(HandlerComponent::Pinch), kPinchUrl, item);
        break;
    default:
        return nullptr;
    }
    // Declared on QQuickPointerHandler / QQuickSinglePointHandler: one index for every handler type.
    if (obj && !props.active.isValid())
        props.active = metaProperty(obj, "active");
    if (obj && !props.point.isValid()
        && (type == VLQT_ELEMENT_TYPE_HOVER_HANDLER || type == VLQT_ELEMENT_TYPE_POINT_HANDLER || type == VLQT_ELEMENT_TYPE_WHEEL_HANDLER))
        props.point = metaProperty(obj, "point");
    return obj;
}

QObject* Handlers::createHover(QQuickItem* item, qint32 devices)
{
    QObject* obj = create(component(HandlerComponent::Hover), kHoverUrl, item);
    if (!obj)
        return nullptr;
    if (!props.hovered.isValid())
        props.hovered = metaProperty(obj, "hovered");
    if (!props.point.isValid())
        props.point = metaProperty(obj, "point");
    applyAccepted(obj, VLQT_PROPERTY_ACCEPTED_DEVICES, devices);
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

void updateTextMask(ElementEntry& node)
{
    const bool want = node.type == VLQT_ELEMENT_TYPE_TEXT && !node.handlers.empty();
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
    return type == VLQT_ELEMENT_TYPE_HOVER_HANDLER || type == VLQT_ELEMENT_TYPE_TAP_HANDLER || type == VLQT_ELEMENT_TYPE_POINT_HANDLER
        || type == VLQT_ELEMENT_TYPE_WHEEL_HANDLER || type == VLQT_ELEMENT_TYPE_DRAG_HANDLER || type == VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER
        || type == VLQT_ELEMENT_TYPE_PINCH_HANDLER;
}

int reparentHandler(Scene& scene, HandlerEntry& handler, quint32 handlerId, quint32 parentId, std::string& err)
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
    ElementEntry& owner = pit->second;
    QObject* obj = scene.handlers.createHandler(handler.type, owner.item);
    if (!obj) {
        err = "input handler of type " + std::to_string(handler.type) + " could not be created (see log)";
        return ApplyInvalid;
    }
    // Properties stored before the REPARENT.
    const HandlerConfig& config = handler.config;
    HandlerObjects& qt = handler.objects;
    if (config.acceptedButtons)
        applyAccepted(obj, VLQT_PROPERTY_ACCEPTED_BUTTONS, *config.acceptedButtons);
    if (config.acceptedDevices)
        applyAccepted(obj, VLQT_PROPERTY_ACCEPTED_DEVICES, *config.acceptedDevices);
    if (handler.type == VLQT_ELEMENT_TYPE_TAP_HANDLER) {
        auto* tap = new TapState(obj);
        QObject::connect(obj, SIGNAL(tapped(QEventPoint, Qt::MouseButton)), tap, SLOT(onTapped()));
        QObject::connect(obj, SIGNAL(longPressed()), tap, SLOT(onLongPressed()));
        QObject::connect(obj, SIGNAL(pressedChanged()), tap, SLOT(onPressedChanged()));
        qt.tap = tap;
    }
    if (handler.type == VLQT_ELEMENT_TYPE_DRAG_HANDLER) {
        applyDragAxis(obj, config.dragAxis);
        auto* drag = new DragState(obj, scene, handlerId);
        QObject::connect(obj, SIGNAL(activeChanged()), drag, SLOT(onActiveChanged()));
        QObject::connect(obj, SIGNAL(translationChanged(QVector2D)), drag, SLOT(onTranslationChanged()));
        qt.drag = drag;
    }
    if (handler.type == VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER)
        static_cast<TouchPointsHandler*>(obj)->setOcclusion(config.occlusion);
    if (handler.type == VLQT_ELEMENT_TYPE_PINCH_HANDLER) {
        if (config.minimumPointCount)
            applyPointCount(obj, VLQT_PROPERTY_MINIMUM_POINT_COUNT, *config.minimumPointCount);
        if (config.maximumPointCount)
            applyPointCount(obj, VLQT_PROPERTY_MAXIMUM_POINT_COUNT, *config.maximumPointCount);
        // Internal point counter: keeps the gesture running while the finger count changes (PinchState, scene/touch_handler.h).
        auto* tracker = new TouchPointsHandler(owner.item);
        tracker->setOcclusion(false);
        if (config.acceptedDevices)
            applyAccepted(tracker, VLQT_PROPERTY_ACCEPTED_DEVICES, *config.acceptedDevices);
        auto* pinch = new PinchState(obj, tracker);
        QObject::connect(obj, SIGNAL(activeChanged()), pinch, SLOT(onActiveChanged()));
        QObject::connect(obj, SIGNAL(scaleChanged(qreal)), pinch, SLOT(onValuesChanged()));
        QObject::connect(obj, SIGNAL(rotationChanged(qreal)), pinch, SLOT(onValuesChanged()));
        QObject::connect(obj, SIGNAL(translationChanged(QVector2D)), pinch, SLOT(onValuesChanged()));
        QObject::connect(obj, SIGNAL(centroidChanged()), pinch, SLOT(onValuesChanged()));
        QObject::connect(tracker, SIGNAL(pointsChanged()), pinch, SLOT(onPointsChanged()));
        qt.pinchTracker = tracker;
        qt.pinch = pinch;
    }
    qt.object = obj;
    handler.owner = parentId;
    syncPointHover(scene, handler, owner.item);
    if (handler.type != VLQT_ELEMENT_TYPE_POINT_HANDLER)
        raisePointHandlers(scene, owner, handlerId);
    owner.handlers.push_back(handlerId);
    updateTextMask(owner);
    return ApplyOk;
}

int setHandler(Scene& scene, HandlerEntry& handler, quint16 prop, const Value& v, std::string& err)
{
    // A value goes to the Qt object first (when there is one) and is stored only when Qt accepted it: a SET that fails
    // leaves the stored value as it was, so the next REPARENT does not apply the rejected one.
    const HandlerProp* row = findHandlerProp(prop, handler.type);
    if (!row) {
        err = "property " + std::to_string(prop) + " is not supported by input handler type " + std::to_string(handler.type);
        return ApplyUnknownProperty;
    }
    if (v.type != row->valueType) {
        err = "property " + std::to_string(prop) + " expects value type " + std::to_string(int(row->valueType)) + ", got "
            + std::to_string(v.type);
        return ApplyBadValue;
    }
    HandlerConfig& config = handler.config;
    const HandlerObjects& qt = handler.objects;
    switch (prop) {
    case VLQT_PROPERTY_MOVES_ELEMENT:
        config.movesElement = v.b; // the current offset stays; off: the next drags only report
        return ApplyOk;
    case VLQT_PROPERTY_DRAG_AXIS:
        if (v.i < 0 || v.i > 2) {
            err = "drag axis " + std::to_string(v.i) + " is not 0 (both), 1 (x) or 2 (y)";
            return ApplyBadValue;
        }
        config.dragAxis = v.i;
        if (qt.object)
            applyDragAxis(qt.object, v.i);
        return ApplyOk;
    case VLQT_PROPERTY_DRAG_LIMITS:
        // rectf slots: minX, minY, maxX, maxY (not x, y, w, h). NaN fails both comparisons.
        if (!(v.f[0] <= v.f[2]) || !(v.f[1] <= v.f[3])) {
            err = "drag limits need minimum <= maximum on both axes";
            return ApplyBadValue;
        }
        for (int i = 0; i < 4; ++i)
            config.dragLimits[i] = v.f[i];
        return ApplyOk; // applies from the next movement on
    case VLQT_PROPERTY_OCCLUSION:
        config.occlusion = v.b;
        if (TouchPointsHandler* points = touchPoints(handler))
            points->setOcclusion(v.b); // from the next press on
        return ApplyOk;
    case VLQT_PROPERTY_MINIMUM_POINT_COUNT:
    case VLQT_PROPERTY_MAXIMUM_POINT_COUNT: {
        // Not checked against each other: the host sets them one by one (Qt never activates while min > max).
        if (v.i < 2) {
            err = "point count " + std::to_string(v.i) + " of a pinch handler is less than 2";
            return ApplyBadValue;
        }
        if (qt.object && !applyPointCount(qt.object, prop, v.i)) {
            err = "property " + std::to_string(prop) + " could not be set to " + std::to_string(v.i);
            return ApplyBadValue;
        }
        (prop == VLQT_PROPERTY_MINIMUM_POINT_COUNT ? config.minimumPointCount : config.maximumPointCount) = v.i;
        if (qt.pinch)
            qt.pinch->onPointsChanged(); // a paused gesture ends when the new minimum is not reached
        return ApplyOk;
    }
    default:
        break;
    }
    // ACCEPTED_BUTTONS, ACCEPTED_DEVICES
    if (qt.object && !applyAccepted(qt.object, prop, v.i)) {
        err = "property " + std::to_string(prop) + " could not be set to " + std::to_string(v.i);
        return ApplyBadValue;
    }
    (prop == VLQT_PROPERTY_ACCEPTED_BUTTONS ? config.acceptedButtons : config.acceptedDevices) = v.i;
    if (qt.pinchTracker) // the internal point counter of a pinch takes the same devices
        applyAccepted(qt.pinchTracker, prop, v.i);
    auto oit = scene.nodes.find(handler.owner);
    if (oit != scene.nodes.end())
        syncPointHover(scene, handler, oit->second.item);
    return ApplyOk;
}

void bindHandler(Scene& scene, HandlerEntry& handler, quint32 handle)
{
    if (handle == handler.feedbackId)
        return;
    resetSent(scene, handler);
    // What happened for the old sink stays there: a running press is cancelled, unpolled taps and motion are dropped.
    const HandlerObjects& qt = handler.objects;
    if (qt.tap)
        qt.tap->cancel(scene.handlers.isPressed(qt.object));
    float drop[2];
    if (handler.type == VLQT_ELEMENT_TYPE_WHEEL_HANDLER && qt.object)
        scene.handlers.takeWheel(qt.object, drop);
    // The last drag belonged to the old sink; a running one goes on for the new sink.
    if (qt.drag && !scene.handlers.isActive(qt.object))
        qt.drag->delta[0] = qt.drag->delta[1] = 0;
    handler.feedbackId = handle;
}

void destroyHandler(Scene& scene, quint32 handlerId)
{
    auto it = scene.handlerNodes.find(handlerId);
    if (it == scene.handlerNodes.end())
        return;
    HandlerEntry& handler = it->second;
    resetSent(scene, handler);
    handler.objects.release();
    auto oit = scene.nodes.find(handler.owner);
    if (oit != scene.nodes.end()) {
        auto& list = oit->second.handlers;
        list.erase(std::remove(list.begin(), list.end(), handlerId), list.end());
        updateTextMask(oit->second);
    }
    scene.handlerNodes.erase(it);
}

namespace {

void pushPointer(Scene& scene, HandlerEntry& hn, QObject* source)
{
    HandlerSent& sent = hn.sent;
    float p[2];
    if (scene.handlers.pointerPosition(source, p) && (!sent.hasPointer || p[0] != sent.pointer[0] || p[1] != sent.pointer[1])) {
        sent.pointer[0] = p[0];
        sent.pointer[1] = p[1];
        sent.hasPointer = true;
        scene.events.push(VLQT_EVENT_POINTER, hn.feedbackId, p, sizeof p);
    }
}

void collectHover(Scene& scene, HandlerEntry& hn)
{
    const bool hovered = scene.handlers.isHovered(hn.objects.object);
    if (hovered != hn.sent.hover) {
        hn.sent.hover = hovered;
        scene.events.pushFlag(VLQT_EVENT_HOVER, hn.feedbackId, hovered);
    }
}

void collectTap(Scene& scene, HandlerEntry& hn)
{
    TapState& ts = *hn.objects.tap;
    HandlerSent& sent = hn.sent;
    const bool pressed = scene.handlers.isPressed(hn.objects.object) && !ts.cancelled;
    if (pressed != sent.press) {
        sent.press = pressed;
        scene.events.pushFlag(VLQT_EVENT_PRESS, hn.feedbackId, pressed);
    }
    if (ts.taps) {
        const quint32 c = ts.taps;
        ts.taps = 0;
        scene.events.push(VLQT_EVENT_CLICK, hn.feedbackId, &c, sizeof c);
    }
    // Never 0: the count of the last tap stays until the next tap (a sink change starts at 0 = nothing sent).
    if (ts.tapCount != 0 && ts.tapCount != sent.tapCount) {
        sent.tapCount = ts.tapCount;
        scene.events.push(VLQT_EVENT_TAP_COUNT, hn.feedbackId, &ts.tapCount, sizeof ts.tapCount);
    }
    if (ts.longPresses) {
        const quint32 c = ts.longPresses;
        ts.longPresses = 0;
        scene.events.push(VLQT_EVENT_LONG_PRESS, hn.feedbackId, &c, sizeof c);
    }
}

void collectPoint(Scene& scene, HandlerEntry& hn)
{
    // A pressed point (touch, pen, accepted mouse button) wins over the hovering mouse (ACCEPTED_BUTTONS 0).
    const HandlerObjects& qt = hn.objects;
    HandlerSent& sent = hn.sent;
    const bool pressed = scene.handlers.isActive(qt.object);
    const bool hovered = !pressed && qt.hover && scene.handlers.isHovered(qt.hover);
    const bool active = pressed || hovered;
    if (active != sent.active) {
        sent.active = active;
        scene.events.pushFlag(VLQT_EVENT_ACTIVE, hn.feedbackId, active);
    }
    if (active)
        pushPointer(scene, hn, pressed ? qt.object : qt.hover);
    else
        sent.hasPointer = false; // active again: the next poll sends the position right away
}

void collectWheel(Scene& scene, HandlerEntry& hn)
{
    float d[2] = {};
    if (scene.handlers.takeWheel(hn.objects.object, d)) {
        hn.sent.wheel = true;
        scene.events.push(VLQT_EVENT_WHEEL, hn.feedbackId, d, sizeof d);
    } else if (hn.sent.wheel) {
        hn.sent.wheel = false; // once (0, 0) after motion
        scene.events.push(VLQT_EVENT_WHEEL, hn.feedbackId, d, sizeof d);
    }
}

void collectDrag(Scene& scene, HandlerEntry& hn)
{
    HandlerSent& sent = hn.sent;
    const bool active = scene.handlers.isActive(hn.objects.object);
    if (active != sent.active) {
        sent.active = active;
        scene.events.pushFlag(VLQT_EVENT_ACTIVE, hn.feedbackId, active);
    }
    const DragState& ds = *hn.objects.drag;
    const float* d = ds.delta;
    if (d[0] != sent.drag[0] || d[1] != sent.drag[1] || (ds.drags != sent.drags && (d[0] != 0 || d[1] != 0))) {
        sent.drag[0] = d[0];
        sent.drag[1] = d[1];
        sent.drags = ds.drags;
        scene.events.push(VLQT_EVENT_DRAG, hn.feedbackId, d, 2 * sizeof(float));
    }
}

void collectTouchPoints(Scene& scene, HandlerEntry& hn)
{
    const auto& points = touchPoints(hn)->points();
    const quint32 count = quint32(std::min<size_t>(points.size(), kMaxTouchPoints));
    // No point and none sent: nothing to send (a new sink starts empty).
    if (count == 0 && hn.sent.touch.size() <= 4)
        return;
    // Built in the scene's scratch (capacity kept), compared with the last payload and swapped in on change: no
    // allocation per frame while a finger is down and nothing changes.
    QByteArray& payload = scene.handlers.touchScratch;
    payload.resize(0);
    WireWriter w(payload);
    w.u32(count);
    quint32 n = 0;
    for (const auto& [id, p] : points) {
        if (n++ == count)
            break;
        w.u32(quint32(id)).f32(float(p.pos.x())).f32(float(p.pos.y())).f32(p.pressure).f32(float(p.size.width())).f32(float(p.size.height()));
    }
    if (payload == hn.sent.touch)
        return;
    std::swap(payload, hn.sent.touch);
    scene.events.push(VLQT_EVENT_TOUCH_POINTS, hn.feedbackId, hn.sent.touch.constData(), hn.sent.touch.size());
}

void collectPinch(Scene& scene, HandlerEntry& hn)
{
    const PinchState& ps = *hn.objects.pinch;
    HandlerSent& sent = hn.sent;
    // Gesture start: ACTIVE 1, then the values; end: the last values (if not sent yet), then ACTIVE 0.
    if (ps.running && !sent.active)
        scene.events.pushFlag(VLQT_EVENT_ACTIVE, hn.feedbackId, true);
    const float* v = ps.value;
    bool changed = false;
    for (int i = 0; i < 6; ++i)
        changed = changed || v[i] != sent.pinch[i];
    if ((ps.running || sent.active) && (changed || ps.gestures != sent.gestures)) {
        for (int i = 0; i < 6; ++i)
            sent.pinch[i] = v[i];
        sent.gestures = ps.gestures;
        scene.events.push(VLQT_EVENT_PINCH, hn.feedbackId, v, 6 * sizeof(float));
    }
    if (!ps.running && sent.active)
        scene.events.pushFlag(VLQT_EVENT_ACTIVE, hn.feedbackId, false);
    sent.active = ps.running;
}

} // namespace

/* Events of the input handlers, only while bound. */
void collectHandlers(Scene& scene)
{
    for (auto& kv : scene.handlerNodes) {
        HandlerEntry& hn = kv.second;
        if (hn.feedbackId == 0 || !hn.objects.object)
            continue;
        switch (hn.type) {
        case VLQT_ELEMENT_TYPE_HOVER_HANDLER: collectHover(scene, hn); break;
        case VLQT_ELEMENT_TYPE_TAP_HANDLER: collectTap(scene, hn); break;
        case VLQT_ELEMENT_TYPE_POINT_HANDLER: collectPoint(scene, hn); break;
        case VLQT_ELEMENT_TYPE_WHEEL_HANDLER: collectWheel(scene, hn); break;
        case VLQT_ELEMENT_TYPE_DRAG_HANDLER: collectDrag(scene, hn); break;
        case VLQT_ELEMENT_TYPE_TOUCH_POINTS_HANDLER: collectTouchPoints(scene, hn); break;
        case VLQT_ELEMENT_TYPE_PINCH_HANDLER: collectPinch(scene, hn); break;
        default: break;
        }
    }
}

} // namespace vlqt
