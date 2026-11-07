#include "commands.h"
#include "debug.h"
#include "effectchain.h"
#include "shadereffectitem.h"
#include "vlstyle.h"

#include <QtGui/QColor>
#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>

#include <algorithm>
#include <cstring>
#include <vector>

namespace vlqt {
namespace {

// ---------------------------------------------------------------------------------------------
// Wire helpers (little-endian host: Windows x64/arm64)

template <class T>
T rd(const unsigned char* p)
{
    T v;
    std::memcpy(&v, p, sizeof(T));
    return v;
}

constexpr int align4(int n) { return (n + (VLQT_COMMAND_ALIGN - 1)) & ~(VLQT_COMMAND_ALIGN - 1); }

const char* opName(quint16 op)
{
    switch (op) {
    case VLQT_OP_CREATE: return "CREATE";
    case VLQT_OP_DESTROY: return "DESTROY";
    case VLQT_OP_REPARENT: return "REPARENT";
    case VLQT_OP_SET: return "SET";
    case VLQT_OP_BIND: return "BIND";
    case VLQT_OP_CLEAR_COLOR: return "CLEAR_COLOR";
    case VLQT_OP_RESET: return "RESET";
    case VLQT_OP_SET_STYLE: return "SET_STYLE";
    case VLQT_OP_SET_UNIFORMS: return "SET_UNIFORMS";
    case VLQT_OP_SET_EFFECT_CHAIN: return "SET_EFFECT_CHAIN";
    default: return "?";
    }
}

/* Payload size of a SET value, -1 = unknown value type. Strings: fixed part only. */
int valueSize(quint16 vt)
{
    switch (vt) {
    case VLQT_VT_F32: return 4;
    case VLQT_VT_I32: return 4;
    case VLQT_VT_BOOL: return 1;
    case VLQT_VT_COLOR: return 16;
    case VLQT_VT_RECTF: return 16;
    case VLQT_VT_MAT4: return 64;
    case VLQT_VT_STRING: return 4;
    case VLQT_VT_REF: return 4;
    case VLQT_VT_U64: return 8;
    case VLQT_VT_SAMPLER: return 8;
    case VLQT_VT_VEC2: return 8;
    case VLQT_VT_VEC3: return 12;
    case VLQT_VT_VEC4: return 16;
    case VLQT_VT_IVEC2: return 8;
    default: return -1;
    }
}

QColor toColor(const float* f) { return QColor::fromRgbF(f[0], f[1], f[2], f[3]); }

/* Value type of a MULTI entry key (EFFECT_* 833..855), 0 = not a key (protocol.h "Effect chain"). */
quint16 effectValueType(quint16 prop)
{
    switch (prop) {
    case VLQT_PROP_EFFECT_BLUR_ENABLED:
    case VLQT_PROP_EFFECT_SHADOW_ENABLED:
    case VLQT_PROP_EFFECT_AUTO_PADDING:
    case VLQT_PROP_EFFECT_MASK_INVERTED:
        return VLQT_VT_BOOL;
    case VLQT_PROP_EFFECT_BLUR_MAX: return VLQT_VT_I32;
    case VLQT_PROP_EFFECT_SHADOW_COLOR:
    case VLQT_PROP_EFFECT_COLORIZATION_COLOR:
        return VLQT_VT_COLOR;
    case VLQT_PROP_EFFECT_PADDING_RECT: return VLQT_VT_RECTF;
    case VLQT_PROP_EFFECT_MASK_SOURCE: return VLQT_VT_REF;
    default:
        return prop > VLQT_PROP_EFFECT_ENABLED && prop <= VLQT_PROP_EFFECT_MASK_INVERTED ? VLQT_VT_F32 : 0;
    }
}

/* SAMPLER value (protocol.h "Sampler"): bytes filter, mip filter, address U, address V, 0, 0, 0, 0. */
bool samplerValueOk(const unsigned char* v)
{
    return v[0] <= 1 && v[1] <= 2 && v[2] <= 2 && v[3] <= 2 && v[4] == 0 && v[5] == 0 && v[6] == 0 && v[7] == 0;
}

/* Copies a value (type already checked, size = valueSize) from the wire; refs stay ids. */
EffectValue readEffectValue(quint16 vt, const unsigned char* vp)
{
    EffectValue v;
    v.type = vt;
    switch (vt) {
    case VLQT_VT_I32: v.i = rd<qint32>(vp); break;
    case VLQT_VT_IVEC2:
        v.i = rd<qint32>(vp);
        v.i2 = rd<qint32>(vp + 4);
        break;
    case VLQT_VT_BOOL: v.b = vp[0] != 0; break;
    case VLQT_VT_U64: v.u = rd<quint64>(vp); break;
    case VLQT_VT_SAMPLER: v.u = rd<quint64>(vp); break;
    case VLQT_VT_REF: v.ref = rd<quint32>(vp); break;
    default: std::memcpy(v.f, vp, size_t(valueSize(vt))); break; // f32, color, rectf, mat4, vec2/3/4
    }
    return v;
}

} // namespace

// ---------------------------------------------------------------------------------------------

Scene::Scene(QQuickWindow* w, QQmlEngine* e)
    : window(w), engine(e), root(new QQuickItem()), elements(e), handlers(e)
{
    QQuickItem* content = window->contentItem();
    root->setParentItem(content);
    root->setSize(content->size());
    elements.proxyParent = root; // Group frame proxies of ShaderEffects (#486)
    QObject::connect(content, &QQuickItem::widthChanged, root, [this, content] { root->setWidth(content->width()); });
    QObject::connect(content, &QQuickItem::heightChanged, root, [this, content] { root->setHeight(content->height()); });
    watchWindowGeometry(*this);
}

Scene::~Scene()
{
    // Items have no QObject parent (only parentItem), so every tracked item is deleted explicitly.
    // Effects are detached from their sources first (arbitrary deletion order below).
    for (auto& kv : nodes) {
        elements.clearEffectSource(kv.second);
        if (kv.second.type == VLQT_TYPE_VIDEO) // #130, see destroySubtree
            QMetaObject::invokeMethod(kv.second.item, "vlqtRelease");
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

int Scene::apply(const unsigned char* buf, int len, std::string& err)
{
    // Former effect sources destroyed in an earlier apply: the frame in between has polished the effects onto
    // their new sources, so nothing refers to them any more (see destroySubtree).
    deleteRetired();
    if (len < 0 || (len > 0 && !buf)) {
        err = "invalid buffer (null or negative length)";
        return ApplyBadLength;
    }
    lastApplyError = ApplyError{};
    int off = 0, index = 0;
    while (off < len) {
        quint16 op = 0;
        quint32 cmdId = 0;
        quint16 cmdProp = 0;
        int rc = ApplyOk;
        std::string reason;
        if (len - off < VLQT_HEADER_SIZE) {
            rc = ApplyBadLength;
            reason = "truncated header (" + std::to_string(len - off) + " bytes left)";
        } else {
            op = rd<quint16>(buf + off);
            const int plen = rd<quint16>(buf + off + 2);
            const unsigned char* p = buf + off + VLQT_HEADER_SIZE;
            const int size = align4(VLQT_HEADER_SIZE + plen);
            auto expect = [&](int n) {
                if (plen == n)
                    return true;
                rc = ApplyBadLength;
                reason = "payloadLen " + std::to_string(plen) + ", expected " + std::to_string(n);
                return false;
            };
            if (off + size > len) {
                rc = ApplyBadLength;
                reason = "command (" + std::to_string(size) + " bytes incl. padding) exceeds buffer ("
                    + std::to_string(len - off) + " bytes left)";
            } else {
                // Object addressed by the command, for the structured error (#92): first u32 of every op with an id.
                const bool hasId = op == VLQT_OP_CREATE || op == VLQT_OP_DESTROY || op == VLQT_OP_REPARENT
                    || op == VLQT_OP_SET || op == VLQT_OP_BIND || op == VLQT_OP_SET_STYLE || op == VLQT_OP_SET_UNIFORMS
                    || op == VLQT_OP_SET_EFFECT_CHAIN;
                if (hasId && plen >= 4)
                    cmdId = rd<quint32>(p);
                if (op == VLQT_OP_SET && plen >= 6)
                    cmdProp = rd<quint16>(p + 4);
                switch (op) {
                case VLQT_OP_CREATE:
                    if (expect(6))
                        rc = create(rd<quint32>(p), rd<quint16>(p + 4), reason);
                    break;
                case VLQT_OP_DESTROY:
                    if (expect(4))
                        rc = destroy(rd<quint32>(p), reason);
                    break;
                case VLQT_OP_REPARENT:
                    if (expect(12))
                        rc = reparent(rd<quint32>(p), rd<quint32>(p + 4), rd<quint32>(p + 8), reason);
                    break;
                case VLQT_OP_SET:
                    rc = set(p, plen, reason);
                    break;
                case VLQT_OP_SET_STYLE:
                    rc = setStyle(p, plen, reason);
                    break;
                case VLQT_OP_SET_UNIFORMS:
                    rc = setUniforms(p, plen, reason);
                    break;
                case VLQT_OP_SET_EFFECT_CHAIN:
                    rc = setEffectChain(p, plen, reason);
                    break;
                case VLQT_OP_BIND:
                    if (expect(10)) {
                        const quint32 id = rd<quint32>(p);
                        auto it = nodes.find(id);
                        auto hit = it == nodes.end() ? handlerNodes.find(id) : handlerNodes.end();
                        if (it != nodes.end()) {
                            bindNode(*this, it->second, rd<quint32>(p + 4)); // flags ignored (#320)
                        } else if (hit != handlerNodes.end()) {
                            bindHandler(*this, hit->second, rd<quint32>(p + 4)); // flags: none for handlers
                        } else {
                            rc = ApplyUnknownId;
                            reason = "unknown id " + std::to_string(id);
                        }
                    }
                    break;
                case VLQT_OP_CLEAR_COLOR:
                    if (expect(16)) {
                        float f[4];
                        std::memcpy(f, p, sizeof f);
                        window->setColor(toColor(f));
                    }
                    break;
                case VLQT_OP_RESET:
                    if (expect(0))
                        reset();
                    break;
                default:
                    rc = ApplyUnknownOp;
                    reason = "unknown opcode";
                    break;
                }
            }
            if (rc == ApplyOk)
                off += size;
        }
        if (rc != ApplyOk) {
            err = "apply: command #" + std::to_string(index) + " (op " + std::to_string(op) + " " + opName(op)
                + (cmdId ? ", id " + std::to_string(cmdId) : std::string()) + ", offset " + std::to_string(off)
                + "): " + reason;
            lastApplyError = ApplyError{rc, index, cmdId, op, cmdProp, std::move(reason)};
            return rc;
        }
        ++index;
    }
    return ApplyOk;
}

int Scene::create(quint32 id, quint16 type, std::string& err)
{
    if (id == VLQT_ROOT_ID || nodes.count(id) || handlerNodes.count(id)) {
        err = id == VLQT_ROOT_ID ? "id 0 is the window root" : "id " + std::to_string(id) + " already exists";
        return ApplyInvalid;
    }
    if (isHandlerType(type)) {
        // Only the id, type and properties: the QML handler needs its parent item before completeCreate (REPARENT).
        HandlerNode h;
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
    Node& node = nodes[id] = Node{item, type, 0, 0, nullptr};
    elements.initGeometry(node); // anchor placement, follows size changes (#469)
    ids[item] = id;
    return ApplyOk;
}

void Scene::destroySubtree(QQuickItem* item)
{
    const auto children = item->childItems();
    for (QQuickItem* c : children)
        if (ids.count(c))
            destroySubtree(c);
    auto it = ids.find(item);
    Node& node = nodes[it->second];
    destroyHandlersOf(node); // before the item goes: handler QML objects, ids free again
    // #130: stop the player and drop its source first, so the decoder and audio threads end before the item goes.
    if (node.type == VLQT_TYPE_VIDEO)
        QMetaObject::invokeMethod(item, "vlqtRelease");
    // Effect chain off first (layer, its root and stages; the masks of its links are detached and parked); a
    // SHADER_EFFECT source keeps its layer until flushLayerOff.
    const std::vector<QQuickItem*> masks = elements.effectMasks(node);
    elements.releaseEffectLayer(node);
    // QQuickMultiEffect holds a raw pointer to its source: detach before anything is deleted.
    elements.clearEffectSource(node);
    elements.releaseTextureGroups(node); // Group frame proxies of its texture uniforms (#486)
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
    nodes.erase(it->second);
    ids.erase(it);
    if (!wasSource) {
        delete item;
        return;
    }
    // #123: a former effect source is deleted only after the next frame (next poll or apply). QQuickMultiEffect's
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

void Scene::detachMask(Node& host, QQuickItem* mask)
{
    elements.detachEffectMask(host, mask);
    auto it = ids.find(mask);
    if (it != ids.end())
        nodes[it->second].maskHost = nullptr; // stays maskHidden (parked) until REPARENT or DESTROY
}

void Scene::destroyHandlersOf(Node& node)
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
    // first, destroySubtree changes the maps. Same path as DESTROY (video release, effect sources, #123 retire).
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
        underShaderEffect = pit->second.type == VLQT_TYPE_SHADER_EFFECT;
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
        // Free parent: x/y = -anchor; Row/Column: its layout owns x/y and lays out the new child itself (#469).
        if (!elements.placeAfterReparent(it->second, err))
            return ApplyInvalid;
    }
    it->second.maskHidden = false; // a detached mask comes back into the tree
    // Visibility is QtNative's alone since #474 (no VISIBLE property): children of a ShaderEffect (Input, texture
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

int Scene::set(const unsigned char* p, int plen, std::string& err)
{
    if (plen < 8) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET (>= 8)";
        return ApplyBadLength;
    }
    const quint32 id = rd<quint32>(p);
    const quint16 prop = rd<quint16>(p + 4);
    Value v;
    v.type = rd<quint16>(p + 6);
    const unsigned char* vp = p + 8;
    const int vlen = plen - 8;
    const int fixed = valueSize(v.type);
    if (fixed < 0) {
        err = "unknown value type " + std::to_string(v.type);
        return ApplyBadValue;
    }
    int expected = fixed;
    if (v.type == VLQT_VT_STRING && vlen >= 4)
        expected = 4 + int(rd<quint32>(vp));
    if (vlen != expected) {
        err = "value length " + std::to_string(vlen) + ", expected " + std::to_string(expected)
            + " for value type " + std::to_string(v.type);
        return ApplyBadLength;
    }

    auto it = nodes.find(id);
    auto hit = it == nodes.end() ? handlerNodes.find(id) : handlerNodes.end();
    if (it == nodes.end() && hit == handlerNodes.end()) {
        err = id == VLQT_ROOT_ID ? "the window root has no properties" : "unknown id " + std::to_string(id);
        return id == VLQT_ROOT_ID ? ApplyInvalid : ApplyUnknownId;
    }

    switch (v.type) {
    case VLQT_VT_F32:
    case VLQT_VT_COLOR:
    case VLQT_VT_RECTF:
    case VLQT_VT_MAT4:
    case VLQT_VT_VEC2:
    case VLQT_VT_VEC3:
    case VLQT_VT_VEC4:
        std::memcpy(v.f, vp, size_t(fixed));
        break;
    case VLQT_VT_I32: v.i = rd<qint32>(vp); break;
    case VLQT_VT_IVEC2:
        v.i = rd<qint32>(vp);
        v.i2 = rd<qint32>(vp + 4);
        break;
    case VLQT_VT_BOOL: v.b = vp[0] != 0; break;
    case VLQT_VT_U64: v.u = rd<quint64>(vp); break;
    case VLQT_VT_STRING:
        v.utf8 = QByteArray::fromRawData(reinterpret_cast<const char*>(vp + 4), vlen - 4);
        break;
    case VLQT_VT_REF: {
        const quint32 ref = rd<quint32>(vp);
        if (ref != VLQT_ROOT_ID) {
            auto rit = nodes.find(ref);
            if (rit == nodes.end()) {
                err = "unknown ref id " + std::to_string(ref);
                return ApplyUnknownId;
            }
            v.ref = rit->second.item;
        }
        break;
    }
    }

    if (hit != handlerNodes.end())
        return setHandler(*this, hit->second, prop, v, err);
    return elements.set(it->second, prop, v, err);
}

int Scene::setStyle(const unsigned char* p, int plen, std::string& err)
{
    if (plen < 8) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_STYLE (>= 8)";
        return ApplyBadLength;
    }
    const quint32 id = rd<quint32>(p);
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
            err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id))
                + " has no style (Controls elements only)";
            return ApplyUnknownProperty;
        }
        err = "unknown id " + std::to_string(id);
        return ApplyUnknownId;
    }
    const quint16 type = it->second.type;
    if (type < VLQT_TYPE_BUTTON || type > VLQT_TYPE_TEXT_FIELD) {
        err = "type " + std::to_string(type) + " has no style (Controls elements only)";
        return ApplyUnknownProperty;
    }
    return applySetStyle(it->second.item, p, plen, err);
}

