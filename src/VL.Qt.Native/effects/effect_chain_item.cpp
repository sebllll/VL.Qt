// Private Qt API: QQuickShaderEffectSource (the layer's texture source); check on Qt updates (list: elements/elements.h).
#include "effects/effect_chain_item.h"

#include "effects/effect_chain.h"

#include <QtQuick/private/qquickshadereffectsource_p.h> // the layer's texture source: its sourceItem is the element

namespace vlqt {

void EffectChainItem::setSource(QObject* s)
{
    if (m_source == s)
        return;
    m_source = s;
    emit sourceChanged();
    if (isComponentComplete())
        attach();
}

void EffectChainItem::componentComplete()
{
    QQuickItem::componentComplete();
    attach();
}

void EffectChainItem::attach()
{
    auto* src = qobject_cast<QQuickShaderEffectSource*>(m_source.data());
    QQuickItem* element = src ? src->sourceItem() : nullptr;
    if (auto* chain = element ? element->findChild<EffectChain*>(QString(), Qt::FindDirectChildrenOnly) : nullptr)
        chain->attach(this);
    else
        qCWarning(lcEffectChain, "effect chain root without chain (source %p)", static_cast<void*>(m_source.data()));
}

} // namespace vlqt
