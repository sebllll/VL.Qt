/* elements/elements.h - the elements of one window (class Elements): creates the items and sets their properties.
 *
 * Every element type is an entry of the type table (element_types.h, one type file each): type id, C++ factory or QML
 * file, property table. Rectangle, Text, Image, Shape, Row and Column are built in C++ and written through typed setters (no
 * QVariant per SET); the QML types (Video, the controls) have their QML properties resolved once per type
 * and engine to a QMetaProperty (on the first item), never looked up by name per frame. A new element property is a
 * table entry only, no new export.
 *
 * QT PRIVATE API - the one list, the checklist for a Qt update. Private classes are allowed here (LGPL; Qt promises no
 * compatibility for *_p.h, a limited one for qpa/ and rhi/), but every use is listed below and the file that has it
 * says so in its head comment. CMakeLists.txt links the modules GuiPrivate, QuickPrivate, QuickShapesPrivate and
 * ShaderToolsPrivate for them. After a Qt update: build, run tools/build.ps1 -Test, and check each entry (header path,
 * the members used). "Public way" is information only: nothing has to move to a public API.
 *
 *   QtCore/private/qabstractanimation_p.h   QAbstractAnimationTimer, QUnifiedTimer::startAnimationTimer: keep-alive of
 *       the vvvv clock (app/animation_clock.cpp); QUnifiedTimer::runningAnimationCount (diagnostics/perf_pages.cpp).
 *       Public way: partly, an endless QAbstractAnimation (duration() == -1, empty updateCurrentTime) started once also
 *       keeps the driver running, but counts as a running animation (the perf page must subtract it) and must never
 *       be paused.
 *   QtQuick/private/qquickitem_p.h          QQuickItemPrivate: dirtyAttributes, nextDirtyItem, childItems for the
 *       change count (app/scene_change.cpp); explicitVisible (elements/group_item.cpp); childItems and explicitVisible
 *       (elements/positioner_align.cpp). Public way: no.
 *   QtQuick/private/qquickwindow_p.h        QQuickWindowPrivate: dirtyItemList (app/scene_change.cpp),
 *       deliveryAgentPrivate() (app/input_delivery.cpp), itemsToPolish (scene/scene.cpp), renderControl (device/device.cpp),
 *       rhi, graphicsConfig, ensureCustomRenderTarget (offscreen/offscreen_scene.cpp, offscreen_binding.cpp). Public way: no.
 *   QtQuick/private/qquickdeliveryagent_p_p.h   QQuickDeliveryAgentPrivate::delayedTouch and
 *       deliverDelayedTouchEvent(): input at the frame start (app/input_delivery.cpp). Public way: no; the most fragile
 *       entry (a _p_p header).
 *   QtQuick/private/qquickrendercontrol_p.h QQuickRenderControlPrivate: resetRhi (offscreen/offscreen_scene.cpp),
 *       isRenderWindow, overridden by SceneRenderControlPrivate for the keyboard focus of a scene
 *       (offscreen/offscreen_input.cpp). Public way: no.
 *   QtQuick/private/qquickshadereffectsource_p.h   QQuickShaderEffectSource built and read in C++ (group bounds
 *       proxy, layers, previous frame, grabs: elements/group_item.cpp, effects/effect_chain_item.cpp,
 *       effects/layer_requests.cpp, scene/previous_frame_layer.cpp, snapshot_resource.cpp). Public way: only the QML type.
 *   QtQuick/private/qquickrectangle_p.h, qquicktext_p.h   QQuickRectangle and QQuickText built in C++ with typed
 *       setters (elements/type_rectangle.cpp, elements/type_text.cpp). Public way: the QML types, set through QMetaProperty.
 *   QtQuick/private/qquickpositioners_p.h   QQuickRow and QQuickColumn built in C++ with typed setters
 *       (elements/type_layout.cpp); QQuickBasePositioner paddings and positioningComplete (elements/positioner_align.cpp).
 *       Public way: the QML types, set through QMetaProperty.
 *   QtQuick/private/qquicktranslate_p.h     QQuickMatrix4x4::setMatrix without QVariant (elements/geometry.cpp).
 *   QtQuick/private/qquickpath_p.h          QQuickCurve, QQuickPathData: base of ShapePathElement
 *       (elements/shape_path_element.h). Public way: no.
 *   QtQuickShapes/private/qquickshape_p.h   QQuickShape, QQuickShapePath, QQuickShapeTrim built in C++ with typed
 *       setters (elements/type_shape.cpp). Public way: the QML types, set through QMetaProperty.
 *   QtQuick/private/qquickpointerdevicehandler_p.h   QQuickPointerDeviceHandler, base of TouchPointsHandler
 *       (scene/touch_handler.h). Public way: no.
 *   QtQuick/private/qsgplaintexture_p.h     QSGPlainTexture around the wrapped QRhiTexture, which it owns
 *       (resources/image_texture_cache.cpp). Public way: possibly QQuickWindow::createTextureFromRhiTexture (RHI API), which does not
 *       take ownership; not tried.
 *   QtQuick/private/qsgrenderloop_p.h       QSGRenderLoop::instance, to drop the window RHIs of a lost test device
 *       (test/test_device.cpp). Test code only.
 *   QtGui/private/qwindow_p.h               QWindowPrivate: positionPolicy (platform/platform_linux.cpp); visible,
 *       to grab an offscreen scene (snapshot_resource.cpp). Public way: no.
 *   QtGui/private/qhighdpiscaling_p.h       QHighDpi::toNativeWindowGeometry (platform/platform_linux.cpp);
 *       toNativeLocalPosition / toNativeGlobalPosition (test/test_input.cpp). Public way: for the test, multiply by
 *       devicePixelRatio(); for Linux borderless: no.
 *   QtGui/private/qeventpoint_p.h, qpointingdevice_p.h   QMutableEventPoint, QPointingDevicePrivate::pointById /
 *       removePointById: injected touch (window/touch_input.cpp, also the touch input of offscreen scenes, which
 *       ships). Public way: possibly, QWindowSystemInterface::handleTouchEvent registers the points itself; not
 *       verified for a render-control window without a platform window.
 *   QtGui/qpa/qplatformwindow.h             QPlatformWindow::setGeometry (platform/platform_linux.cpp). Public way: no.
 *   QtGui/qpa/qwindowsysteminterface.h      windowSystemEventsQueued / flushWindowSystemEvents (app/input_delivery.cpp),
 *       registerInputDevice (offscreen/offscreen_input.cpp), handle*Event, registerInputDevice and
 *       handleFocusWindowChanged for the tests (test/test_input.cpp). Public way: none to flush the queue without
 *       rendering.
 *   rhi/qrhi.h, qshader.h, qshaderdescription.h, qshaderbaker.h   RHI API (QRhi, QShader, QShaderBaker): in public
 *       and versioned include directories with limited compatibility guarantees (device/device.cpp,
 *       resources/gpu_texture.h, resources/image_kind.cpp, resources/image_texture_cache.cpp,
 *       resources/image_upload_rhi.cpp, diagnostics/perf_*.cpp, effects/shader_*, shader_resource.cpp).
 *
 * Not an include but a behaviour of Qt core that user_qml/qml_watcher.h relies on: QMetaObjectPrivate, raw method ids
 * through QMetaObject::connect. Check it on a Qt update as well.
 *
 * A new private include goes into this list and into the head comment of its file in the same change. */