int Scene::setUniforms(const unsigned char* p, int plen, std::string& err)
{
    // Interface stub (#437 task 1): validates the command completely (wire format: protocol.h head comment), applies
    // nothing yet. Task 3 hands the entries to the ShaderEffect item (table by name, CLEAR = full send).
    if (plen < 8) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_UNIFORMS (>= 8)";
        return ApplyBadLength;
    }
    const quint32 id = rd<quint32>(p);
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
            err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id))
                + " has no uniforms (ShaderEffect only)";
            return ApplyUnknownProperty;
        }
        err = "unknown id " + std::to_string(id);
        return ApplyUnknownId;
    }
    if (it->second.type != VLQT_TYPE_SHADER_EFFECT) {
        err = "type " + std::to_string(it->second.type) + " has no uniforms (ShaderEffect only)";
        return ApplyUnknownProperty;
    }
    const int count = rd<quint16>(p + 4);
    int off = 8;
    for (int i = 0; i < count; ++i) {
        if (plen - off < 4) {
            err = "uniform #" + std::to_string(i) + ": truncated entry";
            return ApplyBadLength;
        }
        const quint16 vt = rd<quint16>(p + off);
        const int nameLen = rd<quint16>(p + off + 2);
        const int size = vt == VLQT_VT_STRING ? -1 : valueSize(vt);
        if (size < 0) {
            err = "uniform #" + std::to_string(i) + ": value type " + std::to_string(vt) + " not allowed";
            return ApplyBadValue;
        }
        if (nameLen < 1 || nameLen > VLQT_UNIFORM_NAME_MAX) {
            err = "uniform #" + std::to_string(i) + ": name length " + std::to_string(nameLen) + " not in 1.."
                + std::to_string(VLQT_UNIFORM_NAME_MAX);
            return ApplyBadValue;
        }
        const int valueOff = off + align4(4 + nameLen);
        const int next = valueOff + align4(size);
        if (next > plen) {
            err = "uniform #" + std::to_string(i) + ": entry exceeds the payload";
            return ApplyBadLength;
        }
        if (vt == VLQT_VT_REF) {
            const quint32 ref = rd<quint32>(p + valueOff);
            if (ref != VLQT_ROOT_ID && !nodes.count(ref)) {
                err = "uniform #" + std::to_string(i) + ": unknown ref id " + std::to_string(ref);
                return ApplyUnknownId;
            }
        }
        if (vt == VLQT_VT_SAMPLER && !samplerValueOk(p + valueOff)) {
            err = "uniform #" + std::to_string(i) + ": sampler state out of range";
            return ApplyBadValue;
        }
        off = next;
    }
    if (off != plen) {
        err = "SET_UNIFORMS: " + std::to_string(plen - off) + " bytes after the last entry";
        return ApplyBadLength;
    }
    // Valid as a whole: apply (content problems become the element's error text, ShaderEffectItem::rebuild).
    auto* item = static_cast<ShaderEffectItem*>(it->second.item);
    item->beginUniforms((rd<quint16>(p + 6) & VLQT_UNIFORM_SET_CLEAR) != 0);
    off = 8;
    for (int i = 0; i < count; ++i) {
        const quint16 vt = rd<quint16>(p + off);
        const int nameLen = rd<quint16>(p + off + 2);
        const int valueOff = off + align4(4 + nameLen);
        QQuickItem* ref = nullptr;
        if (vt == VLQT_VT_REF) {
            const auto r = nodes.find(rd<quint32>(p + valueOff));
            ref = r != nodes.end() ? r->second.item : nullptr; // 0 (root) = no texture
        }
        item->setUniform(reinterpret_cast<const char*>(p + off + 4), nameLen, vt, p + valueOff, ref);
        off = valueOff + align4(valueSize(vt));
    }
    item->endUniforms();
    elements.syncTextureGroups(it->second); // Groups as textures go through their frame proxy (#486)
    item->refreshLayerRequests(true); // their proxies carry the layer mipmaps / format of the samplers (#492)
    return ApplyOk;
}

