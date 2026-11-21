/* commands.h - decoder/dispatcher of the command buffer (vlqt_scene_apply) and the poll queue.
 * Protocol constants live in protocol.h. Element types and their properties are table entries
 * (elements.h/.cpp); plan 05 only adds entries, no new exports. */
#pragma once

#include "elements.h"

#include <QtCore/QByteArray>
#include <QtCore/QObject>
#include <QtCore/QString>
#include <QtCore/QtTypes>
#include <QtGui/QColor>

#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

class QObject;
class QQmlEngine;
class QQuickItem;
class QQuickWindow;

namespace vlqt {

class DebugOverlay;
class PreviousFrameLayer;

/* One scene per window: id table, bindings, event queue. Root id 0 = an item filling contentItem. */
class Scene
{
public:
    Scene(QQuickWindow* window, QQmlEngine* engine);
    ~Scene();
    Scene(const Scene&) = delete;
    Scene& operator=(const Scene&) = delete;

    /* Window swap (vlqt_window_set_samples): the root item moves to window's content item (size follow and
     * window-geometry watch reconnected, EVENT_WINDOW_GEOMETRY queued), the debug overlay is rebuilt there. Items, nodes,
     * bindings and the engine stay. Call before the old window is deleted (it owns the old debug overlay). */
    void moveToWindow(QQuickWindow* newWindow);

    /* Applies all commands; stops at the first bad one (earlier ones stay applied). err names index + reason. */
    int apply(const unsigned char* buf, int len, std::string& err);
    /* Queues changed measured values of bound items (events.h), then copies queued events into out and clears
     * the queue: a buffer export (vlqt.h); on VLQT_ERR_BUFFER_TOO_SMALL the events stay queued. */
    int poll(unsigned char* out, int cap, int* written, int* required);
    /* Runs pending polish (layouts/positioners) of all scene items without a render, so poll measures current
     * geometry also when Qt did not render this pump. Skipped when nothing changed since the last poll
     * (#71): no apply, no marked geometry, no window geometry change and no polish request pending in the window. */
    void polish();
    /* Set by apply, cleared by poll: the next polish must run. */
    bool appliedSincePoll = true;

    /* Appends one record u16 event, u16 flags (0), u32 len, u32 feedbackId, payload (padded to 4);
     * len = 4 + payloadLen (feedbackId + payload, without padding). A payload whose len would not fit a u32 is dropped. */
    void pushEvent(quint16 ev, quint32 feedbackId, const void* payload, qsizetype payloadLen);

    /* Debug overlay (debug.h): window-wide switch (vlqt_scene_set_debug) and the refresh after commands. The
     * overlay is created on first use and shown while debugAll is on or any node has DEBUG (Elements::debugCount). */
    void setDebugAll(bool on);
    void updateDebug();
    const QQuickItem* debugOverlay() const; // nullptr until first used (perf: skipped in the item count)
    bool debugAll = false;

    /* Clear (previous_frame_layer.h): false keeps the previous frame through the previous-frame layer, which exists only while
     * clear is false; the first frame after switching off starts from the clear colour. Set by VLQT_OP_SET_CLEAR, kept
     * by RESET. The layer is derived state: clearFrames is the state, the layer is built from it for the current
     * window. */
    void setClear(bool clear);
    bool clear() const { return clearFrames; }
    /* Space (VLQT_OP_SET_SPACE): vlqt_space of the scene, kept by RESET and across a window swap. QtNative moves
     * only the origin of the root (top left or centre of the scene); C# scales. ApplyBadValue for an unknown space. */
    int setSpace(quint32 space, std::string& err);
    quint32 space() const { return sceneSpace; }
    /* Root position from the space and the root size: top left, or the centre of the scene (follows resizes). */
    void updateRootOrigin();
    /* Clear colour (VLQT_OP_SET_CLEAR_COLOR, straight alpha; kept by RESET and across a window swap). With Clear on, or
     * Clear off and alpha 0 or 1, the window clears to it. With Clear off its alpha is the fade strength: the
     * layer draws it over the previous frame (alpha > 0), and for 0 < alpha < 1 the window clears to it opaque
     * (previous_frame_layer.h "Fade"). Runtime changes keep the layer. */
    void setClearColor(const QColor& color);
    /* Before the QQuickWindow is replaced (MSAA change): drops the layer (its source is a child of the old
     * content item, its textures belong to the old window's QRhi), keeps the state. After the root moved to the new
     * window (and `window` points to it): rebuilds the layer from the state; the first frame starts from the clear
     * colour, as after a resize. */
    void releasePreviousFrame();
    void restorePreviousFrame();

