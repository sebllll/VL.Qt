/* elements.h - element registry of QtNative (plan 05).
 *
 * Every element type is a table entry {type id, QML one-liner, property table}. Items are created from
 * QQmlComponents compiled once per type and engine (no private Qt headers, LGPL). QML properties are
 * written through QMetaProperty resolved once per type (indexOfProperty on the first instance), never by
 * name per frame. Grouped properties ("border.color") go through the group object; QFont fields through
 * modifyFont (read-modify-write). New element properties are table entries only, no new exports. */
#pragma once

#include "events.h"
#include "protocol.h"

#include <QtCore/QByteArray>
#include <QtCore/QMetaProperty>
#include <QtCore/QPointF>
#include <QtCore/QPointer>
#include <QtCore/QRectF>
#include <QtCore/QtTypes>
#include <QtCore/QVariant>
#include <QtGui/QMatrix4x4>

#include <memory>

#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

class QFont;
class QObject;
class QQmlComponent;
class QQmlEngine;
class QQuickItem;

namespace vlqt {

struct EffectChainDesc;  // effectchain.h
struct EffectComponents; // effectchain.h
class EffectChain;       // effectchain.h

/* Return codes of Scene::apply / vlqt_window_apply (0 = ok). */
enum ApplyResult {
    ApplyOk = 0,
    ApplyNullWindow = -1,
    ApplyBadLength = -2,      // truncated buffer, wrong payloadLen for op / value type, bad alignment
    ApplyUnknownOp = -3,
    ApplyUnknownId = -4,
    ApplyUnknownType = -5,
    ApplyUnknownProperty = -6, // property not supported by the element type (also logged once per type)
    ApplyBadValue = -7,        // value type does not match the property, or the setter rejected the value
    ApplyInvalid = -8          // e.g. CREATE with id 0 / existing id, REPARENT into own subtree
};

/* Decoded SET value. Pointers/strings are only valid during the setter call. */
struct Value {
    quint16 type = 0;          // VLQT_VT_*
    float f[16] = {};          // f32 (f[0]), color (rgba), rectf (x, y, w, h), mat4 (row-major like Stride)
    qint32 i = 0;              // i32, ivec2 x
    qint32 i2 = 0;             // ivec2 y
    bool b = false;            // bool
    quint64 u = 0;             // u64 (resource id)
    QByteArray utf8;           // string (raw UTF-8, not copied: fromRawData)
    QObject* ref = nullptr;    // ref (resolved id; id 0 = nullptr, i.e. "no reference")
    // f32 array and path: views into the command buffer, not copied. The floats are 4-byte aligned within the buffer,
    // but the buffer itself has no guaranteed alignment: read them with std::memcpy, never through a float*.
    const unsigned char* floats = nullptr; // f32 array: floatCount x f32
    quint32 floatCount = 0;
    const unsigned char* verbs = nullptr;  // path: verbCount x u8 (VLQT_VERB_*, checked; pointCount matches the verbs)
    quint32 verbCount = 0;
    const unsigned char* points = nullptr; // path: pointCount x (x, y) f32
    quint32 pointCount = 0;
};

/* Geometry of an element (#469, "Geometry" in protocol.h): TRANSFORM as sent and ANCHOR, resolved against the item's
 * actual size whenever it changes (see Elements::initGeometry). Created with the Node; shared with the item's
 * widthChanged/heightChanged connection, so it never refers to an erased Node. */
struct TransformState {
    QMatrix4x4 base;            // TRANSFORM (Qt column vectors), applied around the anchor
    float fx = 0.f, fy = 0.f;   // ANCHOR as fractions of width/height (default AlignLeft|AlignTop, #471)
    float dx = 0, dy = 0;       // drag offset (#209): translation in the parent's space, applied after TRANSFORM
    float ox = 0, oy = 0;       // top left of the item's frame in its own coordinates: < 0 only for an auto-sized
                                // Group with children left of / above its origin (GroupItem, "Group frame" in protocol.h)
    bool sizedFrame = false;    // Group with AUTO_SIZE off (#475): frame = SIZE with its anchor point at the origin,
                                // the item (and so the children) stays at the origin
    QObject* matrix = nullptr;  // = Node::transform (child of the item), null while TRANSFORM is a pure x/y translation
                                // folded into x/y (placeItem in elements.cpp) and there was no drag offset
    // Measure on change (#20, events.h "Measure on change"): the scene and id of the node, set by watchGeometry, null
    // once the node is erased or the scene goes; queued = the id is in Scene::measureQueue.
    Scene* scene = nullptr;
    quint32 id = 0;
    bool measureQueued = false;
};

/* The geometry of the node changed (frame, matrix, anchor, ...): queues it for the next poll, which measures it and its
 * subtree (events.cpp). Cheap and idempotent until that poll; no-op once the node is gone. */
void markGeometry(TransformState& s);

/* The item's frame (#469): (ox, oy, width, height) in item coordinates, i.e. (0, 0, w, h) except for an auto-sized
 * Group whose children reach left of / above its origin, and (-fx * w, -fy * h, w, h) for a Group with AUTO_SIZE off
 * (sizedFrame, #475). Hit area of the Group, its clip, SCENE_BOUNDS, snapshot, effect layer and the debug overlay use it. */
QRectF itemFrame(const TransformState& s, const QQuickItem* item);
/* Anchor point in item coordinates: per axis 0 for Left/Top (the item's origin), otherwise frame.left + f * width
 * (Center, Right/Bottom). Equal to f * size whenever the frame starts at the origin (every element but the Group). */
QPointF anchorPoint(const TransformState& s, const QQuickItem* item);
/* The item is a Group (ITEM, GroupItem in elements.cpp). */
bool isGroup(const QQuickItem* item);
/* Texture source of a Group used as ShaderEffect Input or texture uniform (#486): a hidden QQuickShaderEffectSource
 * whose sourceRect is the Group's frame (Elements::acquireGroupProxy); nullptr for other items or a Group without one.
 * Read by ShaderEffectItem on the render thread during sync (GUI thread blocked). */
QQuickItem* groupTextureSource(const QQuickItem* item);

/* State of one created element (owned by the Scene's id table). */
struct Node {
    QQuickItem* item = nullptr;
    quint16 type = 0;
    quint32 sink = 0;            // BIND sinkHandle (0 = unbound), see events.h
    QObject* transform = nullptr; // QML Matrix4x4, appended to item.transform on the first TRANSFORM or drag offset
    std::shared_ptr<TransformState> xf; // TRANSFORM + ANCHOR as sent (Elements::initGeometry, right after CREATE)
    SentState sent;              // last EV_MEASURED / EV_SCENE_BOUNDS sent for sink
    QQuickItem* effectSource = nullptr; // SHADER_EFFECT: current source item (see Elements::clearEffectSource)
    std::vector<QPointer<QQuickItem>> textureGroups; // SHADER_EFFECT: Groups of texture uniforms, one proxy reference each (#486)
    std::vector<quint32> handlers;      // ids of the input handlers attached to this item (#319), events.h
    QObject* mask = nullptr;            // TextMask (Text items with input handlers), events.h updateTextMask
    EffectChain* effectChain = nullptr; // SET_EFFECT_CHAIN (#489): QObject child of item, null = no chain (layer off)
    QQuickItem* maskHost = nullptr;     // this node is attached as mask of a link of that element's chain (nullptr = not)
    bool maskHidden = false;            // attached, or detached and parked hidden under the root until REPARENT/DESTROY
    bool autoSize = true;        // AUTO_SIZE (containers + Button, see applySize in elements.cpp)
    float size[2] = {0, 0};      // last SIZE (w, h), so AUTO_SIZE can re-apply it
    quint32 transformEpoch = 0;  // counts SET TRANSFORM: a running drag re-bases its offset on 0 after one (#209)
    bool debug = false;          // DEBUG: drawn by the window's debug overlay (debug.h), counted in Elements::debugCount
    bool polled = false;         // Elements::pollsState: the poll reads media/error/clicks/user value of this type
    bool pollListed = false;     // its id is in Scene::pollIds (listPolled, events.h)
    quint32 measuredStamp = 0;   // Scene::measureStamp of the last poll that visited it (one visit per poll, #20)
    quint32 polledStamp = 0;     // Scene::pollStamp of the last poll that read its state
};

/* The element type takes AUTO_SIZE (containers, Button, value inputs, ShaderEffect with Input), see applySize. */
bool takesAutoSize(const Node& n);
/* Its size currently follows the implicit size (takesAutoSize and AUTO_SIZE on); otherwise SIZE counts. */
inline bool isAutoSized(const Node& n) { return n.autoSize && takesAutoSize(n); }

struct PropDef;

/* A table property resolved against the QML type (once per type and engine). */
struct ResolvedProp {
    const PropDef* def = nullptr;
    QMetaProperty prop;  // property on the item ("color"), or the group property ("border")
    QMetaProperty sub;   // property on the group object ("color" of "border"); invalid if not grouped
};

class Elements;

/* Setter: returns false + err on a rejected value (-> ApplyBadValue). rp is resolved (or empty for
 * properties without a QML name, which use the C++ QQuickItem API). */
using Setter = bool (*)(Elements& els, Node& node, const ResolvedProp& rp, const Value& v, std::string& err);

struct PropDef {
    quint16 id;              // VLQT_PROP_*
    quint16 valueType;       // expected VLQT_VT_*
    Setter set;
    const char* qmlName;     // "color", "border.color" (group.sub), "font" (QFont, see modifyFont); nullptr = C++ API
};

struct TypeDef {
    quint16 id;              // VLQT_TYPE_*
    const char* name;
    const char* qml;         // QML one-liner (nullptr = plain QQuickItem, no component)
    std::vector<PropDef> props; // type-specific (ids >= 256); common ones (1..255) apply to every type
    QQuickItem* (*make)() = nullptr; // C++ item instead of QML (Image: QtImageItem, #361); qml is ignored then
};

const TypeDef* findType(quint16 type);

/* Per engine (= per window): component cache, resolved property cache, Matrix4x4 component. */
class Elements
{
public:
    explicit Elements(QQmlEngine* engine);
    ~Elements();
    Elements(const Elements&) = delete;
    Elements& operator=(const Elements&) = delete;