int Scene::setEffectChain(const unsigned char* p, int plen, std::string& err)
{
    // Validates the whole command first (wire format: protocol.h "Effect chain"), so a bad one changes nothing.
    if (plen < 8) {
        err = "payloadLen " + std::to_string(plen) + " too short for SET_EFFECT_CHAIN (>= 8)";
        return ApplyBadLength;
    }
    const quint32 id = rd<quint32>(p);
    auto it = nodes.find(id);
    if (it == nodes.end()) {
        if (id == VLQT_ROOT_ID || handlerNodes.count(id)) {
            err = (id == VLQT_ROOT_ID ? std::string("the window root") : "input handler " + std::to_string(id))
                + " has no effect chain (elements only)";
            return ApplyUnknownProperty;
        }
        err = "unknown id " + std::to_string(id);
        return ApplyUnknownId;
    }
    auto knownRef = [&](quint32 ref) { return ref == VLQT_ROOT_ID || nodes.count(ref) != 0; };
    const int linkCount = rd<quint16>(p + 4);
    EffectChainDesc chain;
    chain.links.reserve(size_t(linkCount));
    int off = 8;
    for (int l = 0; l < linkCount; ++l) {
        const std::string at = "link #" + std::to_string(l);
        if (plen - off < 8) {
            err = at + ": truncated link header";
            return ApplyBadLength;
        }
        EffectLinkDesc link;
        link.kind = rd<quint16>(p + off);
        const int count = rd<quint16>(p + off + 2);
        const quint32 bodyLen = rd<quint32>(p + off + 4);
        const int body = off + 8;
        if ((bodyLen & (VLQT_COMMAND_ALIGN - 1)) != 0 || bodyLen > quint32(plen - body)) {
            err = at + ": bodyLen " + std::to_string(bodyLen) + " not aligned or exceeds the payload";
            return ApplyBadLength;
        }
        const int end = body + int(bodyLen);
        int e = body;
        if (link.kind == VLQT_EFFECT_LINK_MULTI) {
            for (int i = 0; i < count; ++i) {
                if (end - e < 4) {
                    err = at + " entry #" + std::to_string(i) + ": truncated entry";
                    return ApplyBadLength;
                }
                const quint16 prop = rd<quint16>(p + e);
                const quint16 vt = rd<quint16>(p + e + 2);
                const quint16 expected = effectValueType(prop);
                if (!expected) {
                    err = at + " entry #" + std::to_string(i) + ": property " + std::to_string(prop)
                        + " is no MULTI key (EFFECT_* 833..855)";
                    return ApplyUnknownProperty;
                }
                if (vt != expected) {
                    err = at + " entry #" + std::to_string(i) + ": value type " + std::to_string(vt)
                        + ", property " + std::to_string(prop) + " takes " + std::to_string(expected);
                    return ApplyBadValue;
                }
                const int next = e + 4 + align4(valueSize(vt));
                if (next > end) {
                    err = at + " entry #" + std::to_string(i) + ": entry exceeds the body";
                    return ApplyBadLength;
                }
                EffectMultiEntry entry{prop, readEffectValue(vt, p + e + 4)};
                if (vt == VLQT_VT_REF && !knownRef(entry.value.ref)) {
                    err = at + " entry #" + std::to_string(i) + ": unknown ref id " + std::to_string(entry.value.ref);
                    return ApplyUnknownId;
                }
                link.multi.push_back(entry);
                e = next;
            }
        } else if (link.kind == VLQT_EFFECT_LINK_SHADER) {
            if (end - e < 4) {
                err = at + ": truncated SHADER head";
                return ApplyBadLength;
            }
            link.templ = rd<quint16>(p + e);
            const int shaderCount = rd<quint16>(p + e + 2);
            int roles = 0;
            switch (link.templ) {
            case VLQT_EFFECT_TEMPLATE_SINGLE:
            case VLQT_EFFECT_TEMPLATE_TWO_PASS: roles = 1; break;
            case VLQT_EFFECT_TEMPLATE_PYRAMID: roles = 2; break;
            default:
                err = at + ": unknown template " + std::to_string(link.templ);
                return ApplyBadValue;
            }
            if (shaderCount != roles) {
                err = at + ": template " + std::to_string(link.templ) + " takes " + std::to_string(roles)
                    + " shader id(s), got " + std::to_string(shaderCount);
                return ApplyBadValue;
            }
            e += 4;
            if (end - e < shaderCount * 8 + 24) {
                err = at + ": truncated SHADER head";
                return ApplyBadLength;
            }
            for (int s = 0; s < shaderCount; ++s, e += 8)
                link.shaders.push_back(rd<quint64>(p + e));
            std::memcpy(link.padding, p + e, sizeof link.padding);
            link.strength = rd<float>(p + e + 16);
            const int nameLen = rd<quint16>(p + e + 20);
            const bool twoPass = link.templ == VLQT_EFFECT_TEMPLATE_TWO_PASS;
            if (twoPass ? nameLen < 1 || nameLen > VLQT_UNIFORM_NAME_MAX : nameLen != 0) {
                err = at + ": strength name length " + std::to_string(nameLen)
                    + (twoPass ? " not in 1.." + std::to_string(VLQT_UNIFORM_NAME_MAX) : std::string(", expected 0"));
                return ApplyBadValue;
            }
            e += 24;
            if (end - e < align4(nameLen)) {
                err = at + ": strength name exceeds the body";
                return ApplyBadLength;
            }
            link.strengthUniform = QByteArray(reinterpret_cast<const char*>(p + e), nameLen);
            e += align4(nameLen);
            for (int i = 0; i < count; ++i) {
                const std::string ue = at + " uniform #" + std::to_string(i);
                if (end - e < 4) {
                    err = ue + ": truncated entry";
                    return ApplyBadLength;
                }
                const quint16 vt = rd<quint16>(p + e);
                const int uNameLen = rd<quint16>(p + e + 2);
                const int size = vt == VLQT_VT_STRING ? -1 : valueSize(vt);
                if (size < 0) {
                    err = ue + ": value type " + std::to_string(vt) + " not allowed";
                    return ApplyBadValue;
                }
                if (uNameLen < 1 || uNameLen > VLQT_UNIFORM_NAME_MAX) {
                    err = ue + ": name length " + std::to_string(uNameLen) + " not in 1.."
                        + std::to_string(VLQT_UNIFORM_NAME_MAX);
                    return ApplyBadValue;
                }
                const int valueOff = e + align4(4 + uNameLen);
                const int next = valueOff + align4(size);
                if (next > end) {
                    err = ue + ": entry exceeds the body";
                    return ApplyBadLength;
                }
                if (vt == VLQT_VT_SAMPLER && !samplerValueOk(p + valueOff)) {
                    err = ue + ": sampler state out of range";
                    return ApplyBadValue;
                }
                EffectUniform u{QByteArray(reinterpret_cast<const char*>(p + e + 4), uNameLen), readEffectValue(vt, p + valueOff)};
                if (vt == VLQT_VT_REF && !knownRef(u.value.ref)) {
                    err = ue + ": unknown ref id " + std::to_string(u.value.ref);
                    return ApplyUnknownId;
                }
                link.uniforms.push_back(std::move(u));
                e = next;
            }
        } else {
            err = at + ": unknown kind " + std::to_string(link.kind);
            return ApplyBadValue;
        }
        if (e != end) {
            err = at + ": " + std::to_string(end - e) + " bytes after the last entry of the body";
            return ApplyBadLength;
        }
        chain.links.push_back(std::move(link));
        off = end;
    }
    if (off != plen) {
        err = "SET_EFFECT_CHAIN: " + std::to_string(plen - off) + " bytes after the last link";
        return ApplyBadLength;
    }
    // Masks (protocol.h "Effect chain", one per MULTI run): all checked before anything changes.
    Node& host = it->second;
    std::vector<QQuickItem*> masks(chain.links.size(), nullptr);
    for (size_t l = 0; l < chain.links.size(); ++l)
        for (const EffectMultiEntry& e : chain.links[l].multi) {
            if (e.prop != VLQT_PROP_EFFECT_MASK_SOURCE || e.value.ref == VLQT_ROOT_ID)
                continue;
            const Node& mask = nodes[e.value.ref]; // existence checked above (knownRef)
            const std::string at = "link #" + std::to_string(l) + ": mask " + std::to_string(e.value.ref);
            for (QQuickItem* a = host.item; a; a = a->parentItem())
                if (a == mask.item) {
                    err = at + " is the element itself or one of its ancestors";
                    return ApplyInvalid;
                }
            if (mask.maskHost && mask.maskHost != host.item) {
                err = at + " is attached to another element";
                return ApplyInvalid;
            }
            if (!mask.maskHost && mask.item->parentItem() && !mask.maskHidden) {
                err = at + " already has a parent (REPARENT)";
                return ApplyInvalid;
            }
            if (std::find(masks.begin(), masks.end(), mask.item) != masks.end()) {
                err = at + " is the mask of another link";
                return ApplyInvalid;
            }
            masks[l] = mask.item;
        }
    const std::vector<QQuickItem*> before = elements.effectMasks(host);
    // A new mask is parked hidden under the root: outside the element's children, so it never counts for its layout,
    // childrenRect/auto size, hit-test or REPARENT indices. Elements moves it into its frame there.
    for (QQuickItem* m : masks)
        if (m && std::find(before.begin(), before.end(), m) == before.end()) {
            if (m->parentItem() != root)
                m->setParentItem(root);
            m->setVisible(false);
            nodes[ids[m]].maskHidden = true;
        }
    // Texture uniforms of SHADER links (REF): an element of its own that the host never REPARENTs, parked hidden under the
    // root like a mask (rendered through its layer, ShaderEffectItem::setUniform); problems are EV_ERROR lines.
    for (EffectLinkDesc& link : chain.links)
        for (EffectUniform& u : link.uniforms) {
            if (u.value.type != VLQT_VT_REF || u.value.ref == VLQT_ROOT_ID)
                continue;
            Node& tex = nodes[u.value.ref]; // existence checked above (knownRef)
            bool ancestor = false;
            for (QQuickItem* a = host.item; a; a = a->parentItem())
                ancestor = ancestor || a == tex.item;
            if (ancestor)
                u.refError = "the texture element must not be the element or one of its ancestors";
            else if (tex.maskHost || std::find(masks.begin(), masks.end(), tex.item) != masks.end())
                u.refError = "the texture element is the mask of an element";
            else if (tex.item->parentItem() && !tex.maskHidden)
                u.refError = "the texture element has a parent (REPARENT); a texture of a chain link is an element of its own";
            else if (isGroup(tex.item))
                u.refError = "a Group as texture of a chain link is not supported yet";
            if (u.refError)
                continue;
            if (tex.item->parentItem() != root)
                tex.item->setParentItem(root);
            tex.item->setVisible(false);
            tex.maskHidden = true; // parked until REPARENT or DESTROY (deleted one frame late, like a former source)
            u.refItem = tex.item;
        }
    const int rc = elements.setEffectChain(host, chain, masks, err);
    const std::vector<QQuickItem*> after = elements.effectMasks(host);
    for (QQuickItem* m : before)
        if (std::find(after.begin(), after.end(), m) == after.end())
            if (auto mit = ids.find(m); mit != ids.end())
                nodes[mit->second].maskHost = nullptr; // detached: stays maskHidden (parked) until REPARENT or DESTROY
    for (QQuickItem* m : after)
        nodes[ids[m]].maskHost = host.item;
    return rc;
}

