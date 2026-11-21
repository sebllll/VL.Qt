#include "scene/scene.h"
#include "app/exports.h"
#include "audio_tap.h"
#include "debug.h"

#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
#include <QtQuick/private/qquickwindow_p.h> // itemsToPolish

#include <string>
#include <utility>
#include <vector>

namespace vlqt {

Scene::Scene(QQuickWindow* w, QQmlEngine* e)
    : window(w), engine(e), root(new QQuickItem()), elements(e), handlers(e), clearState(w->color())
{
    elements.proxyParent = root; // Group frame proxies of ShaderEffects
    // The origin of the root follows its size (centre spaces); the root's own signals, so a window swap keeps them.
    QObject::connect(root, &QQuickItem::widthChanged, root, [this] { updateRootOrigin(); });
    QObject::connect(root, &QQuickItem::heightChanged, root, [this] { updateRootOrigin(); });
    attachRoot();
}

void Scene::attachRoot()
{
    root->setParentItem(window->contentItem());
    // Sized to the window, not to the content item: Qt sizes the content item only for a shown window, so the root of
    // an offscreen scene would stay 0 x 0 (the previous-frame layer of Clear off draws nothing of an empty item).
    root->setSize(window->size());
    QObject::connect(window, &QWindow::widthChanged, root, [this](int width) { root->setWidth(width); });
    QObject::connect(window, &QWindow::heightChanged, root, [this](int height) { root->setHeight(height); });
    updateRootOrigin();
    watchWindowGeometry(*this);
}

void Scene::moveToWindow(QQuickWindow* newWindow)
{
    // The previous-frame layer of Clear off belongs to the old window: dropped here, rebuilt for the new one below.
    // The trail is lost; the first frame starts from the clear colour, as after a resize.
    clearState.release(window);
    // Cut the connections to the old window now: its deletion must not resize the root or mark the geometry.
    QObject::disconnect(window, nullptr, &measure.geometryWatch, nullptr);
    QObject::disconnect(window, nullptr, root, nullptr);
    window = newWindow;
    attachRoot();
    clearState.restore(window, root);
    measure.geometryDirty = true;
    // The overlay is a QObject child of the old content item: rebuilt under the new one if it was in use.
    delete std::exchange(debug, nullptr);
    updateDebug();
}

Scene::~Scene()
{
    // Items have no QObject parent (only parentItem), so every tracked item is deleted explicitly.
    // Effects are detached from their sources first (arbitrary deletion order below).
    clearState.destroyLayer(); // its view is a child of the root
    for (auto& kv : nodes)
        kv.second.xf->scene = nullptr; // the unparenting and deletes below emit geometry signals
    for (auto& kv : nodes) {
        elements.clearEffectSource(kv.second);
        if (kv.second.type == VLQT_ELEMENT_TYPE_VIDEO) { // see destroySubtree
            QMetaObject::invokeMethod(kv.second.item, "vlqtRelease");
            res::audio::releaseVideo(kv.second.item);
        }
    }
    for (auto& kv : nodes)
        kv.second.item->setParentItem(nullptr);
    for (auto& kv : nodes)
        delete kv.second.item; // attached input handlers are QObject children of their items
    handlerNodes.clear();
    deleteRetired(); // destroyed former effect sources of the last frame (not in nodes any more)
    nodes.clear();
    ids.clear();
    delete debug;
    delete root;
}

int Scene::setSpace(quint32 space, std::string& err)
{
    if (space > VLQT_SPACE_PIXEL_TOP_LEFT) {
        err = "unknown space " + std::to_string(space);
        return ApplyBadValue;
    }
    sceneSpace = space;
    updateRootOrigin();
    return ApplyOk;
}

void Scene::updateRootOrigin()
{
    // Only the origin moves, never a scale: layers and text of the elements keep rendering at item size x dpr (C# scales).
    const bool centre = sceneSpace == VLQT_SPACE_NORMALIZED || sceneSpace == VLQT_SPACE_DIP;
    root->setPosition(centre ? QPointF(root->width() / 2, root->height() / 2) : QPointF());
}

void Scene::setDebugAll(bool on)
{
    debugAll = on;
    updateDebug();
}

const QQuickItem* Scene::debugOverlay() const { return debug; }

void Scene::updateDebug()
{
    const bool on = debugAll || elements.debugCount > 0;
    if (!on && !debug)
        return; // never used: nothing to do, no per-frame cost
    if (!debug)
        debug = new DebugOverlay(window->contentItem(), *this);
    debug->setAll(debugAll);
    debug->setActive(on);
    debug->refresh();
}

int Scene::create(quint32 id, quint16 type, std::string& err)
{
    if (id == VLQT_ROOT_ID || nodes.count(id) || handlerNodes.count(id)) {
        err = id == VLQT_ROOT_ID ? "id 0 is the window root" : "id " + std::to_string(id) + " already exists";
        return ApplyInvalid;
    }
    if (isHandlerType(type)) {
        // Only the id, type and properties: the QML handler needs its parent item before completeCreate (REPARENT).
        HandlerEntry h;
        h.type = type;
        handlerNodes[id] = h;
        return ApplyOk;
    }
    const TypeDef* ti = findType(type);
    if (!ti) {
        err = "unknown type " + std::to_string(type);
        return ApplyUnknownType;
    }
    QQuickItem* item = elements.create(*ti, err);
    if (!item)
        return ApplyInvalid;
    ElementEntry& node = nodes[id] = ElementEntry{item, type, 0, 0, nullptr};
    elements.initGeometry(node); // anchor placement, follows size changes
    node.polled = elements.pollsState(type);
    ids[item] = id;
    watchGeometry(*this, id, node); // measure on change
    return ApplyOk;
}

void Scene::destroySubtree(QQuickItem* item)
{
    const auto children = item->childItems();
    for (QQuickItem* c : children)
        if (ids.count(c))
            destroySubtree(c);
    auto it = ids.find(item);
    ElementEntry& node = nodes[it->second];
    destroyHandlersOf(node); // before the item goes: handler QML objects, ids free again
    // stop the player and drop its source first, so the decoder and audio threads end before the item goes.
    // then the audio tap, while the player still exists (audio_tap.h releaseVideo).
    if (node.type == VLQT_ELEMENT_TYPE_VIDEO) {
        QMetaObject::invokeMethod(item, "vlqtRelease");
        res::audio::releaseVideo(item);
    }
    // Effect chain off first (layer, its root and stages; the masks of its links are detached and parked); a
    // SHADER source keeps its layer until flushLayerOff.
    const std::vector<QQuickItem*> masks = elements.effectMasks(node);
    elements.releaseEffectLayer(node);
    // QQuickMultiEffect holds a raw pointer to its source: detach before anything is deleted.
    elements.clearEffectSource(node);
    elements.releaseTextureGroups(node); // Group frame proxies of its texture uniforms
    // Masks (protocol.h "Effect chain"): the attached masks go with their element (not child items: parked under the
    // root); a destroyed mask is detached from its element first. Both are retired below like a former source.
    for (QQuickItem* mask : masks) {
        auto mit = ids.find(mask);
        if (mit == ids.end())
            continue;
        nodes[mit->second].maskHost = nullptr;
        destroySubtree(mask); // erases another node only: node stays valid
    }
    if (node.maskHost) {
        auto hostIt = ids.find(node.maskHost);
        if (hostIt != ids.end())
            detachMask(nodes[hostIt->second], item);
    }
    const bool wasSource = elements.isEffectSource(item) || node.maskHidden;
    if (wasSource)
        for (auto& kv : nodes)
            if (kv.second.effectSource == item)
                elements.clearEffectSource(kv.second);
    if (node.debug)
        --elements.debugCount;
    node.xf->scene = nullptr; // its connections may outlive the node (retired item): no more queue entries
    nodes.erase(it->second);
    ids.erase(it);
    if (!wasSource) {
        delete item;
        return;
    }
    // a former effect source is deleted only after the next frame (next poll or apply). QQuickMultiEffect's
    // source proxy keeps the old item as its output until it polishes in that frame and compares by pointer:
    // deleted right away, the replacement input created in the same apply could get the same address, the proxy
    // saw "unchanged" and the effect stayed empty for good (CI: 5 of 40). Its layer is switched off one frame
    // later as well (Elements::flushLayerOff). It stays in the tree (unparenting an item that a ShaderEffect still uses as source makes Qt adopt it), hidden
    // and moved behind its siblings so that REPARENT indices of this apply still count only live items.
    item->setVisible(false);
    if (QQuickItem* parent = item->parentItem()) {
        const auto siblings = parent->childItems();
        if (siblings.last() != item)
            item->stackAfter(siblings.last());
    }
    retired.push_back(item);
}

void Scene::detachMask(ElementEntry& host, QQuickItem* mask)
{
    elements.detachEffectMask(host, mask);
    auto it = ids.find(mask);
    if (it != ids.end())
        nodes[it->second].maskHost = nullptr; // stays maskHidden (parked) until REPARENT or DESTROY
}

void Scene::destroyHandlersOf(ElementEntry& node)
{
    const std::vector<quint32> handlerIds = node.handlers; // destroyHandler edits the list
    for (quint32 h : handlerIds)
        destroyHandler(*this, h);
}

void Scene::deleteRetired()
{
    elements.flushLayerOff();
    for (QQuickItem* item : retired)
        delete item;
    retired.clear();
}

int Scene::destroy(quint32 id, std::string& err)
{
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (handlerNodes.count(id)) {
            destroyHandler(*this, id);
            return ApplyOk;
        }
        err = id == VLQT_ROOT_ID ? "the window root cannot be destroyed" : "unknown id " + std::to_string(id);
        return id == VLQT_ROOT_ID ? ApplyInvalid : ApplyUnknownId;
    }
    destroySubtree(it->second.item);
    return ApplyOk;
}