    /* The engine of this window (QmlItem compiles the user file on it, #104). */
    QQmlEngine* qmlEngine() const { return engine; }
    /* Creates an item of the given type (no parent). nullptr + err on failure. */
    QQuickItem* create(const TypeDef& type, std::string& err);
    /* Sets one property; returns an ApplyResult. Unknown properties are logged once per type. */
    int set(Node& node, quint16 prop, const Value& v, std::string& err);
    /* Geometry state of a new node (#469): node.xf, anchor placement (x/y = -anchor outside Row/Column) and a
     * width/height connection that re-places the item and re-applies the matrix on every size change. Called by the
     * Scene right after CREATE. */
    void initGeometry(Node& node);
    /* REPARENT: re-places the item for its new parent (free parent: x/y = -anchor; Row/Column: the layout's, a
     * translation folded into x/y moves to the Matrix4x4). */
    bool placeAfterReparent(Node& node, std::string& err);
    /* TRANSFORM: a pure x/y translation of an item without Matrix4x4 (outside Row/Column, no drag offset) only moves
     * x/y (perf); otherwise attaches a Matrix4x4 to item.transform once, afterwards only writes its matrix. */
    bool setMatrix(Node& node, const QMatrix4x4& m, std::string& err);
    /* ANCHOR as fractions (0 / 0.5 / 1) of the item's width/height: re-places the item (outside Row/Column) and
     * re-applies the matrix if one is attached. */
    bool setAnchor(Node& node, float fx, float fy, std::string& err);
    /* Drag offset (#209): composed into the same Matrix4x4 as T(offset) * TRANSFORM around the anchor, so the offset
     * moves the item in its parent's space, independent of its own rotation/scale. Attaches the Matrix4x4 on first
     * use (not for an offset 0 of an item without one). */
    bool setDragOffset(Node& node, float dx, float dy, std::string& err);

