/* events.h - back channel of QtNative (plan 06): bindings, measured values of bound items and input handlers.
 *
 * Every bound item (BIND with feedbackId != 0) reports
 *   EVENT_MEASURED     (f32 w, f32 h)          implicitWidth/Height (Text: actual text size)
 *   EVENT_SCENE_BOUNDS (f32 x, f32 y, f32 w, f32 h)  mapRectToItem(root, 0, 0, width, height): item geometry in the
 *                   coordinates of the window root (origin of the Space, centre or top left), axis-aligned, incl.
 *                   transforms (not boundingRect: Text reports its painted rect there)
 * in logical pixels, and Video items additionally
 *   EVENT_MEDIA        (f32 position, f32 duration)  seconds, 0 until the media is loaded
 * only when the value changed since the last record sent for the same handle. Items with an error state (QML
 * property vlqtError, see elements.cpp: Image could not be loaded, Video player error) additionally report
 *   EVENT_ERROR        (UTF-8 text, no terminator)  on change; empty = error cleared. Nothing is sent while there
 *                   is no error (initial state = no error)
 * Measure on change: MEASURED / SCENE_BOUNDS are read only for items whose geometry changed since the last
 * poll, and for everything below them (an ancestor's move moves the subtree in the scene). A change is the item's
 * x/y, width/height, implicit size or parent (Qt signals, watchGeometry), its Matrix4x4, anchor or Group frame
 * (marked by Elements, Qt has no public signal for these; markGeometry), CREATE and a new handle. Attached masks go
 * with their element (they lie in its coordinates, not below it). A static scene reads no geometry at all; media,
 * error, clicks and user values are still read every poll, but only for types that have them. A new
 * handle (sink change) forgets the sent state, so the new sink gets the current values right away. The poll
 * record index is always 0 here: one item = one binding, the C# side resolves handle -> sink slot (incl. the
 * slot index); index is an offset relative to that slot, reserved for items serving several slots.
 *
 * Button items additionally report Qt `clicked` as
 *   EVENT_CLICK (u32 count delta)              clicked signals since the last poll
 * Value inputs (SpinBox; CheckBox, Switch, Slider, TextField) additionally report user changes as
 *   EVENT_VALUE (u16 valueType, u16 0, value like SET)  after a user change since the last poll, with the value of the
 *                   last one, captured when Qt's user-only signal fired (SpinBox valueModified); never for SET (no
 *                   echo). A new handle drops a change not polled yet. Schema and guarantees: protocol.h, elements.cpp.
 * QmlElement items additionally report the changes of the properties and signals declared in the root of their QML
 * file: EVENT_QML_PROPERTY (the last value of each changed property, never for the patch's own SET_QML_PROPERTIES) and
 * EVENT_QML_SIGNAL (every emitted signal, cap 256 per poll, QmlItem in qmlitem.h), formats in protocol.h; their errors (load,
 * type, rejected properties) arrive as EVENT_ERROR like those of Image/Video.
 * Elements have no pointer handlers of their own: without an attached input handler an item is transparent for input.
 * BIND flags are ignored for elements (CANCEL_PRESS has no effect since #320, the old Interactive/Pointer flags of
 * #208 are reserved).
 *
 * Input handlers (protocol.h head comment) are protocol objects of their own (HandlerNode): CREATE reserves
 * the id, REPARENT creates the QML handler at the element item (appended to its data, like `Item { HoverHandler {} }`)
 * and applies the properties set before, BIND routes its events to a sink handle (index 0), DESTROY of the handler or
 * of its element removes it. Several handlers per item are fine (each reports to its own sink). Events, only while
 * bound:
 *   Hover handler   EVENT_HOVER (u8) on change. Not blocking: hover reaches every handler under the cursor. No position
 *                   (positions come from the Point handler only).
 *   Tap handler     (TapHandler, gesturePolicy ReleaseWithinBounds: exclusive grab on press, the topmost tap handler
 *                   wins, tap when released inside) EVENT_PRESS (u8) on change, EVENT_CLICK (u32) taps since the last poll,
 *                   EVENT_TAP_COUNT (u32) tapCount of the last tap on change (2 = double click; never 0: the value of
 *                   the last tap stays until the next one), EVENT_LONG_PRESS (u32) long
 *                   presses since the last poll. ACCEPTED_BUTTONS / ACCEPTED_DEVICES filter.
 *   Point handler   (PointHandler, passive grab: presses pass through to the handlers below) EVENT_ACTIVE (u8) on change,
 *                   EVENT_POINTER (f32 x, f32 y) item-local while active (mouse button down, finger, pen), on change.
 *                   ACCEPTED_BUTTONS / ACCEPTED_DEVICES filter. ACCEPTED_BUTTONS 0: the PointHandler takes
 *                   touch and pen only, an internal HoverHandler (mouse + touchpad of ACCEPTED_DEVICES) makes the mouse
 *                   active while it is over the item and reports its position; created/removed when the property
 *                   changes (no ACTIVE 1 left hanging).
 *   Wheel handler   (WheelHandler, target null: moves nothing) EVENT_WHEEL (f32 x, f32 y) = sum of angleDelta / 120 since
 *                   the last poll (notches; trackpads fractional), only when != 0; after a poll with motion once (0, 0).
 *                   ACCEPTED_DEVICES filter.
 *   Drag handler    (DragHandler, target null) EVENT_ACTIVE (u8) on change, EVENT_DRAG (f32 x, f32 y) = translation of
 *                   the running drag since the press in the parent's space, after DRAG_AXIS and DRAG_LIMITS, on change
 *                   and once per new drag. It keeps the last value after the release until the next drag; a new sink
 *                   gets nothing until the next drag. MOVES_ELEMENT on (default): the item's drag offset = offset at
 *                   the drag start + translation, clamped to DRAG_LIMITS (minX, minY, maxX, maxY of the offset); off:
 *                   events only. The offset is part of the item's Matrix4x4 (Elements::setDragOffset: T(offset) *
 *                   TRANSFORM around the anchor, one object instead of a Matrix4x4 + Translate pair, so the order is
 *                   always right), so it moves the item in its parent's space whatever its own rotation/scale. Every
 *                   SET TRANSFORM of the item sets it to 0 (a running drag goes on from there); layouts (Row/Column) and
 *                   TRANSFORM leave it alone, a Row/Column keeps the child's place. Several drag handlers at one item
 *                   share its offset. A Group drags as a whole (hit area = its size, i.e. the children's extent with
 *                   Auto Size); nested drags follow Qt: the innermost item wins.
 *                   Qt behaviour found in the spike (Qt 6.12, SmokeTest drag*): activeTranslation is the centroid's
 *                   movement in scene (window) coordinates, also with target null; QtNative maps it into the parent's
 *                   space (equal to the window direction unless a parent is rotated/scaled; that case is not covered by
 *                   a test). It starts with the move after the activation (past the drag threshold), so the first
 *                   values arrive one move late. xAxis/yAxis.minimum/maximum only clamp persistentTranslation and a
 *                   target's position (qquickdragaxis_p.h: activeValue unclamped), not used here: QtNative clamps the
 *                   offset itself. DRAG_AXIS sets xAxis/yAxis.enabled; a drag running more than 45 degrees off the
 *                   enabled axis is then not taken and reaches a parent's drag handler (child x only + vertical drag
 *                   moves the parent, tested).
 *   Touch Points    (TouchPointsHandler, touch.h) EVENT_TOUCH_POINTS = u32 count, then per point u32 id, f32 x, f32 y
 *                   (item-local, logical pixels), f32 pressure, f32 w, f32 h (ellipse diameters; pressure and size are
 *                   device-dependent, often constant / 0x0), sorted by id, on change (press, move, release of one of its
 *                   points); count 0 once when the last point goes. At most 64 points are sent (the record length is
 *                   u16). A point belongs to the handler from its press inside the item until its release, also outside
 *                   the item. Passive grab: Tap, Point, Drag and Pinch at the same item keep working. OCCLUSION on
 *                   (default): a press that an element on top already grabs (Tap/Drag handler, Button) is not taken.
 *                   ACCEPTED_DEVICES filter (Qt default: all, i.e. the mouse appears as point id 0).
 *   Pinch handler   (PinchHandler, target null) EVENT_ACTIVE (u8) on change, EVENT_PINCH (f32 scale, f32 rotation in
 *                   degrees clockwise, f32 x, f32 y translation, f32 x, f32 y pivot, in the parent's space) while
 *                   active, on change and once per new gesture. Values count from Qt's activation (SmokeTest: when the
 *                   second finger touches); the pivot is the centroid then, the translation the movement of the pivot
 *                   point (the centroid's movement without a change of the finger count; not Qt's activeTranslation,
 *                   which is wrong after a change of the finger count, touch.cpp). The
 *                   last EVENT_PINCH comes before EVENT_ACTIVE 0; the 1/0/0 Qt resets to at the end is never sent, a new sink
 *                   gets nothing until the next gesture. MINIMUM_POINT_COUNT / MAXIMUM_POINT_COUNT (default 2/2),
 *                   ACCEPTED_DEVICES. The gesture runs while Qt's pinch is active or at least MINIMUM_POINT_COUNT points
 *                   lie on the item (PinchState, touch.h): a third finger (more than MAXIMUM_POINT_COUNT) pauses it,
 *                   lifting it goes on from the paused value, no jump.
 * A new handle or the DESTROY of a handler send EVENT_HOVER 0 / EVENT_PRESS 0 / EVENT_ACTIVE 0 to the old handle if 1 was sent
 * (EVENT_WHEEL (0, 0) after motion) and forget the sent state: the new handle gets the current state with the next poll.
 * A new handle during a press cancels it for the Tap handler: no EVENT_PRESS 1 and no tap until the next press; taps and
 * long presses not yet polled are dropped, as is wheel motion, and the new handle gets EVENT_TAP_COUNT only from its first tap. A Text item with at least one handler hit-tests against
 * the painted text rect (TextMask as containmentMask), not the item rectangle; line-/glyph-exact testing is not done.
 *
 * Window-level (handle 0, index 0, not a sink), queued by the poll after a move/resize/state change
 * (QWindow x/y/width/height/windowState/screen/visible changed) and once after creation:
 *   EVENT_WINDOW_GEOMETRY (f32 x, f32 y, f32 w, f32 h, u32 flags, f32 cx, f32 cy)
 *                   x/y = outer top-left incl. frame (QWindow::framePosition), w/h = client size, logical pixels (DIP)
 *                   = the convention of vlqt_window_create, so reading and setting round-trips. flags =
 *                   vlqt_window_state (minimized/maximized/fullscreen/visible): consumers persisting the geometry
 *                   skip minimized/fullscreen records (Windows reports -32000 positions for minimized windows).
 *                   cx/cy = client top-left (QWindow::position, DIP), for the Client Bounds output. */