#pragma once

#include "elements/element_entry.h"
#include "elements/element_types.h"
#include "elements/geometry.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QMetaProperty>
#include <QtCore/QPointer>
#include <QtCore/QString>
#include <QtCore/QVariant>
#include <QtGui/QMatrix4x4>

#include <memory>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

class QObject;
class QQmlComponent;
class QQmlEngine;
class QQuickItem;

Q_DECLARE_LOGGING_CATEGORY(lcElements)

namespace vlqt {

class QmlItem;          // user_qml/qml_item.h
struct EffectChainDesc;  // effects/effect_chain_desc.h
struct EffectComponents; // effects/effect_chain.h
class EffectChain;       // effects/effect_chain.h

/* Per engine (= per window): component cache, resolved property cache. */
class Elements
{
public:
    explicit Elements(QQmlEngine* engine);
    ~Elements();
    Elements(const Elements&) = delete;
    Elements& operator=(const Elements&) = delete;

    /* The engine of this window (QmlItem compiles the user file on it). */
    QQmlEngine* qmlEngine() const { return engine; }
    /* Creates an item of the given type (no parent). nullptr + err on failure. */
    QQuickItem* create(const TypeDef& type, std::string& err);
    /* Sets one property; returns an ApplyResult. Unknown properties are logged once per type. */
    int set(ElementEntry& node, quint16 prop, const Value& v, std::string& err);
    /* Geometry state of a new node: node.xf, anchor placement (x/y = -anchor outside Row/Column) and a
     * width/height connection that re-places the item and re-applies the matrix on every size change. Called by the
     * Scene right after CREATE. */
    void initGeometry(ElementEntry& node);
    /* REPARENT: re-places the item for its new parent (free parent: x/y = -anchor; Row/Column: the layout's, a
     * translation folded into x/y moves to the Matrix4x4). */
    void placeAfterReparent(ElementEntry& node);
    /* TRANSFORM: a pure x/y translation of an item without Matrix4x4 (outside Row/Column, no drag offset) only moves
     * x/y (perf); otherwise attaches a Matrix4x4 to item.transform once, afterwards only writes its matrix. */
    void setMatrix(ElementEntry& node, const QMatrix4x4& m);
    /* ANCHOR as fractions (0 / 0.5 / 1) of the item's width/height: re-places the item (outside Row/Column) and
     * re-applies the matrix if one is attached. */
    void setAnchor(ElementEntry& node, float fx, float fy);
    /* Drag offset: composed into the same Matrix4x4 as T(offset) * TRANSFORM around the anchor, so the offset
     * moves the item in its parent's space, independent of its own rotation/scale. Attaches the Matrix4x4 on first
     * use (not for an offset 0 of an item without one). */
    void setDragOffset(ElementEntry& node, float dx, float dy);