    /* Effect source bookkeeping (SHADER_EFFECT SOURCE). A QSGTextureProvider consumer keeps a pointer to its source, so the
     * Scene clears every effect referencing an item before deleting it (and before deleting an effect). */
    bool setEffectSource(Node& effect, const ResolvedProp& rp, QQuickItem* source, std::string& err);
    void clearEffectSource(Node& effect);
    /* Texture uniforms (#486): after SET_UNIFORMS, a Group referenced by a REF uniform gets a frame proxy (one reference per
     * effect and Group, acquired/released by diff); releaseTextureGroups drops them all (DESTROY of the effect). */
    void syncTextureGroups(Node& effect);
    void releaseTextureGroups(Node& effect);
    bool isEffectSource(const QQuickItem* item) const { return sourceRefs.count(item) != 0; }
    /* Disables the layer of former sources, deferred by one frame (#123). Called by the Scene before apply/poll. */
    void flushLayerOff();

    /* SET_EFFECT_CHAIN (#489, protocol.h "Effect chain"): replaces the node's chain with the validated description
     * (links in application order, empty = layer off and chain gone, releaseEffectLayer). masks: mask item per link
     * (nullptr = none), checked and parked under the root by the Scene (Scene::setEffectChain). The chain (EffectChain)
     * renders as an item tree in the item's layer.effect; the same structure only writes changed values. Masks: a new
     * one gets a hidden frame item created next to it with layer.enabled; the mask goes into an inner item whose
     * Matrix4x4 is the inverse of the node's parent mapping (position + TRANSFORM), so the frame's coordinates are the
     * node's own and the mask lies in the node's parent coordinates like a sibling (see updateMaskFrame). A mask no link
     * uses any more goes back next to its frame, hidden; the frame is deleted after the next frame (#123, flushLayerOff).
     * Returns an ApplyResult. */
    int setEffectChain(Node& node, const EffectChainDesc& chain, const std::vector<QQuickItem*>& masks, std::string& err);
    /* Switches the effect layer off and drops the chain (linkCount 0, DESTROY/RESET): its masks are detached (parked
     * hidden, see setEffectChain), stages and root go with the layer. */
    void releaseEffectLayer(Node& node);
    /* Detaches one mask from the link that uses it (DESTROY of the mask): that link runs without mask. */
    void detachEffectMask(Node& node, QQuickItem* mask);
    /* Masks attached to the node's chain. */
    std::vector<QQuickItem*> effectMasks(const Node& node) const;
    /* Re-computes the mask frames' inner matrix from the node's x/y and TRANSFORM (no-op without a mask). Called by
     * the x/y/size connections of the frames and by setMatrix/setAnchor/setDragOffset. */
    void updateMaskFrame(Node& node);
    bool isMaskLayer(const QQuickItem* item) const { return maskLayers.count(item) != 0; }
    /* Root item of the node's chain in its layer.effect (nullptr without a chain). */
    static QQuickItem* effectLayerItem(const Node& node);

