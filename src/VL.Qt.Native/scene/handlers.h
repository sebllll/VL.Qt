/* scene/handlers.h - input handlers of a scene (Hover, Tap, Point, Wheel, Drag, Touch Points, Pinch): the protocol
 * objects (HandlerEntry: CREATE reserves the id, REPARENT creates the QML handler at the element item, BIND routes its
 * events to a feedback id, DESTROY removes it) and the collectors that turn their state into events at each poll.
 * Without an input handler an item is transparent for input. GUI thread. Measured values of bound items and the window
 * geometry: scene/measure.h.
 * Events, grabs, drag and pinch rules, Qt behaviour found in spikes and what a change of the feedback does:
 * docs/design/input.md, "Events per handler", "Drag", "Pinch" and "Change of the feedback". Payloads: protocol.h.
 */
#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QMetaProperty>
#include <QtCore/QObject>
#include <QtCore/QPointF>
#include <QtCore/QPointer>
#include <QtCore/QtTypes>

#include <limits>
#include <optional>
#include <string>

class QQmlComponent;
class QQmlEngine;
class QQuickItem;

namespace vlqt {

class PinchState;
class TouchPointsHandler;
struct ElementEntry;
class Scene;
struct Value;

/* containmentMask of Text items: hit test against the painted text rect (QQuickText::boundingRect, item-local,
 * alignment applied) instead of the whole item rectangle. Child of the item. */
class TextMask : public QObject
{
    Q_OBJECT
public:
    explicit TextMask(QQuickItem* item);
    Q_INVOKABLE bool contains(const QPointF& point) const;

private:
    QQuickItem* item_;
};

/* Signal counters of a Tap handler: QObject child of the QML TapHandler (deleted with it). */
class TapState : public QObject
{
    Q_OBJECT
public:
    explicit TapState(QObject* tap) : QObject(tap), tap_(tap) {}
    quint32 taps = 0;        // tapped since the last poll
    quint32 tapCount = 0;    // tapCount of the last (not cancelled) tap
    quint32 longPresses = 0; // longPressed since the last poll
    bool cancelled = false;  // sink change during a press: the running press neither reports pressed nor taps

    /* Sink change: drops what was not polled, cancels a running press. */
    void cancel(bool pressed);

public Q_SLOTS:
    void onTapped();
    void onLongPressed();
    void onPressedChanged();

private:
    QObject* tap_;
};

/* Drag handler: follows active/activeTranslation of the QML DragHandler (target null) and writes the drag offset
 * of the owner item. QObject child of the QML DragHandler (deleted with it). */
class DragState : public QObject
{
    Q_OBJECT
public:
    DragState(QObject* drag, Scene& scene, quint32 handlerId) : QObject(drag), drag_(drag), scene_(scene), id_(handlerId) {}
    float delta[2] = {};     // translation of the running (or last) drag since the press, after axis + limits (EVENT_DRAG)
    quint32 drags = 0;       // drags started: a new drag sends EVENT_DRAG even with the same value as the last one

public Q_SLOTS:
    void onActiveChanged();
    void onTranslationChanged();

private:
    QObject* drag_;
    Scene& scene_;
    quint32 id_;
    float start_[2] = {};    // drag offset of the item when the drag started (re-based to 0 after a SET TRANSFORM)
    float raw_[2] = {};      // last activeTranslation in parent space (axis applied, before limits)
    quint32 epoch_ = 0;      // ElementEntry::transformEpoch at the start / last re-base
};

/* The stored properties of an input handler (kHandlerProps): set before or after the REPARENT, applied to the Qt
 * objects when they exist. */
struct HandlerConfig {
    std::optional<qint32> acceptedButtons; // VLQT_PROPERTY_ACCEPTED_BUTTONS, Qt default (left) while unset
    std::optional<qint32> acceptedDevices; // VLQT_PROPERTY_ACCEPTED_DEVICES, Qt default while unset
    bool movesElement = true;    // VLQT_PROPERTY_MOVES_ELEMENT (Drag)
    qint32 dragAxis = 0;         // VLQT_PROPERTY_DRAG_AXIS (Drag): 0 both, 1 x only, 2 y only
    float dragLimits[4] = {-std::numeric_limits<float>::infinity(), -std::numeric_limits<float>::infinity(),
        std::numeric_limits<float>::infinity(), std::numeric_limits<float>::infinity()}; // VLQT_PROPERTY_DRAG_LIMITS (Drag): minX, minY, maxX, maxY
    bool occlusion = true;       // VLQT_PROPERTY_OCCLUSION (Touch Points)
    std::optional<qint32> minimumPointCount; // VLQT_PROPERTY_MINIMUM_POINT_COUNT (Pinch), Qt default (2) while unset
    std::optional<qint32> maximumPointCount; // VLQT_PROPERTY_MAXIMUM_POINT_COUNT (Pinch), Qt default (2) while unset
};

/* The Qt side of an input handler, from the REPARENT on; all null before. HandlerObjects owns object, hover and
 * pinchTracker (QObject children of the owner's item, so the item deletes them too: the QPointers then read null):
 * release() and the destructor delete them in the one safe order. tap, drag and pinch are QObject children of object
 * and go with it. Not copyable: the handler table keeps each entry in place. */
struct HandlerObjects {
    QPointer<QObject> object;    // QML handler; Touch Points: the TouchPointsHandler (touch_handler.h)
    QPointer<QObject> hover;     // Point handler with ACCEPTED_BUTTONS 0: internal HoverHandler for the mouse
    QPointer<TouchPointsHandler> pinchTracker; // Pinch handler: internal point counter that PinchState reads
    QPointer<TapState> tap;      // Tap handler: signal counters (child of object)
    QPointer<DragState> drag;    // Drag handler: offset bookkeeping (child of object)
    QPointer<PinchState> pinch;  // Pinch handler: gesture bookkeeping (child of object, reads pinchTracker)