void Scene::reset()
{
    // Top-level objects: parent is the root, nothing (created, never reparented) or an untracked item. Collected
    // first, destroySubtree changes the maps. Same path as DESTROY (video release, effect sources, retire).
    std::vector<QQuickItem*> top;
    for (const auto& kv : nodes) {
        QQuickItem* parent = kv.second.item->parentItem();
        if (!parent || !ids.count(parent))
            top.push_back(kv.second.item);
    }
    for (QQuickItem* item : top)
        if (ids.count(item)) // an attached mask (parked under the root) already went with its element
            destroySubtree(item);
    // Input handlers never attached (attached ones went with their items).
    std::vector<quint32> loose;
    for (const auto& kv : handlerNodes)
        loose.push_back(kv.first);
    for (quint32 h : loose)
        destroyHandler(*this, h);
}

int Scene::reparent(quint32 id, quint32 parentId, quint32 index, std::string& err)
{
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        auto hit = handlerNodes.find(id);
        if (hit != handlerNodes.end())
            return reparentHandler(*this, hit->second, id, parentId, err); // index ignored
        err = id == VLQT_ROOT_ID ? "the window root cannot be reparented" : "unknown id " + std::to_string(id);
        return id == VLQT_ROOT_ID ? ApplyInvalid : ApplyUnknownId;
    }
    QQuickItem* parent = root;
    bool underShaderEffect = false;
    if (parentId != VLQT_ROOT_ID) {
        auto pit = nodes.find(parentId);
        if (pit == nodes.end()) {
            const bool handler = handlerNodes.count(parentId) != 0;
            err = handler ? "parent " + std::to_string(parentId) + " is an input handler, not an element"
                          : "unknown parent id " + std::to_string(parentId);
            return handler ? ApplyInvalid : ApplyUnknownId;
        }
        parent = pit->second.item;
        underShaderEffect = pit->second.type == VLQT_ELEMENT_TYPE_SHADER;
    }
    QQuickItem* item = it->second.item;
    if (it->second.maskHost) {
        err = "element " + std::to_string(id) + " is attached as an effect mask";
        return ApplyInvalid;
    }
    for (QQuickItem* a = parent; a; a = a->parentItem())
        if (a == item) {
            err = "parent " + std::to_string(parentId) + " is inside the subtree of " + std::to_string(id);
            return ApplyInvalid;
        }
    if (item->parentItem() != parent) {
        item->setParentItem(parent); // appended as last child
        // Free parent: x/y = -anchor; Row/Column: its layout owns x/y and lays out the new child itself.
        if (!elements.placeAfterReparent(it->second, err))
            return ApplyInvalid;
    }
    it->second.maskHidden = false; // a detached mask comes back into the tree
    // Visibility is QtNative's alone (there is no VISIBLE property): children of a ShaderEffect (Input, texture
    // elements) are drawn only through their layers, every other element is shown (Opacity 0 = not drawn, input stays).
    item->setVisible(!underShaderEffect); // no-op when the explicit value is unchanged

    // Stacking order: index = position among the element siblings (clamped). Untracked items do not count: the
    // root of an effect chain (a sibling, kept right after its item by Qt), retired items, QML internals.
    QList<QQuickItem*> siblings = parent->childItems();
    // Masks parked under the root (attached or detached, maskHidden) do not count either.
    siblings.removeIf([&](QQuickItem* s) {
        if (s == item)
            return false;
        auto sit = ids.find(s);
        return sit == ids.end() || nodes[sit->second].maskHidden;
    });
    const qsizetype n = siblings.size();
    const qsizetype target = qsizetype(index) < n ? qsizetype(index) : n - 1;
    if (siblings[target] != item) {
        const qsizetype cur = siblings.indexOf(item);
        if (target < cur)
            item->stackBefore(siblings[target]);
        else
            item->stackAfter(siblings[target]);
    }
    return ApplyOk;
}

