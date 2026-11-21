/* scene/measure.h - state reporting of a scene: measured values and state of bound items, and the window geometry.
 *
 * Every bound item (BIND with feedbackId != 0) reports
 *   EVENT_MEASURED     (f32 w, f32 h)          implicitWidth/Height (Text: actual text size)
 *   EVENT_SCENE_BOUNDS (f32 x, f32 y, f32 w, f32 h)  mapRectToItem(root, 0, 0, width, height): item geometry in the
 *                   coordinates of the window root (origin of the Space, centre or top left), axis-aligned, incl.
 *                   transforms (not boundingRect: Text reports its painted rect there)
 * in logical pixels, and Video items additionally
 *   EVENT_MEDIA        (f32 position, f32 duration)  seconds, 0 until the media is loaded
 * only when the value changed since the last record sent for the same handle. Items with an error state (QML
 * property vlqtError, see elements/type_video.cpp: Video player error) additionally report
 *   EVENT_ERROR        (UTF-8 text, no terminator)  on change; empty = error cleared. Nothing is sent while there
 *                   is no error (initial state = no error)
 * Measure on change: MEASURED / SCENE_BOUNDS are read only for items whose geometry changed since the last
 * poll, and for everything below them (an ancestor's move moves the subtree in the scene). A change is the item's
 * x/y, width/height, implicit size or parent (Qt signals, watchGeometry), its Matrix4x4, anchor or Group frame
 * (marked by Elements, Qt has no public signal for these; markGeometry), CREATE and a new handle. Attached masks go
 * with their element (they lie in its coordinates, not below it). A static scene reads no geometry at all; media,
 * error, clicks and user values are still read every poll, but only for types that have them. A new
 * handle (sink change) forgets the sent state, so the new sink gets the current values right away. One item = one
 * binding: the C# side resolves the feedbackId of a poll record to its sink slot.
 *
 * Button items additionally report Qt `clicked` as
 *   EVENT_CLICK (u32 count delta)              clicked signals since the last poll
 * Value inputs (SpinBox; CheckBox, Switch, Slider, TextField) additionally report user changes as
 *   EVENT_VALUE (u16 valueType, u16 0, value like SET)  after a user change since the last poll, with the value of the
 *                   last one, captured when Qt's user-only signal fired (SpinBox valueModified); never for SET (no
 *                   echo). A new handle drops a change not polled yet. Schema and guarantees: protocol.h, elements/type_controls.cpp.
 * QmlElement items additionally report the changes of the properties and signals declared in the root of their QML
 * file: EVENT_QML_PROPERTY (the last value of each changed property, never for the patch's own SET_QML_PROPERTIES) and
 * EVENT_QML_SIGNAL (every emitted signal, cap 256 per poll, QmlItem in user_qml/qml_item.h), formats in protocol.h; their errors (load,
 * type, rejected properties) arrive as EVENT_ERROR like those of Image/Video.
 * Window-level (feedbackId 0, not a sink), queued by the poll after a move/resize/state change
 * (QWindow x/y/width/height/windowState/screen/visible changed) and once after creation:
 *   EVENT_WINDOW_GEOMETRY (f32 x, f32 y, f32 w, f32 h, u32 flags, f32 cx, f32 cy)
 *                   x/y = outer top-left incl. frame (QWindow::framePosition), w/h = client size, logical pixels (DIP)
 *                   = the convention of vlqt_window_create, so reading and setting round-trips. flags =
 *                   vlqt_window_state (minimized/maximized/fullscreen/visible): consumers persisting the geometry
 *                   skip minimized/fullscreen records (Windows reports -32000 positions for minimized windows).
 *                   cx/cy = client top-left (QWindow::position, DIP), for the Client Bounds output. */
#pragma once

#include "user_qml/qml_wire.h"

#include <QtCore/QByteArray>
#include <QtCore/QObject>
#include <QtCore/QPoint>
#include <QtCore/QString>
#include <QtCore/QtTypes>

#include <vector>

class QQuickItem;

namespace vlqt {

struct ElementEntry;
class Scene;
struct TransformState;

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

/* Measure on change and window geometry of one scene (Scene::measure). */
struct MeasureState {
    /* EVENT_WINDOW_GEOMETRY: set by the window's geometry/state signals (context = geometryWatch, so the
     * connections end with the scene), cleared by the poll that queues the record. */
    bool geometryDirty = true;
    /* Frame position of the last record: X11 delivers the frame margins (_NET_FRAME_EXTENTS) later than the
     * geometry and Qt has no signal for them, so a moved framePosition() also queues a record. */
    QPoint reportedFramePos;
    QObject geometryWatch;
    /* Measure on change: ids whose geometry changed since the last poll (markGeometry, no duplicates
     * until the poll), the walk stack and poll stamp of collectMeasurements (capacity reused, no per-frame allocation). */
    std::vector<quint32> measureQueue;
    std::vector<QQuickItem*> measureStack;
    quint32 measureStamp = 0;
    /* Ids of the nodes whose media/error/clicks/user value the poll reads (listPolled); entries of destroyed
     * nodes drop out in the next poll. pollStamp skips a second entry of a reused id. */
    std::vector<quint32> pollIds;
    quint32 pollStamp = 0;
    /* Scratch of collectQml (one QmlElement at a time): the taken events and the payload being built. Members so that
     * their capacity is reused from poll to poll (clear() of a vector and resize(0) of a QByteArray keep it). */
    std::vector<QmlPropertyEvent> qmlProperties;
    std::vector<QmlSignalEvent> qmlSignals;
    QByteArray qmlPayload;
};

/* BIND of an element: stores the handle; a different handle resets the sent state. Flags are ignored. */
void bindNode(Scene& scene, ElementEntry& node, quint32 handle);

/* The geometry of the node changed (frame, matrix, anchor, ...): queues it for the next poll, which measures it and its
 * subtree (scene/measure.cpp). Cheap and idempotent until that poll; no-op once the node is gone. */
void markGeometry(TransformState& s);

/* Queues EVENT_MEASURED / EVENT_SCENE_BOUNDS / EVENT_MEDIA / EVENT_ERROR / Button EVENT_CLICK / EVENT_VALUE for every
 * bound item whose value changed. Called by Scene::poll, i.e. after processEvents (polish/layout done) when the host
 * polls once per frame. Geometry only of the items marked since the last poll and their subtrees. */
void collectMeasurements(Scene& scene);
/* Measure on change: connects the geometry signals of a new node's item to markGeometry (above) and marks
 * it once. Called by Scene::create after Elements::initGeometry. */
void watchGeometry(Scene& scene, quint32 id, ElementEntry& node);
/* Adds the node to the ones whose media/error/clicks/user value the poll reads (ElementEntry::polled or an effect chain), once.
 * Called by watchGeometry and after SET_EFFECT_CHAIN. */
void listPolled(Scene& scene, ElementEntry& node);

/* Connects the window's geometry/state signals to MeasureState::geometryDirty (called by the Scene ctor). */
void watchWindowGeometry(Scene& scene);
/* Queues EVENT_WINDOW_GEOMETRY if the window moved, resized or changed state since the last poll. */
void collectWindowGeometry(Scene& scene);

} // namespace vlqt
