// Private Qt API: QQuickShaderEffectSource (the frame proxy of a Group); check on Qt updates (list: elements/elements.h).
#include "elements/elements.h"
#include "elements/group_item.h"
#include "effects/shader_item.h"

#include <QtQml/QQmlProperty>
#include <QtQuick/QQuickItem>
#include <QtQuick/private/qquickshadereffectsource_p.h> // group bounds proxy

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
    // A Group goes through its bounds proxy: the layer would cover only (0, 0, width, height), not the part of the
    // group bounds left of / above the origin. Its own layer (Effect pin) stays untouched.
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
    // texture with sourceRect = the group bounds (like the snapshot's frameTarget). Hidden itself, so it never draws on
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
    group->syncEffectBounds();
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

} // namespace vlqt
