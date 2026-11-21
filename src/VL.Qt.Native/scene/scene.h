/* scene/scene.h - one scene per window: the table of its elements and input handlers, the structural commands, the window
 * swap and the entry points of apply and poll. The decoder of the command buffer is scene/commands.cpp; protocol constants
 * live in protocol.h. Element types and their properties are table entries (elements.h/.cpp): a new element type or
 * property adds entries, no new exports. */
#pragma once

#include "elements/elements.h"
#include "scene/clear_state.h"
#include "scene/commands.h"
#include "scene/event_queue.h"
#include "scene/handlers.h"
#include "scene/measure.h"

#include <QtCore/QtTypes>

#include <string>
#include <unordered_map>
#include <vector>

class QQmlEngine;
class QQuickItem;
class QQuickWindow;

namespace vlqt {

class DebugOverlay;

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
    /* Queues changed measured values of bound items (scene/measure.h) and the events of the bound input handlers
     * (scene/handlers.h), then copies queued events into out and clears the queue: a buffer export (vlqt.h); on
     * VLQT_ERR_BUFFER_TOO_SMALL the events stay queued. */
    int poll(unsigned char* out, int cap, int* written, int* required);
    /* Runs pending polish (layouts/positioners) of all scene items without a render, so poll measures current
     * geometry also when Qt did not render this pump. Skipped when nothing changed since the last poll:
     * no apply, no marked geometry, no window geometry change and no polish request pending in the window. */
    void polish();
    /* Set by apply, cleared by poll: the next polish must run. */
    bool appliedSincePoll = true;

    /* Debug overlay (debug.h): window-wide switch (vlqt_scene_set_debug) and the refresh after commands. The
     * overlay is created on first use and shown while debugAll is on or any node has DEBUG (Elements::debugCount). */
    void setDebugAll(bool on);
    void updateDebug();
    const QQuickItem* debugOverlay() const; // nullptr until first used (perf: skipped in the item count)
    bool debugAll = false;

    /* Space (VLQT_OP_SET_SPACE): vlqt_space of the scene, kept by RESET and across a window swap. QtNative moves
     * only the origin of the root (top left or centre of the scene); C# scales. ApplyBadValue for an unknown space. */
    int setSpace(quint32 space, std::string& err);
    quint32 space() const { return sceneSpace; }
    /* Root position from the space and the root size: top left, or the centre of the scene (follows resizes). */
    void updateRootOrigin();

    QQuickWindow* window;
    QQmlEngine* engine;
    QQuickItem* root;
    std::unordered_map<quint32, ElementEntry> nodes;
    std::unordered_map<const QQuickItem*, quint32> ids; // reverse map for recursive DESTROY
    std::unordered_map<quint32, HandlerEntry> handlerNodes; // input handlers, same id space as nodes, scene/handlers.h
    EventQueue events; // records for the next poll
    Elements elements; // component + property caches of this window's engine
    Handlers handlers; // QML components of the input handlers, scene/handlers.h
    MeasureState measure; // measure on change and window geometry, scene/measure.h
    ApplyError lastApplyError; // details of the last apply (scene/commands.h), read by vlqt_scene_get_apply_error

private:
    // Structural ops (scene/scene.cpp).
    int create(quint32 id, quint16 type, std::string& err);
    int destroy(quint32 id, std::string& err);
    int reparent(quint32 id, quint32 parent, quint32 index, std::string& err);
    void reset();
    // Payload decoders (scene/commands.cpp).
    int set(const unsigned char* p, int len, std::string& err);
    int setStyle(const unsigned char* p, int len, std::string& err); // SET_STYLE (vlstyle.h)
    int setUniforms(const unsigned char* p, int len, std::string& err); // SET_UNIFORMS (ShaderEffect)
    int setEffectChain(const unsigned char* p, int len, std::string& err); // SET_EFFECT_CHAIN, effectchain.h
    int setQmlProperties(const unsigned char* p, int len, std::string& err); // SET_QML_PROPERTIES (QmlElement)
    /* root under window's content item, size follow, window-geometry watch (constructor, moveToWindow). */
    void attachRoot();
    void destroySubtree(QQuickItem* item);
    void destroyHandlersOf(ElementEntry& node);
    /* Detaches mask from the link of host's chain that uses it (DESTROY of the mask); the mask stays parked hidden
     * under the root until REPARENT or DESTROY. Masks are attached by SET_EFFECT_CHAIN (protocol.h "Effect chain"). */
    void detachMask(ElementEntry& host, QQuickItem* mask);
    void deleteRetired();
    DebugOverlay* debug = nullptr; // child of contentItem, deleted in ~Scene
    ClearState clearState; // Clear pin, clear colour and the previous-frame layer of Clear off
    quint32 sceneSpace = VLQT_SPACE_DIP_TOP_LEFT; // Space pin state
    std::vector<QQuickItem*> retired; // destroyed former effect sources, deleted after the next frame
};

} // namespace vlqt