    /* VIDEO: position + duration in seconds (0 until loaded). false for other types. */
    bool readMedia(const Node& node, float out[2]) const;
    /* Error text of the item (QML property vlqtError of the type: Image/Video could not be loaded, ...); empty if
     * there is no error or the type has no error state (#273). */
    QString readError(const Node& node) const;
    /* BUTTON: clicked signals since the last call (QML property vlqtClicks, reset to 0); 0 for other types. */
    quint32 takeClicks(const Node& node);
    /* Value inputs (SPIN_BOX, #210; #301 more): true + the value of the last user change (QML vlqtUserValue: int, real,
     * bool or string) if there was one since the last call (QML vlqtUserCount, reset to 0); false otherwise and for
     * other types. See "Value inputs" in elements.cpp. */
    bool takeUserValue(const Node& node, QVariant& out);
    /* The type has state the poll reads every frame (VIDEO media, vlqtError, vlqtClicks, vlqtUserCount); resolved once
     * the first item of the type exists (Elements::create). */
    bool pollsState(quint16 type) const;

    /* Number of live nodes with DEBUG on (set by the DEBUG setter, decremented by the Scene when such a node goes);
     * the Scene shows the debug overlay while it is > 0 or the window-wide switch is on. */
    int debugCount = 0;
    /* Parent of the Group frame proxies (#486): the Scene's root (untracked children do not count for REPARENT). */
    QQuickItem* proxyParent = nullptr;

private:
    struct TypeCache {
        QQmlComponent* component = nullptr;
        bool resolved = false;
        std::unordered_map<quint16, ResolvedProp> props; // only successfully resolved QML properties
        std::unordered_set<quint16> warned;              // unknown property ids already logged
        QMetaProperty error;                             // vlqtError (string); invalid if the type has none
        QMetaProperty clicks;                            // vlqtClicks (int, Button); invalid if the type has none
        QMetaProperty userCount;                         // vlqtUserCount (int, value inputs); invalid if none
        QMetaProperty userValue;                         // vlqtUserValue (value inputs); invalid if none
    };
    void resolve(const TypeDef& type, TypeCache& cache, QQuickItem* first);
    QObject* createTransform(std::string& err);
    /* Attaches the Matrix4x4 to item.transform once (node.xf must exist) and writes the current matrix. */
    bool writeTransform(Node& node, std::string& err);
    /* Group as texture (#486): one hidden QQuickShaderEffectSource per Group (sourceRect = frame, follows it, see
     * GroupItem::syncEffectFrame), shared by the Input and texture uniforms of all effects, refcounted; the last release
     * deletes it after the next frame (#123, frameDelete), a destroyed Group retires it as well. */
    QQuickItem* acquireGroupProxy(QQuickItem* group);
    void releaseGroupProxy(QQuickItem* group);