void Scene::polish()
{
    // Two passes: a parent's polish (e.g. Column positions, Row auto size) can change its children's size and
    // with that their layouts, and vice versa; the render's polish loop repeats the same way.
    for (int pass = 0; pass < 2; ++pass)
        for (auto& kv : nodes)
            if (kv.second.item)
                kv.second.item->ensurePolished();
}

int Scene::poll(unsigned char* out, int cap)
{
    deleteRetired(); // poll runs after processEvents: the effects have moved off retired sources
    collectWindowGeometry(*this);
    collectMeasurements(*this);
    const int n = int(events.size());
    if (n == 0)
        return 0;
    if (!out || cap < n)
        return -n;
    std::memcpy(out, events.constData(), size_t(n));
    events.resize(0);
    return n;
}

void Scene::pushEvent(quint16 ev, quint32 handle, quint32 index, const void* payload, quint16 payloadLen)
{
    const quint16 len = quint16(8 + payloadLen); // handle + index + payload, without padding
    const qsizetype at = events.size();
    events.resize(at + align4(VLQT_HEADER_SIZE + len), '\0');
    char* d = events.data() + at;
    std::memcpy(d, &ev, 2);
    std::memcpy(d + 2, &len, 2);
    std::memcpy(d + 4, &handle, 4);
    std::memcpy(d + 8, &index, 4);
    if (payloadLen)
        std::memcpy(d + 12, payload, payloadLen);
}

} // namespace vlqt