    /* Effect source bookkeeping (SHADER SOURCE). A QSGTextureProvider consumer keeps a pointer to its source, so the
     * Scene clears every effect referencing an item before deleting it (and before deleting an effect). */
    bool setEffectSource(ElementEntry& effect, const ResolvedProp& rp, QQuickItem* source, std::string& err);
    void clearEffectSource(ElementEntry& effect);
    /* Texture uniforms: after SET_UNIFORMS, a Group referenced by a REF uniform gets a frame proxy (one reference per
     * effect and Group, acquired/released by diff); releaseTextureGroups drops them all (DESTROY of the effect). */
    void syncTextureGroups(ElementEntry& effect);
    void releaseTextureGroups(ElementEntry& effect);
    bool isEffectSource(const QQuickItem* item) const { return sourceRefs.count(item) != 0; }
    /* Disables the layer of former sources, deferred by one frame. Called by the Scene before apply/poll. */
    void flushLayerOff();

    /* QML SOURCE / RELOAD: a QmlItem is compiled and created when the apply is done (Scene::apply), not when the command
     * comes, so that the SET_QML_PROPERTIES of the same frame are known at creation (required properties). The Scene calls
     * buildQml at the end of every apply, also a failed one. */
    void queueQmlBuild(QmlItem* item);
    void buildQml();

    /* SET_EFFECT_CHAIN (protocol.h "Effect chain"): replaces the node's chain with the validated description
     * (links in application order, empty = layer off and chain gone, releaseEffectLayer). masks: mask item per link
     * (nullptr = none), checked and parked under the root by the Scene (Scene::setEffectChain). The chain (EffectChain)
     * renders as an item tree in the item's layer.effect; the same structure only writes changed values. Masks: a new
     * one gets a hidden frame item created next to it with layer.enabled; the mask goes into an inner item whose
     * Matrix4x4 is the inverse of the node's parent mapping (position + TRANSFORM), so the frame's coordinates are the
     * node's own and the mask lies in the node's parent coordinates like a sibling (see updateMaskFrame). A mask no link
     * uses any more goes back next to its frame, hidden; the frame is deleted after the next frame (flushLayerOff).
     * Returns an ApplyResult. */
    int setEffectChain(ElementEntry& node, const EffectChainDesc& chain, const std::vector<QQuickItem*>& masks, std::string& err);
    /* Switches the effect layer off and drops the chain (linkCount 0, DESTROY/RESET): its masks are detached (parked
     * hidden, see setEffectChain), stages and root go with the layer. */
    void releaseEffectLayer(ElementEntry& node);
    /* Detaches one mask from the link that uses it (DESTROY of the mask): that link runs without mask. */
    void detachEffectMask(ElementEntry& node, QQuickItem* mask);
    /* Masks attached to the node's chain. */
    std::vector<QQuickItem*> effectMasks(const ElementEntry& node) const;
    /* Re-computes the mask frames' inner matrix from the node's x/y and TRANSFORM (no-op without a mask). Called by
     * the x/y/size connections of the frames and by setMatrix/setAnchor/setDragOffset. */
    void updateMaskFrame(ElementEntry& node);
    bool isMaskLayer(const QQuickItem* item) const { return maskLayers.count(item) != 0; }
    /* Root item of the node's chain in its layer.effect (nullptr without a chain). */
    static QQuickItem* effectLayerItem(const ElementEntry& node);

