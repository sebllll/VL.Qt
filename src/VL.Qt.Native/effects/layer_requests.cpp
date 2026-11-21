// Private Qt API: QQuickShaderEffectSource (layer properties); check on Qt updates (list: elements/elements.h).
#include "effects/layer_requests.h"

#include <QtCore/QMetaObject>
#include <QtQml/QQmlProperty>
#include <private/qquickshadereffectsource_p.h>

#include <algorithm>
#include <unordered_map>
#include <vector>

namespace vlqt {

namespace {

struct LayerUser
{
    const ShaderItem* owner;
    bool mip;
};
struct LayerEntry
{
    std::vector<LayerUser> users;
    bool mip = false;
};
std::unordered_map<QQuickItem*, LayerEntry> s_layers; // main thread

void writeLayer(QQuickItem* item, bool mip)
{
    // A group frame proxy is the ShaderEffectSource itself, every other element has it as its layer.
    const QString prefix = qobject_cast<QQuickShaderEffectSource*>(item) ? QString() : QStringLiteral("layer.");
    QQmlProperty(item, prefix + QStringLiteral("mipmap")).write(mip);
}

// Applies the union of the requests of one element, drops the entry when nobody asks.
void settleLayer(QQuickItem* item)
{
    const auto it = s_layers.find(item);
    if (it == s_layers.end())
        return;
    LayerEntry& e = it->second;
    bool mip = false;
    for (const LayerUser& u : e.users)
        mip = mip || u.mip;
    if (e.users.empty()) {
        writeLayer(item, false);
        s_layers.erase(it);
    } else if (mip != e.mip) {
        e.mip = mip;
        writeLayer(item, mip);
    }
}

// Removes the request of owner from the entry of item; false when the element has no entry.
bool dropUser(QQuickItem* item, const ShaderItem* owner)
{
    const auto it = s_layers.find(item);
    if (it == s_layers.end())
        return false;
    auto& users = it->second.users;
    users.erase(std::remove_if(users.begin(), users.end(), [&](const LayerUser& x) { return x.owner == owner; }), users.end());
    return true;
}

} // namespace

void LayerRequests::request(QQuickItem* item, const ShaderItem* owner, bool mip)
{
    auto [it, added] = s_layers.try_emplace(item);
    if (added)
        QObject::connect(item, &QObject::destroyed, [item] { s_layers.erase(item); });
    auto& users = it->second.users;
    const auto u = std::find_if(users.begin(), users.end(), [&](const LayerUser& x) { return x.owner == owner; });
    if (u == users.end())
        users.push_back({owner, mip});
    else
        u->mip = mip;
    settleLayer(item);
}

void LayerRequests::release(QQuickItem* item, const ShaderItem* owner)
{
    if (dropUser(item, owner))
        settleLayer(item);
}

void LayerRequests::releaseQueued(QQuickItem* item, const ShaderItem* owner)
{
    if (dropUser(item, owner))
        QMetaObject::invokeMethod(item, [item] { settleLayer(item); }, Qt::QueuedConnection);
}

} // namespace vlqt