    QQuickWindow* window;
    QQmlEngine* engine;
    QQuickItem* root;
    std::unordered_map<quint32, ElementEntry> nodes;
    std::unordered_map<const QQuickItem*, quint32> ids; // reverse map for recursive DESTROY
    std::unordered_map<quint32, HandlerEntry> handlerNodes; // input handlers, same id space as nodes, events.h
    QByteArray events;
    Elements elements; // component + property caches of this window's engine
    Handlers handlers; // QML components of the input handlers, events.h
    /* EVENT_WINDOW_GEOMETRY (events.h): set by the window's geometry/state signals (context = geometryWatch, so the
     * connections end with the scene), cleared by the poll that queues the record. */
    bool geometryDirty = true;
    /* Frame position of the last record: X11 delivers the frame margins (_NET_FRAME_EXTENTS) later than the
     * geometry and Qt has no signal for them, so a moved framePosition() also queues a record. */
    QPoint reportedFramePos;
    QObject geometryWatch;
    /* Measure on change (events.h): ids whose geometry changed since the last poll (markGeometry, no duplicates
     * until the poll), the walk stack and poll stamp of collectMeasurements (capacity reused, no per-frame allocation). */
    std::vector<quint32> measureQueue;
    std::vector<QQuickItem*> measureStack;
    quint32 measureStamp = 0;
    /* Ids of the nodes whose media/error/clicks/user value the poll reads (listPolled, events.h); entries of destroyed
     * nodes drop out in the next poll. pollStamp skips a second entry of a reused id. */
    std::vector<quint32> pollIds;
    quint32 pollStamp = 0;

    /* Details of the last apply: rc 0 = it succeeded. id = object id the bad command addresses (0 = none:
     * CLEAR_COLOR, RESET, truncated header, root), prop = property of a bad SET (else 0), reason = message without
     * the "apply: command #n ..." prefix. Read by vlqt_scene_get_apply_error. */
    struct ApplyError {
        int rc = 0;
        int command = 0;
        quint32 id = 0;
        quint16 op = 0;
        quint16 prop = 0;
        std::string reason;
    } lastApplyError;

private:
    int create(quint32 id, quint16 type, std::string& err);
    int destroy(quint32 id, std::string& err);
    int reparent(quint32 id, quint32 parent, quint32 index, std::string& err);
    int set(const unsigned char* p, int len, std::string& err);
    int setStyle(const unsigned char* p, int len, std::string& err); // SET_STYLE (vlstyle.h)
    int setUniforms(const unsigned char* p, int len, std::string& err); // SET_UNIFORMS (ShaderEffect)
    int setEffectChain(const unsigned char* p, int len, std::string& err); // SET_EFFECT_CHAIN, effectchain.h
    int setQmlProperties(const unsigned char* p, int len, std::string& err); // SET_QML_PROPERTIES (QmlElement)
    void reset();
    /* root under window's content item, size follow, window-geometry watch (constructor, moveToWindow). */
    void attachRoot();
    void destroySubtree(QQuickItem* item);
    void destroyHandlersOf(ElementEntry& node);
    /* Detaches mask from the link of host's chain that uses it (DESTROY of the mask); the mask stays parked hidden
     * under the root until REPARENT or DESTROY. Masks are attached by SET_EFFECT_CHAIN (protocol.h "Effect chain"). */
    void detachMask(ElementEntry& host, QQuickItem* mask);
    void deleteRetired();
    DebugOverlay* debug = nullptr; // child of contentItem, deleted in ~Scene
    std::unique_ptr<PreviousFrameLayer> feedback; // Clear off only
    bool clearFrames = true; // Clear pin state
    quint32 sceneSpace = VLQT_SPACE_DIP_TOP_LEFT; // Space pin state
    QColor clearColor; // VLQT_OP_SET_CLEAR_COLOR; initially the colour the window was created with
    void applyClearColor(); // window clear and layer fill from clearColor and the layer
    bool feedbackSoftwareWarned = false; // Clear off ignored on the software backend, logged once per scene
    std::vector<QQuickItem*> retired; // destroyed former effect sources, deleted after the next frame
};

} // namespace vlqt
