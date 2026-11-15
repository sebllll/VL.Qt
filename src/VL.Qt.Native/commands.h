/* commands.h - decoder/dispatcher of the command buffer (vlqt_window_apply) and the poll queue.
 * Protocol constants live in protocol.h. Element types and their properties are table entries
 * (elements.h/.cpp); plan 05 only adds entries, no new exports. */
#pragma once

#include "elements.h"

#include <QtCore/QByteArray>
#include <QtCore/QObject>
#include <QtCore/QString>
#include <QtCore/QtTypes>

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
class FeedbackLayer;

/* One scene per window: id table, bindings, event queue. Root id 0 = an item filling contentItem. */
struct Scene {
    Scene(QQuickWindow* window, QQmlEngine* engine);
    ~Scene();
    Scene(const Scene&) = delete;
    Scene& operator=(const Scene&) = delete;

    /* Applies all commands; stops at the first bad one (earlier ones stay applied). err names index + reason. */
    int apply(const unsigned char* buf, int len, std::string& err);
    /* Queues changed measured values of bound items (events.h), then copies queued events into out and clears
     * the queue; returns bytes written, -required if cap is too small (events stay queued). */
    int poll(unsigned char* out, int cap);
    /* Runs pending polish (layouts/positioners) of all scene items without a render, so poll measures current
     * geometry also when Qt did not render this pump (#215). Skipped when nothing changed since the last poll
     * (#71): no apply, no marked geometry, no window geometry change and no polish request pending in the window. */
    void polish();
    /* Set by apply, cleared by poll: the next polish must run (#71). */
    bool appliedSincePoll = true;

    /* Appends one record u16 event, u16 len, u32 handle, u32 index, payload (padded to 4);
     * len = 8 + payloadLen (handle + index + payload, without padding). */
    void pushEvent(quint16 ev, quint32 handle, quint32 index, const void* payload, quint16 payloadLen);

    /* Debug overlay (debug.h, #279): window-wide switch (vlqt_window_set_debug) and the refresh after commands. The
     * overlay is created on first use and shown while debugAll is on or any node has DEBUG (Elements::debugCount). */
    void setDebugAll(bool on);
    void updateDebug();
    const QQuickItem* debugOverlay() const; // nullptr until first used (perf: skipped in the item count)
    bool debugAll = false;

    /* Clear (#117, feedback.h): false keeps the previous frame through the feedback layer, which exists only while
     * clear is false; the first frame after switching off starts from the clear colour. The layer is derived state:
     * clearFrames and feedbackSamples are the state, the layer is built from them for the current window.
     * samples: MSAA of the layer; 0 (and 1) = the sample count of the target, which Qt's layer inherits (window: 4,
     * offscreen scene: 1), so the edges look as with Clear on. */
    void setClear(bool clear, int samples = 0);
    bool clear() const { return clearFrames; }
    /* Before the QQuickWindow is replaced (#118 MSAA change): drops the layer (its source is a child of the old
     * content item, its textures belong to the old window's QRhi), keeps the state. After the root moved to the new
     * window (and `window` points to it): rebuilds the layer from the state; the first frame starts from the clear
     * colour, as after a resize. */
    void releaseFeedback();
    void restoreFeedback();

    QQuickWindow* window;
    QQmlEngine* engine;
    QQuickItem* root;
    std::unordered_map<quint32, Node> nodes;
    std::unordered_map<const QQuickItem*, quint32> ids; // reverse map for recursive DESTROY
    std::unordered_map<quint32, HandlerNode> handlerNodes; // input handlers (#319), same id space as nodes, events.h
    QByteArray events;
    Elements elements; // component + property caches of this window's engine
    Handlers handlers; // QML components of the input handlers, events.h
    /* EV_WINDOW_GEOMETRY (events.h): set by the window's geometry/state signals (context = geometryWatch, so the
     * connections end with the scene), cleared by the poll that queues the record. */
    bool geometryDirty = true;
    /* Frame position of the last record: X11 delivers the frame margins (_NET_FRAME_EXTENTS) later than the
     * geometry and Qt has no signal for them, so a moved framePosition() also queues a record (#232). */
    QPoint reportedFramePos;
    QObject geometryWatch;
    /* Measure on change (#20, events.h): ids whose geometry changed since the last poll (markGeometry, no duplicates
     * until the poll), the walk stack and poll stamp of collectMeasurements (capacity reused, no per-frame allocation),
     * and the number of bound items whose geometry the last poll read (vlqt_test_measure_count). */
    std::vector<quint32> measureQueue;
    std::vector<QQuickItem*> measureStack;
    quint32 measureStamp = 0;
    int measuredLastPoll = 0;
    /* Ids of the nodes whose media/error/clicks/user value the poll reads (listPolled, events.h); entries of destroyed
     * nodes drop out in the next poll. pollStamp skips a second entry of a reused id. */
    std::vector<quint32> pollIds;
    quint32 pollStamp = 0;

    /* Details of the last apply (#92/#275): rc 0 = it succeeded. id = object id the bad command addresses (0 = none:
     * CLEAR_COLOR, RESET, truncated header, root), prop = property of a bad SET (else 0), reason = message without
     * the "apply: command #n ..." prefix. Read by vlqt_window_get_apply_error. */
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
    int setStyle(const unsigned char* p, int len, std::string& err); // SET_STYLE (vlstyle.h, #292)
    int setUniforms(const unsigned char* p, int len, std::string& err); // SET_UNIFORMS (ShaderEffect, #437)
    int setEffectChain(const unsigned char* p, int len, std::string& err); // SET_EFFECT_CHAIN (#489), effectchain.h
    int setQmlProperties(const unsigned char* p, int len, std::string& err); // SET_QML_PROPERTIES (QmlElement, #104)
    void reset();
    void destroySubtree(QQuickItem* item);
    void destroyHandlersOf(Node& node);
    /* Detaches mask from the link of host's chain that uses it (DESTROY of the mask); the mask stays parked hidden
     * under the root until REPARENT or DESTROY. Masks are attached by SET_EFFECT_CHAIN (protocol.h "Effect chain"). */
    void detachMask(Node& host, QQuickItem* mask);
    void deleteRetired();
    DebugOverlay* debug = nullptr; // child of contentItem, deleted in ~Scene
    std::unique_ptr<FeedbackLayer> feedback; // Clear off only (#117)
    bool clearFrames = true;  // Clear pin state (#117)
    int feedbackSamples = 0;  // MSAA of the layer, 0 = the target's (only the test hook sets another)
    std::vector<QQuickItem*> retired; // destroyed former effect sources, deleted after the next frame (#123)
};

} // namespace vlqt
