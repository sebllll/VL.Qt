#include "elements/elements.h"
#include "elements/geometry.h"
#include "elements/group_item.h"
#include "effectchain.h"
#include "effects/shader_item.h"

#include <QtQml/QQmlComponent>
#include <QtQml/QQmlListReference>
#include <QtQml/QQmlProperty>
#include <QtQuick/QQuickItem>
#include <QtQuick/private/qquickshadereffectsource_p.h> // Group frame proxy

#include <algorithm>

namespace vlqt {

namespace {

void setLayerEnabled(QQuickItem* item, bool on)
{
    // Once per source change, not per frame: QQmlProperty resolves the grouped "layer.enabled".
    QQmlProperty(item, QStringLiteral("layer.enabled")).write(on);
}

} // namespace

bool Elements::setEffectSource(ElementEntry& effect, const ResolvedProp& rp, QQuickItem* source, std::string& err)
{
    QQuickItem* old = effect.effectSource;
    if (old == source)
        return true;
    // A Group goes through its frame proxy: the layer would cover only (0, 0, width, height), not the part of the
    // frame left of / above the origin. Its own layer (Effect pin) stays untouched.
    QQuickItem* input = isGroup(source) ? acquireGroupProxy(source) : source;
    if (!writeQml(effect.item, rp, QVariant::fromValue(input), err)) {
        if (input != source)
            releaseGroupProxy(source);
        return false;
    }
    effect.effectSource = source;
    if (old) {
        if (isGroup(old))
            releaseGroupProxy(old); // the proxy goes after the next frame
        auto it = sourceRefs.find(old);
        if (it != sourceRefs.end() && --it->second <= 0) {
            sourceRefs.erase(it);
            // Free the layer texture, but only after the next frame: disabling the layer deletes its texture
            // provider right away, while the effect still renders from it until it polishes onto the new source
            // (use after free, crash 0xC0000409 when the input changes every frame). Visibility stays with the host.
            if (!isGroup(old))
                layerOff.emplace_back(old);
        }
    }
    if (source && ++sourceRefs[source] == 1) {
        if (!isGroup(source))
            setLayerEnabled(source, true);
        source->setVisible(false); // the effect draws it
    }
    return true;
}

void Elements::flushLayerOff()
{
    for (const QPointer<QQuickItem>& item : layerOff)
        if (item && !isEffectSource(item) && !effectLayers.count(item) && !maskLayers.count(item))
            setLayerEnabled(item, false);
    layerOff.clear();
    for (const QPointer<QQuickItem>& frame : frameDelete)
        delete frame.data(); // the mask itself was moved out on detach
    frameDelete.clear();
}

QQuickItem* Elements::acquireGroupProxy(QQuickItem* item)
{
    auto* group = static_cast<GroupItem*>(item);
    ++group->proxyRefs;
    if (group->proxy)
        return group->proxy;
    // A ShaderEffectSource renders its source item (also a hidden one: it refs it, refFromEffectItem) into its own
    // texture with sourceRect = the frame (like the snapshot's frameTarget). Hidden itself, so it never draws on
    // screen; it still gets updatePaintNode and its texture is rendered by the consuming ShaderItem's preprocess
    // without an extra refFromEffectItem on the proxy (checked by grab in the smoke test group-shader-source, also
    // live changes). Under the root, never under the Group (it would be part of its own texture) or an element (layout,
    // REPARENT indices count tracked items only). hideSource off: the host hides the Group already (REPARENT under the
    // ShaderEffect, setEffectSource). Not smooth, like the item layers used before (layer.smooth default false).
    auto* proxy = new QQuickShaderEffectSource();
    proxy->setParent(proxyParent); // deleted with the root at the latest
    proxy->setVisible(false);
    proxy->setLive(true);
    proxy->setHideSource(false);
    proxy->setSmooth(false);
    proxy->setParentItem(proxyParent);
    proxy->setSourceItem(group);
    group->proxy = proxy;
    group->syncEffectFrame();
    // A destroyed Group (DESTROY of a texture element, RESET) retires its proxy: the effects still sample it this frame.
    group->proxyRetire = QObject::connect(group, &QObject::destroyed, proxy, [this, proxy] { frameDelete.emplace_back(proxy); });
    return proxy;
}

void Elements::releaseGroupProxy(QQuickItem* item)
{
    auto* group = static_cast<GroupItem*>(item);
    if (--group->proxyRefs > 0 || !group->proxy)
        return;
    group->proxyRefs = 0;
    QQuickShaderEffectSource* proxy = group->proxy;
    group->proxy = nullptr;
    // Only our own connection: disconnecting every destroyed -> proxy connection would also drop the proxy's
    // sourceItemDestroyed, and ~QQuickShaderEffectSource would then use the deleted Group (window_destroy, or the Input
    // released and the Group destroyed in one apply).
    QObject::disconnect(group->proxyRetire);
    frameDelete.emplace_back(proxy); // the effect may sample it until it syncs onto the new texture
}

void Elements::syncTextureGroups(ElementEntry& effect)
{
    auto* fx = static_cast<ShaderItem*>(effect.item);
    if (!fx->takeRefsChanged())
        return;
    std::vector<QPointer<QQuickItem>> now;
    fx->forEachRef([&](QQuickItem* ref) {
        if (isGroup(ref) && std::find(now.begin(), now.end(), ref) == now.end())
            now.emplace_back(ref);
    });
    // Acquire first: a Group that stays keeps its proxy. A destroyed Group (its uniform only loses the pointer) must
    // re-sync the effect before the proxy goes: Qt deletes the proxy's texture after the next sync, the effect's material
    // would still hold it (use after free in EffectNode::preprocess, seen in the smoke test).
    for (const QPointer<QQuickItem>& g : now)
        if (std::find(effect.textureGroups.begin(), effect.textureGroups.end(), g) == effect.textureGroups.end()) {
            acquireGroupProxy(g);
            QObject::connect(g, &QObject::destroyed, effect.item, &QQuickItem::update, Qt::UniqueConnection);
        }
    for (const QPointer<QQuickItem>& g : effect.textureGroups)
        if (g && std::find(now.begin(), now.end(), g) == now.end()) {
            QObject::disconnect(g, &QObject::destroyed, effect.item, &QQuickItem::update);
            releaseGroupProxy(g);
        }
    effect.textureGroups.swap(now);
}

void Elements::releaseTextureGroups(ElementEntry& effect)
{
    for (const QPointer<QQuickItem>& g : effect.textureGroups)
        if (g) { // a deleted Group retired its proxy already
            QObject::disconnect(g, &QObject::destroyed, effect.item, &QQuickItem::update);
            releaseGroupProxy(g);
        }
    effect.textureGroups.clear();
}

void Elements::clearEffectSource(ElementEntry& effect)
{
    if (!effect.effectSource)
        return;
    auto it = types[effect.type].props.find(VLQT_PROPERTY_SOURCE);
    std::string err;
    if (it == types[effect.type].props.end() || !setEffectSource(effect, it->second, nullptr, err)) {
        // Should not happen (the source was set through the same property); never keep a dangling pointer.
        qCWarning(lcElements, "clearing effect source failed: %s", err.c_str());
        sourceRefs.erase(effect.effectSource);
        effect.effectSource = nullptr;
    }
}

// ---------------------------------------------------------------------------------------------
// Effect layer (SET_EFFECT_CHAIN) and the mask frames of its links.

int Elements::setEffectChain(ElementEntry& node, const EffectChainDesc& chain, const std::vector<QQuickItem*>& masks, std::string& err)
{
    if (chain.links.empty()) {
        releaseEffectLayer(node);
        return ApplyOk;
    }
    if (!effectComponents)
        effectComponents = std::make_unique<EffectComponents>(engine);
    EffectChain* ec = node.effectChain;
    if (!ec) {
        ec = node.effectChain = new EffectChain(*effectComponents, node.item); // dies with the item at the latest
        if (auto* group = dynamic_cast<GroupItem*>(node.item))
            ec->built = [group](QQuickItem* first) { // the chain is a child of the group: never outlives it
                group->effectHolder = first;
                group->syncEffectFrame();
            };
    }
    std::vector<QPointer<QQuickItem>> retired;
    std::vector<EffectChain::DroppedMask> dropped;
    const bool ok = ec->apply(chain, masks, retired, dropped, err);
    for (const QPointer<QQuickItem>& stage : retired)
        frameDelete.emplace_back(stage); // may still be sampled this frame
    for (const EffectChain::DroppedMask& d : dropped)
        retireMaskFrame(node, d.mask, d.frame);
    if (!ok)
        return ApplyBadValue;
    for (size_t i = 0; i < ec->links.size(); ++i) {
        EffectChain::Link& l = ec->links[i];
        if (!l.mask || l.maskFrame)
            continue;
        l.maskFrame = createMaskFrame(node, l.mask, l.maskFrameXf, err);
        if (!l.maskFrame) {
            l.mask = nullptr;
            return ApplyBadValue;
        }
        ec->syncMaskFrame(i);
    }
    if (!effectLayers.count(node.item)) {
        QQmlComponent* root = ec->rootComponent(err);
        // Component first, then enabled: activating the layer creates the root once, the root builds the stages.
        if (!root || !QQmlProperty(node.item, QStringLiteral("layer.effect")).write(QVariant::fromValue(root))) {
            if (err.empty())
                err = "setting layer.effect failed";
            return ApplyBadValue;
        }
        setLayerEnabled(node.item, true);
        effectLayers.insert(node.item);
    }
    updateMaskFrame(node);
    return ApplyOk;
}

void Elements::releaseEffectLayer(ElementEntry& node)
{
    EffectChain* ec = node.effectChain;
    if (!ec)
        return;
    for (QQuickItem* mask : effectMasks(node))
        detachEffectMask(node, mask);
    effectLayers.erase(node.item);
    // A SHADER source or an attached mask keeps its layer (an effect samples it), only the layer effect goes;
    // a Group source is sampled through its frame proxy, not its layer. The root and its stages go with it.
    if ((!isEffectSource(node.item) || isGroup(node.item)) && !isMaskLayer(node.item))
        setLayerEnabled(node.item, false);
    QQmlProperty(node.item, QStringLiteral("layer.effect")).write(QVariant::fromValue<QQmlComponent*>(nullptr));
    if (auto* group = dynamic_cast<GroupItem*>(node.item))
        group->effectHolder = nullptr;
    node.effectChain = nullptr;
    delete ec;
}

void Elements::detachEffectMask(ElementEntry& node, QQuickItem* mask)
{
    if (!node.effectChain || !mask)
        return;
    std::vector<EffectChain::Link>& links = node.effectChain->links;
    for (size_t i = 0; i < links.size(); ++i) {
        EffectChain::Link& l = links[i];
        if (l.mask != mask)
            continue;
        QQuickItem* frame = l.maskFrame;
        l.mask = nullptr;
        l.maskFrame = nullptr;
        l.maskFrameXf = nullptr;
        node.effectChain->syncMaskFrame(i); // detach before the frame goes: MultiEffect keeps a raw maskSource pointer
        retireMaskFrame(node, mask, frame);
    }
}

std::vector<QQuickItem*> Elements::effectMasks(const ElementEntry& node) const
{
    std::vector<QQuickItem*> masks;
    if (node.effectChain)
        for (const EffectChain::Link& l : node.effectChain->links)
            if (l.mask)
                masks.push_back(l.mask);
    return masks;
}

QQuickItem* Elements::createMaskFrame(ElementEntry& node, QQuickItem* mask, QObject*& xfOut, std::string& err)
{
    // Mask rule (protocol.h): the mask lies in the element's parent coordinates like a sibling, with its own SIZE,
    // ANCHOR and TRANSFORM. frame (hidden, layer on, the link's maskSource) > inner (Matrix4x4 = inverse of the
    // element's parent mapping, updateMaskFrame) > mask: frame coordinates are the element's own, so the layer's
    // sourceRect is the link's effect area as it is (stage Binding) and the texture lands exactly where a sibling would be.
    QObject* xf = createTransform(err);
    if (!xf)
        return nullptr;
    auto* frame = new QQuickItem(mask->parentItem()); // next to the parked mask (window root): outside every layout
    frame->setVisible(false);                         // texture source only, the effect draws it as alpha
    frame->setSize(QSizeF(1, 1)); // a 0-sized source item gives no texture, whatever sourceRect says
    auto* inner = new QQuickItem(frame);
    xf->setParent(inner);
    QQmlListReference list(inner, "transform");
    if (!list.isValid() || !list.append(xf)) {
        delete frame;
        err = "appending Matrix4x4 to the mask frame failed";
        return nullptr;
    }
    mask->setParentItem(inner);
    mask->setVisible(true); // inside the hidden frame: rendered into its layer only
    maskLayers.insert(frame);
    // Linear filtering: a rotated/scaled element samples the texture off the pixel grid.
    QQmlProperty(frame, QStringLiteral("layer.smooth")).write(true);
    setLayerEnabled(frame, true);
    // Position (anchor placement or Row/Column layout) and size (anchor) of the element; TRANSFORM/ANCHOR/drag offset call
    // updateMaskFrame themselves. Context = frame: disconnected on detach, gone with it. The Scene detaches the
    // masks before it erases the node, so the pointer never dangles.
    ElementEntry* n = &node;
    auto update = [this, n]() { updateMaskFrame(*n); };
    QObject::connect(node.item, &QQuickItem::xChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::yChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::widthChanged, frame, update);
    QObject::connect(node.item, &QQuickItem::heightChanged, frame, update);
    xfOut = xf;
    return frame;
}

void Elements::retireMaskFrame(ElementEntry& node, QQuickItem* mask, QQuickItem* frame)
{
    if (!frame)
        return;
    maskLayers.erase(frame);
    QObject::disconnect(node.item, nullptr, frame, nullptr);
    if (mask && frame->isAncestorOf(mask)) {
        mask->setParentItem(frame->parentItem()); // parked next to it again, hidden
        mask->setVisible(false);
    }
    // The effect may sample the frame until it polishes: layer off and delete after the next frame.
    layerOff.emplace_back(frame);
    frameDelete.emplace_back(frame);
}

void Elements::updateMaskFrame(ElementEntry& node)
{
    if (!node.effectChain)
        return;
    const auto& links = node.effectChain->links;
    if (std::none_of(links.begin(), links.end(), [](const EffectChain::Link& l) { return l.maskFrameXf; }))
        return;
    // Element -> parent, as Qt maps it: T(x, y) * item.transform (x/y = -anchor or the layout position; our Matrix4x4:
    // TRANSFORM around the anchor + drag offset). Its inverse brings parent coordinates (the mask's) into the element's (the effect area's).
    QMatrix4x4 m;
    m.translate(float(node.item->x()), float(node.item->y()));
    if (node.transform && node.xf)
        m *= pivoted(*node.xf, node.item);
    const QMatrix4x4 inv = m.inverted();
    for (const EffectChain::Link& l : links)
        if (l.maskFrameXf)
            setMatrixValue(l.maskFrameXf, inv);
}

QQuickItem* Elements::effectLayerItem(const ElementEntry& node)
{
    return node.effectChain ? node.effectChain->root() : nullptr;
}

} // namespace vlqt