    HandlerObjects() = default;
    ~HandlerObjects();
    HandlerObjects(const HandlerObjects&) = delete;
    HandlerObjects& operator=(const HandlerObjects&) = delete;

    /* Deletes the Qt objects: object first (takes tap, drag and pinch along, so PinchState never sees a deleted
     * pinchTracker), then pinchTracker and hover. */
    void release();
};

/* What the sink of an input handler got last: the next poll sends only changes against it. A sink change or DESTROY
 * pushes the 0 records for what was set (resetSent in scene/handlers.cpp), then resets it with `sent = {}`. */
struct HandlerSent {
    bool hover = false;          // EVENT_HOVER 1
    bool press = false;          // EVENT_PRESS 1
    bool active = false;         // EVENT_ACTIVE 1
    bool wheel = false;          // EVENT_WHEEL with motion: the next poll without motion sends (0, 0)
    bool hasPointer = false;     // pointer holds the last EVENT_POINTER
    float pointer[2] = {};       // last EVENT_POINTER
    quint32 tapCount = 0;        // last EVENT_TAP_COUNT (0 = none)
    float drag[2] = {};          // last EVENT_DRAG (0, 0 = nothing sent)
    quint32 drags = 0;           // DragState::drags at the last EVENT_DRAG
    QByteArray touch;            // last EVENT_TOUCH_POINTS payload (empty or count 0 = no point sent)
    float pinch[6] = {};         // last EVENT_PINCH
    quint32 gestures = 0;        // PinchState::gestures at the last EVENT_PINCH
};

/* Input handler object: CREATE reserves the id, REPARENT attaches the QML handler to an element item. */
struct HandlerEntry {
    quint16 type = 0;            // VLQT_ELEMENT_TYPE_*_HANDLER
    quint32 owner = 0;           // element id the handler is attached to, 0 = not attached yet
    quint32 feedbackId = 0;      // BIND feedbackId, 0 = unbound
    HandlerConfig config;
    HandlerObjects objects;
    HandlerSent sent;
};

/* The cached QML component of each input handler type (Handlers::comps); Touch Points has none. */
enum class HandlerComponent { Hover, Tap, Point, Wheel, Drag, Pinch, Count };

/* Per window (engine): QML components of the handler types, created on first use, and their properties. */
class Handlers
{
public:
    explicit Handlers(QQmlEngine* engine) : engine(engine) {}
    ~Handlers();
    Handlers(const Handlers&) = delete;
    Handlers& operator=(const Handlers&) = delete;

    /* Creates the QML handler of a VLQT_ELEMENT_TYPE_*_HANDLER type at item; nullptr on failure (logged). */
    QObject* createHandler(quint16 type, QQuickItem* item);

    bool isHovered(const QObject* hover) const { return props.hovered.read(hover).toBool(); }
    bool isPressed(const QObject* tap) const { return props.pressed.read(tap).toBool(); }
    bool isActive(const QObject* handler) const { return props.active.read(handler).toBool(); }
    /* Creates a HoverHandler at item with acceptedDevices = devices; nullptr on failure (logged). */
    QObject* createHover(QQuickItem* item, qint32 devices);
    /* point.position of a single point handler (QQuickHandlerPoint gadget), local to the item. */
    bool pointerPosition(QObject* handler, float out[2]);
    /* Wheel handler: notches accumulated since the last call (angleDelta / 120), reset to 0. False if none. */
    bool takeWheel(QObject* wheel, float out[2]);

    /* collectTouchPoints: the payload being built, swapped with HandlerSent::touch on change (capacity reused). */
    QByteArray touchScratch;

private:
    QObject* create(QQmlComponent*& comp, const char* url, QQuickItem* item);

    QQmlEngine* engine;
    QQmlComponent* comps[size_t(HandlerComponent::Count)] = {};
    QQmlComponent*& component(HandlerComponent c) { return comps[size_t(c)]; }
    struct {
        QMetaProperty hovered, pressed, active, point, wheelDelta; // resolved on the first creation of each type
        QMetaProperty position;                                   // on the point gadget, resolved on first read
    } props;
};

/* Text items: TextMask as containmentMask while the item has at least one input handler. */
void updateTextMask(ElementEntry& node);

bool isHandlerType(quint16 type);
/* Handler ops, called by the Scene for ids of its handler table. Return an ApplyResult (+ err). */
int reparentHandler(Scene& scene, HandlerEntry& handler, quint32 handlerId, quint32 parentId, std::string& err);
/* SET of a handler property: checked against the property table (kHandlerProps in scene/handlers.cpp); a value the Qt
 * object rejects fails the SET and is not stored. */
int setHandler(Scene& scene, HandlerEntry& handler, quint16 prop, const Value& v, std::string& err);
void bindHandler(Scene& scene, HandlerEntry& handler, quint32 handle);
/* Removes the handler (QML object, owner list, table entry); resets the sent state of its sink (EVENT_HOVER 0 ...). */
void destroyHandler(Scene& scene, quint32 handlerId);

/* Queues the events of the bound input handlers. Called by Scene::poll after collectMeasurements. */
void collectHandlers(Scene& scene);

} // namespace vlqt