void Scene::polish()
{
    // Nothing changed since the last poll: ensurePolished calls updatePolish unconditionally, so the passes
    // below cost a call per element although nothing moves. A change after the last poll shows as an apply, a marked
    // geometry (scene/measure.h) or a window resize; a layout that has to follow a child (Row/Column, Text implicit size,
    // Group frame) requests polish() and is in the window's itemsToPolish until Qt polishes it in a render, so a
    // layout that needs the passes of another poll is not skipped either. Private API, check on Qt updates.
    if (!appliedSincePoll && measure.measureQueue.empty() && !measure.geometryDirty
        && QQuickWindowPrivate::get(window)->itemsToPolish.isEmpty())
        return;
    // Two passes: a parent's polish (e.g. Column positions, Row auto size) can change its children's size and
    // with that their layouts, and vice versa; the render's polish loop repeats the same way.
    for (int pass = 0; pass < 2; ++pass)
        for (auto& kv : nodes)
            if (kv.second.item)
                kv.second.item->ensurePolished();
}

int Scene::poll(unsigned char* out, int cap, int* written, int* required)
{
    if (const int rc = beginBuffer(out, cap, written, required); rc != VLQT_OK)
        return rc;
    deleteRetired(); // poll runs after processEvents: the effects have moved off retired sources
    appliedSincePoll = false; // polish (if Qt did not render) ran before this poll
    collectWindowGeometry(*this);
    collectMeasurements(*this);
    collectHandlers(*this);
    return events.drain(out, cap, written, required);
}

} // namespace vlqt