    QQmlEngine* engine;
    std::unordered_map<quint16, TypeCache> types;
    QQmlComponent* matrixComponent = nullptr;
    std::unordered_map<const QQuickItem*, int> sourceRefs; // item -> number of effects using it as source
    std::vector<QPointer<QQuickItem>> layerOff; // former effect sources, layer disabled after the next frame (#123)
    /* Mask frame of mask for node (protocol.h mask rule, setEffectChain); nullptr + err on failure. */
    QQuickItem* createMaskFrame(Node& node, QQuickItem* mask, QObject*& xf, std::string& err);
    /* Mask back next to frame, hidden; frame layer off + deleted after the next frame (#123). */
    void retireMaskFrame(Node& node, QQuickItem* mask, QQuickItem* frame);

    std::unordered_set<const QQuickItem*> effectLayers; // items with an effect chain (their layer stays on)
    std::unordered_set<const QQuickItem*> maskLayers;   // mask frames attached to a link (their layer stays on)
    std::vector<QPointer<QQuickItem>> frameDelete;      // detached mask frames, Group proxies, retired chain stages: deleted after the next frame (#123)
    std::unique_ptr<EffectComponents> effectComponents; // chain items, compiled on first use
    QMetaProperty mediaPosition, mediaDuration; // VIDEO wrapper vlqtPosition/vlqtDuration, resolved once
};

/* Helpers for setters. */
bool writeQml(QQuickItem* item, const ResolvedProp& rp, const QVariant& v, std::string& err);
/* Read-modify-write of a QFont property (rp.prop = "font"). */
template <class F>
bool modifyFont(QObject* item, const ResolvedProp& rp, F&& f, std::string& err);
/* #15: warns once per requested family (log category vlqt.font) when Qt does not have it and substitutes another font.
 * Empty family and families already checked: nothing. Qt GUI thread only (the apply path). */
void warnMissingFontFamily(const QString& family);

} // namespace vlqt

#include <QtGui/QFont>

template <class F>
bool vlqt::modifyFont(QObject* item, const ResolvedProp& rp, F&& f, std::string& err)
{
    QFont font = rp.prop.read(item).value<QFont>();
    f(font);
    if (!rp.prop.write(item, QVariant::fromValue(font))) {
        err = "writing QML property 'font' failed";
        return false;
    }
    return true;
}