#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QMetaProperty>
#include <QtCore/QObject>
#include <QtCore/QPointF>
#include <QtCore/QString>
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
struct Node;
struct Scene;
struct Value;

inline constexpr float kInf = std::numeric_limits<float>::infinity();

/* Last values sent for the current handle of one item. */
struct SentState {
    float measured[2] = {};
    float bounds[4] = {};
    float media[2] = {};
    QString error;      // last EVENT_ERROR text (empty = none sent or cleared)
    bool hasMeasured = false;
    bool hasBounds = false;
    bool hasMedia = false;
};

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
    quint32 epoch_ = 0;      // Node::transformEpoch at the start / last re-base
};

/* Input handler object: CREATE reserves the id, REPARENT attaches the QML handler to an element item. */
struct HandlerNode {
    quint16 type = 0;            // VLQT_ELEMENT_TYPE_*_HANDLER
    quint32 owner = 0;           // element id the handler is attached to, 0 = not attached yet
    QObject* object = nullptr;   // QML handler (QObject child of the owner's item), nullptr until the REPARENT
    TapState* tap = nullptr;     // Tap handler: signal counters (child of object)
    DragState* drag = nullptr;   // Drag handler: offset bookkeeping (child of object)
    bool movesElement = true;    // VLQT_PROPERTY_MOVES_ELEMENT (Drag)
    qint32 dragAxis = 0;         // VLQT_PROPERTY_DRAG_AXIS (Drag): 0 both, 1 x only, 2 y only
    float dragLimits[4] = {-kInf, -kInf, kInf, kInf}; // VLQT_PROPERTY_DRAG_LIMITS (Drag): minX, minY, maxX, maxY
    float sentDrag[2] = {};      // last EVENT_DRAG sent (0, 0 = nothing sent)
    quint32 sentDrags = 0;       // DragState::drags at the last EVENT_DRAG
    QObject* hover = nullptr;    // Point handler with ACCEPTED_BUTTONS 0: internal HoverHandler for the mouse
    TouchPointsHandler* points = nullptr; // Touch Points: the handler itself; Pinch: internal point counter
    PinchState* pinch = nullptr; // Pinch handler: gesture bookkeeping (child of object)
    bool occlusion = true;       // VLQT_PROPERTY_OCCLUSION (Touch Points)
    std::optional<qint32> minimumPointCount; // VLQT_PROPERTY_MINIMUM_POINT_COUNT (Pinch), Qt default (2) while unset
    std::optional<qint32> maximumPointCount; // VLQT_PROPERTY_MAXIMUM_POINT_COUNT (Pinch), Qt default (2) while unset
    QByteArray sentTouch;        // last EVENT_TOUCH_POINTS payload (empty or count 0 = no point sent)
    float sentPinch[6] = {};     // last EVENT_PINCH sent
    quint32 sentGestures = 0;    // PinchState::gestures at the last EVENT_PINCH
    quint32 feedbackId = 0;            // BIND feedbackId, 0 = unbound
    std::optional<qint32> acceptedButtons; // VLQT_PROPERTY_ACCEPTED_BUTTONS, Qt default (left) while unset
    std::optional<qint32> acceptedDevices; // VLQT_PROPERTY_ACCEPTED_DEVICES, Qt default while unset
    // Last values sent to sink.
    bool sentHover = false;
    bool sentPress = false;
    bool sentActive = false;
    bool sentWheel = false;      // EVENT_WHEEL with motion sent: the next poll without motion sends (0, 0)
    bool hasPointer = false;
    quint32 sentTapCount = 0;
    float sentPointer[2] = {};
};

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