    /* VIDEO: position + duration in seconds (0 until loaded; QML properties vlqtPosition/vlqtDuration of the type).
     * false for types without them. */
    bool readMedia(const ElementEntry& node, float out[2]) const;
    /* Error text of the item (QML property vlqtError of the type: the Video player failed, ...) and of its effect
     * chain, joined; empty if there is no error or the type has no error state. */
    QString readError(const ElementEntry& node) const;
    /* BUTTON: clicked signals since the last call (QML property vlqtClicks, reset to 0); 0 for other types. */
    quint32 takeClicks(const ElementEntry& node);
    /* Value inputs (SPIN_BOX; more): true + the value of the last user change (QML vlqtUserValue: int, real,
     * bool or string) if there was one since the last call (QML vlqtUserCount, reset to 0); false otherwise and for
     * other types. See "Value inputs" in type_controls.cpp. */
    bool takeUserValue(const ElementEntry& node, QVariant& out);
    /* The type has state the poll reads every frame (vlqtPosition/vlqtDuration, vlqtError, vlqtClicks, vlqtUserCount);
     * resolved once the first item of the type exists (Elements::create). */
    bool pollsState(quint16 type) const;

    /* DEBUG of a node: the one place that counts the live nodes with DEBUG on. Called by the DEBUG setter and with false
     * by the Scene when the node goes. */
    void setDebug(ElementEntry& node, bool on);
    /* Some live node has DEBUG on: the Scene shows the debug overlay then (or while the window-wide switch is on). */
    bool anyDebug() const { return debugCount > 0; }
    /* Parent of the group bounds proxies: the Scene's root (untracked children do not count for REPARENT). */
    QQuickItem* proxyParent = nullptr;

private:
    struct TypeCache {
        // Compiled QML only: its items are deleted by the Scene before, the engine goes after the Scene.
        std::unique_ptr<QQmlComponent> component;
        bool resolved = false;
        std::unordered_map<quint16, ResolvedProp> props; // only successfully resolved QML properties
        std::unordered_set<quint16> warned;              // unknown property ids already logged
        QMetaProperty position, duration;                // vlqtPosition/vlqtDuration (qint64 ms, Video); invalid if none
        QMetaProperty error;                             // vlqtError (string); invalid if the type has none
        QMetaProperty clicks;                            // vlqtClicks (int, Button); invalid if the type has none
        QMetaProperty userCount;                         // vlqtUserCount (int, value inputs); invalid if none
        QMetaProperty userValue;                         // vlqtUserValue (value inputs); invalid if none
    };
    void resolve(const TypeDef& type, TypeCache& cache, QQuickItem* first);
    /* Attaches the Matrix4x4 to item.transform once (node.xf must exist) and writes the current matrix. */
    void writeTransform(ElementEntry& node);
    /* Group as texture: the Group's proxy (GroupItem::acquireProxy) under proxyParent, shared by the Input and texture
     * uniforms of all effects; a retired proxy (last release, destroyed Group) is deleted after the next frame (frameDelete). */
    QQuickItem* acquireGroupProxy(QQuickItem* group);
    void releaseGroupProxy(QQuickItem* group);

    QQmlEngine* engine;
    std::unordered_map<quint16, TypeCache> types;
    std::unordered_map<const QQuickItem*, int> sourceRefs; // item -> number of effects using it as source
    std::vector<QPointer<QQuickItem>> layerOff; // former effect sources, layer disabled after the next frame
    std::vector<QPointer<QQuickItem>> qmlBuilds; // QmlItems whose Source or Reload changed in this apply
    /* Mask frame of mask for node (protocol.h mask rule, setEffectChain). */
    QQuickItem* createMaskFrame(ElementEntry& node, QQuickItem* mask, QQuickMatrix4x4*& xf);
    /* Mask back next to frame, hidden; frame layer off + deleted after the next frame. */
    void retireMaskFrame(ElementEntry& node, QQuickItem* mask, QQuickItem* frame);

    std::unordered_set<const QQuickItem*> effectLayers; // items with an effect chain (their layer stays on)
    std::unordered_set<const QQuickItem*> maskLayers;   // mask frames attached to a link (their layer stays on)
    std::vector<QPointer<QQuickItem>> frameDelete;      // detached mask frames, Group proxies, retired chain stages: deleted after the next frame
    std::unique_ptr<EffectComponents> effectComponents; // chain items, compiled on first use
    int debugCount = 0; // live nodes with DEBUG on (setDebug)
};

} // namespace vlqt