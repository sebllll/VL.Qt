#include "scene/measure.h"
#include "effects/effect_chain.h"
#include "user_qml/qml_item.h"
#include "scene/scene.h"
#include "wire.h"

#include <QtCore/QByteArray>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>

#include <vector>

namespace vlqt {
namespace {

/* EVENT_VALUE: u16 value type, u16 0, value encoded like SET (i32, f32, bool u8, string u32 len + UTF-8). The
 * value type follows the QML type of vlqtUserValue (int, real, bool, string); others are not sent. Only on user
 * changes, so the string copy does not allocate per frame. */
void pushValue(Scene& scene, quint32 sink, const QVariant& v)
{
    quint16 vt = 0;
    switch (v.typeId()) {
    case QMetaType::Int: vt = VLQT_VALUE_TYPE_I32; break;
    case QMetaType::Double:
    case QMetaType::Float: vt = VLQT_VALUE_TYPE_F32; break;
    case QMetaType::Bool: vt = VLQT_VALUE_TYPE_BOOL; break;
    case QMetaType::QString: vt = VLQT_VALUE_TYPE_STRING; break;
    default: return;
    }
    QByteArray payload;
    WireWriter w(payload);
    w.u16(vt).u16(0);
    switch (vt) {
    case VLQT_VALUE_TYPE_I32: w.i32(v.toInt()); break;
    case VLQT_VALUE_TYPE_F32: w.f32(float(v.toDouble())); break;
    case VLQT_VALUE_TYPE_BOOL: w.u8(v.toBool() ? 1 : 0); break;
    default: {
        const QByteArray utf8 = v.toString().toUtf8();
        w.u32(quint32(utf8.size())).bytes(utf8);
        break;
    }
    }
    scene.events.push(VLQT_EVENT_VALUE, sink, payload.constData(), payload.size());
}

/* QmlElement (protocol.h "QmlElement"): the changes of the root since the last poll as EVENT_QML_PROPERTY (one per
 * changed property, its last value) and EVENT_QML_SIGNAL (every emitted signal, in order). Records are 4-byte aligned inside
 * (name, value, argument), the poll record is padded by EventQueue::push.
 * Taken (and dropped) also without a sink so that nothing piles up; "signals dropped" is shown by QmlItem::error. */
void collectQml(Scene& scene, ElementEntry& n)
{
    auto* item = static_cast<QmlItem*>(n.item);
    if (!item->hasEvents())
        return;
    std::vector<QmlPropertyEvent>& props = scene.measure.qmlProperties;
    std::vector<QmlSignalEvent>& signalEvents = scene.measure.qmlSignals;
    props.clear();
    signalEvents.clear();
    bool dropped = false;
    item->takeEvents(props, signalEvents, dropped);
    if (n.feedbackId == 0)
        return;
    if (dropped)
        item->noteDropped();
    QByteArray& payload = scene.measure.qmlPayload;
    WireWriter w(payload);
    for (const QmlPropertyEvent& e : props) {
        payload.resize(0);
        w.u16(quint16(e.name.size())).u16(e.value.type).bytes(e.name).align().bytes(e.value.bytes);
        scene.events.push(VLQT_EVENT_QML_PROPERTY, n.feedbackId, payload.constData(), payload.size());
    }
    for (const QmlSignalEvent& e : signalEvents) {
        payload.resize(0);
        w.u16(quint16(e.name.size())).u16(quint16(e.args.size())).bytes(e.name).align();
        for (const QmlWireValue& a : e.args)
            w.u16(a.type).u16(0).bytes(a.bytes).align();
        scene.events.push(VLQT_EVENT_QML_SIGNAL, n.feedbackId, payload.constData(), payload.size());
    }
}

/* EVENT_MEASURED / EVENT_SCENE_BOUNDS of one bound node, each only on change. */
void measureGeometry(Scene& scene, ElementEntry& n)
{
    SentState& s = n.sent;
    const QQuickItem* item = n.item;

    const float m[2] = {float(item->implicitWidth()), float(item->implicitHeight())};
    if (!s.hasMeasured || m[0] != s.measured[0] || m[1] != s.measured[1]) {
        s.measured[0] = m[0];
        s.measured[1] = m[1];
        s.hasMeasured = true;
        scene.events.push(VLQT_EVENT_MEASURED, n.feedbackId, m, sizeof m);
    }

    // Item geometry (frame: an auto-sized Group may start in negative coordinates), not boundingRect():
    // QQuickText overrides that with the painted text rect.
    // In the coordinates of the window root: its origin is the origin of the Space (the root may sit at the centre).
    const QRectF r = item->mapRectToItem(scene.root, itemBounds(*n.xf, item));
    const float b[4] = {float(r.x()), float(r.y()), float(r.width()), float(r.height())};
    if (!s.hasBounds || b[0] != s.bounds[0] || b[1] != s.bounds[1] || b[2] != s.bounds[2] || b[3] != s.bounds[3]) {
        for (int i = 0; i < 4; ++i)
            s.bounds[i] = b[i];
        s.hasBounds = true;
        scene.events.push(VLQT_EVENT_SCENE_BOUNDS, n.feedbackId, b, sizeof b);
    }
}

/* Visits the queued nodes and every item below them once per poll: an ancestor's change moves the whole subtree in the
 * scene. Attached masks (effect chain) lie in their element's coordinates without being its children (mask frame,
 * Elements::updateMaskFrame): they go with the element. */
void collectGeometry(Scene& scene)
{
    if (scene.measure.measureQueue.empty())
        return;
    if (++scene.measure.measureStamp == 0) // wrapped: 0 is the stamp of nodes never visited
        ++scene.measure.measureStamp;
    const quint32 stamp = scene.measure.measureStamp;
    std::vector<QQuickItem*>& stack = scene.measure.measureStack;
    for (const quint32 id : scene.measure.measureQueue) {
        const auto it = scene.nodes.find(id);
        if (it == scene.nodes.end())
            continue; // destroyed since
        it->second.xf->measureQueued = false;
        stack.push_back(it->second.item);
        while (!stack.empty()) {
            QQuickItem* item = stack.back();
            stack.pop_back();
            if (const auto idIt = scene.ids.find(item); idIt != scene.ids.end()) {
                ElementEntry& n = scene.nodes.find(idIt->second)->second;
                if (n.measuredStamp == stamp)
                    continue; // visited with its subtree already
                n.measuredStamp = stamp;
                if (n.feedbackId != 0)
                    measureGeometry(scene, n);
                if (const EffectChain* ec = n.effectChain)
                    for (size_t i = 0; i < ec->linkCount(); ++i)
                        if (QQuickItem* mask = ec->link(i).multi.mask)
                            stack.push_back(mask);
            }
            const QList<QQuickItem*> children = item->childItems(); // shared copy; const: no detach, no allocation
            for (QQuickItem* c : children)
                stack.push_back(c);
        }
    }
    scene.measure.measureQueue.clear();
}

} // namespace

void bindNode(Scene& scene, ElementEntry& node, quint32 handle)
{
    if (node.feedbackId != handle) {
        node.sent = SentState{}; // new sink (or unbind): the next poll sends the current values again
        markGeometry(*node.xf);  // ... measured once more, unchanged or not
        scene.elements.takeClicks(node); // Button clicks not yet polled belonged to the old sink
        QVariant dropped;
        scene.elements.takeUserValue(node, dropped); // same for a user change of a value input
    }
    node.feedbackId = handle;
}

void markGeometry(TransformState& s)
{
    if (!s.scene || s.measureQueued)
        return;
    s.measureQueued = true;
    s.scene->measure.measureQueue.push_back(s.id);
}

void watchGeometry(Scene& scene, quint32 id, ElementEntry& node)
{
    TransformState& s = *node.xf;
    s.scene = &scene;
    s.id = id;
    // Width/height: the size connection of Elements::initGeometry marks. Context = the item: the connections end with it.
    auto mark = [xf = node.xf] { markGeometry(*xf); };
    QQuickItem* item = node.item;
    QObject::connect(item, &QQuickItem::xChanged, item, mark);
    QObject::connect(item, &QQuickItem::yChanged, item, mark);
    QObject::connect(item, &QQuickItem::implicitWidthChanged, item, mark);
    QObject::connect(item, &QQuickItem::implicitHeightChanged, item, mark);
    QObject::connect(item, &QQuickItem::parentChanged, item, mark); // REPARENT, parked (mask, texture), unparented
    markGeometry(s);
    listPolled(scene, node);
}

void listPolled(Scene& scene, ElementEntry& node)
{
    if (node.pollListed || (!node.polled && !node.effectChain))
        return;
    node.pollListed = true;
    scene.measure.pollIds.push_back(node.xf->id);
}

void collectMeasurements(Scene& scene)
{
    collectGeometry(scene);
    // Only the nodes with state to read (types with media/error/clicks/user value, effect chains): a scene of plain
    // elements costs nothing here. Entries of destroyed nodes, of a chain gone and second entries of a reused id go.
    if (++scene.measure.pollStamp == 0)
        ++scene.measure.pollStamp;
    std::vector<quint32>& ids = scene.measure.pollIds;
    for (size_t i = 0; i < ids.size();) {
        const auto it = scene.nodes.find(ids[i]);
        // A chain gone with its error still shown stays one more poll: it sends the cleared EVENT_ERROR.
        if (it == scene.nodes.end() || it->second.polledStamp == scene.measure.pollStamp
            || (!it->second.polled && !it->second.effectChain && it->second.sent.error.isEmpty())) {
            if (it != scene.nodes.end() && it->second.polledStamp != scene.measure.pollStamp)
                it->second.pollListed = false; // chain gone: listPolled adds it again with the next one
            ids[i] = ids.back();
            ids.pop_back();
            continue;
        }
        ++i;
        ElementEntry& n = it->second;
        n.polledStamp = scene.measure.pollStamp;
        if (n.type == VLQT_ELEMENT_TYPE_QML)
            collectQml(scene, n); // QML root changes and signals
        if (n.feedbackId == 0)
            continue;
        SentState& s = n.sent;
        float md[2];
        if (scene.elements.readMedia(n, md) && (!s.hasMedia || md[0] != s.media[0] || md[1] != s.media[1])) {
            s.media[0] = md[0];
            s.media[1] = md[1];
            s.hasMedia = true;
            scene.events.push(VLQT_EVENT_MEDIA, n.feedbackId, md, sizeof md);
        }

        // Error text: only on change; empty = cleared.
        const QString error = scene.elements.readError(n);
        if (error != s.error) {
            s.error = error;
            const QByteArray utf8 = error.toUtf8();
            scene.events.push(VLQT_EVENT_ERROR, n.feedbackId, utf8.constData(), utf8.size());
        }
        if (n.type == VLQT_ELEMENT_TYPE_QML)
            static_cast<QmlItem*>(n.item)->clearDropNotice(); // "signals dropped" is shown for one poll

        // Button: clicked signals, always on (Bang output).
        if (const quint32 bc = scene.elements.takeClicks(n))
            scene.events.push(VLQT_EVENT_CLICK, n.feedbackId, &bc, sizeof bc);

        // Value inputs: the value of the last user change, captured at the signal (protocol.h EVENT_VALUE).
        QVariant uv;
        if (scene.elements.takeUserValue(n, uv))
            pushValue(scene, n.feedbackId, uv);
    }
}

void watchWindowGeometry(Scene& scene)
{
    QQuickWindow* w = scene.window;
    QObject* ctx = &scene.measure.geometryWatch;
    auto dirty = [&scene] { scene.measure.geometryDirty = true; };
    QObject::connect(w, &QWindow::xChanged, ctx, dirty);
    QObject::connect(w, &QWindow::yChanged, ctx, dirty);
    QObject::connect(w, &QWindow::widthChanged, ctx, dirty);
    QObject::connect(w, &QWindow::heightChanged, ctx, dirty);
    QObject::connect(w, &QWindow::windowStateChanged, ctx, dirty);
    QObject::connect(w, &QWindow::screenChanged, ctx, dirty); // frame margins / DIP mapping may change
    QObject::connect(w, &QWindow::visibleChanged, ctx, dirty);
}

void collectWindowGeometry(Scene& scene)
{
    QQuickWindow* w = scene.window;
    // Same convention as vlqt_window_create: outer top-left (frame position), client size, DIP.
    // Fullscreen has no frame, but Qt (Windows) keeps reporting the old frame margins: client position.
    const Qt::WindowStates st = w->windowStates();
    const QPoint pos = st.testFlag(Qt::WindowFullScreen) ? w->position() : w->framePosition();
    if (!scene.measure.geometryDirty && pos == scene.measure.reportedFramePos)
        return;
    scene.measure.geometryDirty = false;
    scene.measure.reportedFramePos = pos;
    const QPoint client = w->position();
    struct { float x, y, w, h; quint32 flags; float cx, cy; } g{
        float(pos.x()), float(pos.y()), float(w->width()), float(w->height()),
        (st.testFlag(Qt::WindowMinimized) ? quint32(VLQT_WINDOW_STATE_MINIMIZED) : 0u)
            | (st.testFlag(Qt::WindowMaximized) ? quint32(VLQT_WINDOW_STATE_MAXIMIZED) : 0u)
            | (st.testFlag(Qt::WindowFullScreen) ? quint32(VLQT_WINDOW_STATE_FULLSCREEN) : 0u)
            | (w->isVisible() ? quint32(VLQT_WINDOW_STATE_VISIBLE) : 0u),
        float(client.x()), float(client.y())};
    static_assert(sizeof g == 28, "EVENT_WINDOW_GEOMETRY payload");
    scene.events.push(VLQT_EVENT_WINDOW_GEOMETRY, 0, &g, sizeof g);
}

} // namespace vlqt