private:
    QObject* create(QQmlComponent*& comp, const char* qml, const char* url, QQuickItem* item);

    QQmlEngine* engine;
    QQmlComponent* comps[6] = {}; // HOVER, TAP, POINT, WHEEL, DRAG, PINCH
    struct {
        QMetaProperty hovered, pressed, active, point, wheelDelta; // resolved on the first creation of each type
        QMetaProperty position;                                   // on the point gadget, resolved on first read
    } props;
};

/* BIND of an element: stores the handle; a different handle resets the sent state. Flags are ignored. */
void bindNode(Scene& scene, Node& node, quint32 handle);

/* Text items: TextMask as containmentMask while the item has at least one input handler. */
void updateTextMask(Node& node);

bool isHandlerType(quint16 type);
/* Handler ops, called by the Scene for ids of its handler table. Return an ApplyResult (+ err). */
int reparentHandler(Scene& scene, HandlerNode& handler, quint32 handlerId, quint32 parentId, std::string& err);
int setHandler(Scene& scene, HandlerNode& handler, quint16 prop, const Value& v, std::string& err);
void bindHandler(Scene& scene, HandlerNode& handler, quint32 handle);
/* Removes the handler (QML object, owner list, table entry); resets the sent state of its sink (EVENT_HOVER 0 ...). */
void destroyHandler(Scene& scene, quint32 handlerId);

/* Queues EVENT_MEASURED / EVENT_SCENE_BOUNDS / EVENT_MEDIA / EVENT_ERROR / Button EVENT_CLICK / EVENT_VALUE for every bound item whose value changed
 * and the events of the bound input handlers. Called by Scene::poll, i.e. after processEvents (polish/layout done)
 * when the host polls once per frame. Geometry only of the items marked since the last poll and their subtrees. */
void collectMeasurements(Scene& scene);
/* Measure on change: connects the geometry signals of a new node's item to markGeometry (elements.h) and marks
 * it once. Called by Scene::create after Elements::initGeometry. */
void watchGeometry(Scene& scene, quint32 id, Node& node);
/* Adds the node to the ones whose media/error/clicks/user value the poll reads (Node::polled or an effect chain), once.
 * Called by watchGeometry and after SET_EFFECT_CHAIN. */
void listPolled(Scene& scene, Node& node);

/* Connects the window's geometry/state signals to Scene::geometryDirty (called by the Scene ctor). */
void watchWindowGeometry(Scene& scene);
/* Queues EVENT_WINDOW_GEOMETRY if the window moved, resized or changed state since the last poll. */
void collectWindowGeometry(Scene& scene);

} // namespace vlqt
