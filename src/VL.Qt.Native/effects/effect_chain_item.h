/* effects/effect_chain_item.h - EffectChainItem, the root of an Effect chain: the item layer.effect creates. */
#pragma once

#include <QtCore/QObject>
#include <QtCore/QPointer>
#include <QtQuick/QQuickItem>

namespace vlqt {

/* Root of a chain = the item layer.effect creates (registered as VLQt.EffectChain/VlqtEffectChain). The layer sets
 * source (its ShaderEffectSource of the element) before completing it; the root then hands itself to the EffectChain
 * child of that element, which builds the stages under it. Created and deleted by the layer only. */
class EffectChainItem : public QQuickItem // not final: qmlRegisterType derives from it
{
    Q_OBJECT
    Q_PROPERTY(QObject* source READ source WRITE setSource NOTIFY sourceChanged)
public:
    explicit EffectChainItem(QQuickItem* parent = nullptr) : QQuickItem(parent) {}

    QObject* source() const { return m_source; }
    void setSource(QObject* s);

signals:
    void sourceChanged();

protected:
    void componentComplete() override;

private:
    void attach();

    QPointer<QObject> m_source;
};

} // namespace vlqt
