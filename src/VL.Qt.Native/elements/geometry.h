/* elements/geometry.h - geometry of an element ("Geometry" in protocol.h): SIZE, AUTO_SIZE, TRANSFORM, ANCHOR, drag
 * offset, the item's bounds and the common properties every element type takes. */
#pragma once

#include "elements/element_entry.h"
#include "elements/element_types.h"

#include <QtCore/QPointF>
#include <QtCore/QRectF>
#include <QtGui/QMatrix4x4>

#include <string>
#include <vector>

class QObject;
class QQuickItem;

namespace vlqt {

/* Geometry of an element ("Geometry" in protocol.h): TRANSFORM as sent and ANCHOR, resolved against the item's
 * actual size whenever it changes (see Elements::initGeometry). Created with the ElementEntry; shared with the item's
 * widthChanged/heightChanged connection, so it never refers to an erased ElementEntry. */
struct TransformState {
    QMatrix4x4 base;            // TRANSFORM (Qt column vectors), applied around the anchor
    float fx = 0.f, fy = 0.f;   // ANCHOR as fractions of width/height (default AlignLeft|AlignTop)
    float dx = 0, dy = 0;       // drag offset: translation in the parent's space, applied after TRANSFORM
    float ox = 0, oy = 0;       // top left of the item's bounds in its own coordinates: < 0 only for an auto-sized
                                // Group with children left of / above its origin (GroupItem, "Group frame" in protocol.h)
    bool sizedBounds = false;   // Group with AUTO_SIZE off: bounds = SIZE with its anchor point at the origin,
                                // the item (and so the children) stays at the origin
    QObject* matrix = nullptr;  // = ElementEntry::transform (child of the item), null while TRANSFORM is a pure x/y translation
                                // folded into x/y (placeItem in geometry.cpp) and there was no drag offset
    // Measure on change (scene/measure.h "Measure on change"): the scene and id of the node, set by watchGeometry,
    // null once the node is erased or the scene goes; queued = the id is in MeasureState::measureQueue.
    Scene* scene = nullptr;
    quint32 id = 0;
    bool measureQueued = false;
};

/* The item's bounds: (ox, oy, width, height) in item coordinates, i.e. (0, 0, w, h) except for an auto-sized
 * Group whose children reach left of / above its origin, and (-fx * w, -fy * h, w, h) for a Group with AUTO_SIZE off
 * (sizedBounds). Hit area of the Group, its clip, the group bounds of its parent Group, SCENE_BOUNDS, snapshot,
 * effect layer and the debug overlay use it. */
QRectF itemBounds(const TransformState& s, const QQuickItem* item);
/* Anchor point in item coordinates: per axis 0 for Left/Top (the item's origin), otherwise bounds.left + f * width
 * (Center, Right/Bottom). Equal to f * size whenever the bounds start at the origin (every element but the Group). */
QPointF anchorPoint(const TransformState& s, const QQuickItem* item);

/* The element type takes AUTO_SIZE (containers, Button, value inputs, ShaderEffect with Input), see applySize. */
bool takesAutoSize(const ElementEntry& n);
/* Its size currently follows the implicit size (takesAutoSize and AUTO_SIZE on); otherwise SIZE counts. */
inline bool isAutoSized(const ElementEntry& n) { return n.autoSize && takesAutoSize(n); }
/* Applies SIZE or, while auto-sized (isAutoSized), the implicit size; Group and QmlElement also get the kind of bounds. */
void applySize(ElementEntry& n);
/* AUTO_SIZE setter of the types that take it (takesAutoSize). */
bool setAutoSize(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&);
/* Common properties (ids 1..255): SIZE, TRANSFORM, OPACITY, CLIP, ANCHOR, DEBUG; every element type takes them. */
const std::vector<PropDef>& commonProps();
/* TRANSFORM around the anchor, then the drag offset: the matrix of the item's Matrix4x4 (see geometry.cpp). */
QMatrix4x4 pivoted(const TransformState& s, const QQuickItem* item);
/* Writes m into a Matrix4x4 created by Elements::createTransform (typed, no QVariant). */
void setMatrixValue(QObject* t, const QMatrix4x4& m);

} // namespace vlqt
