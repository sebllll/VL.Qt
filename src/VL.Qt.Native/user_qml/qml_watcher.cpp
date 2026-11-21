#include "user_qml/qml_watcher.h"

#include "user_qml/qml_item.h"

#include <QtCore/QList>
#include <QtCore/QMetaObject>
#include <QtQuick/QQuickItem>

namespace vlqt {

static_assert(QmlWatcher::kPropBase < QmlWatcher::kSignalBase, "property ids come before signal ids");
static_assert(QmlWatcher::kMaxProps > 0 && QmlWatcher::kMaxSignals > 0, "both id ranges hold at least one entry");
static_assert(QmlWatcher::kSignalBase + QmlWatcher::kMaxSignals == QmlWatcher::kIdLimit, "the signal ids end at the ushort limit");

bool isQmlGenerated(const QMetaObject* mo) { return mo && QByteArray(mo->className()).contains("_QML"); }

QmlWatcher::QmlWatcher(QmlItem* owner, QQuickItem* root) : m_owner(owner), m_root(root)
{
    const QMetaObject* mo = root->metaObject();
    // Declared in the user file: the QML-generated classes of the chain (a QML type derived from another QML type
    // counts with all its QML ancestors), not those of the native base (Item, Rectangle, ...).
    QList<const QMetaObject*> chain;
    for (const QMetaObject* m = mo; m && isQmlGenerated(m); m = m->superClass())
        chain.push_back(m);
    bool propsFull = false;
    for (const QMetaObject* m : chain) {
        for (int i = m->propertyOffset(); i < m->propertyCount(); ++i) {
            const QMetaProperty p = m->property(i);
            if (!p.isValid() || !p.hasNotifySignal())
                continue;
            const int k = int(m_props.size());
            if (k >= kMaxProps) {
                if (!propsFull)
                    m_owner->rejectRootProperty(QString::fromUtf8(p.name()),
                                                QStringLiteral("too many properties to watch (more than %1)").arg(kMaxProps));
                propsFull = true;
                continue;
            }
            Prop e;
            e.prop = p;
            e.name = p.name();
            m_props.push_back(e);
            m_propOfIndex.insert(i, k);
            QMetaObject::connect(root, p.notifySignalIndex(), this, kPropBase + k, Qt::DirectConnection);
        }
    }
    bool sigsFull = false;
    for (const QMetaObject* m : chain) {
        for (int i = m->methodOffset(); i < m->methodCount(); ++i) {
            const QMetaMethod mm = m->method(i);
            if (mm.methodType() != QMetaMethod::Signal || (mm.attributes() & QMetaMethod::Cloned))
                continue;
            bool isNotify = false;
            for (const Prop& p : m_props)
                if (p.prop.notifySignalIndex() == mm.methodIndex())
                    isNotify = true;
            if (isNotify)
                continue;
            const int j = int(m_sigs.size());
            if (j >= kMaxSignals) {
                if (!sigsFull)
                    m_owner->rejectRootProperty(QString::fromUtf8(mm.name()),
                                                QStringLiteral("too many signals to watch (more than %1)").arg(kMaxSignals));
                sigsFull = true;
                continue;
            }
            m_sigs.push_back(Sig{mm, mm.name()});
            QMetaObject::connect(root, mm.methodIndex(), this, kSignalBase + j, Qt::DirectConnection);
        }
    }
}

void QmlWatcher::take(std::vector<QmlPropertyEvent>& properties, std::vector<QmlSignalEvent>& signalEvents, bool& dropped)
{
    dropped = m_dropped;
    m_dropped = false;
    for (int k : m_dirty) {
        Prop& p = m_props[size_t(k)];
        p.dirty = false;
        QmlWireValue w;
        const QVariant value = p.prop.read(m_root);
        if (toWireValue(value, w)) {
            p.unsupported = false;
            properties.push_back(QmlPropertyEvent{p.name, std::move(w)});
        } else if (!p.unsupported) {
            p.unsupported = true;
            m_owner->rejectRootProperty(QString::fromUtf8(p.name),
                                        QStringLiteral("type '%1' cannot be sent to the patch").arg(QString::fromLatin1(value.typeName())));
        }
    }
    m_dirty.clear();
    for (QmlSignalEvent& s : m_signals)
        signalEvents.push_back(std::move(s));
    m_signals.clear();
}

int QmlWatcher::qt_metacall(QMetaObject::Call call, int id, void** argv)
{
    if (call != QMetaObject::InvokeMetaMethod || id < kPropBase)
        return QObject::qt_metacall(call, id, argv);
    if (id >= kSignalBase)
        onSignal(id - kSignalBase, argv);
    else
        onProperty(id - kPropBase);
    return -1;
}

void QmlWatcher::onProperty(int k)
{
    if (k < 0 || size_t(k) >= m_props.size() || k == m_suppress) // no echo of the patch's own set
        return;
    Prop& p = m_props[size_t(k)];
    if (!p.dirty) {
        p.dirty = true;
        m_dirty.push_back(k);
    }
}

void QmlWatcher::onSignal(int j, void** argv)
{
    if (j < 0 || size_t(j) >= m_sigs.size())
        return;
    if (m_signals.size() >= size_t(kQmlMaxSignalsPerPoll)) {
        m_dropped = true;
        return;
    }
    const Sig& s = m_sigs[size_t(j)];
    QmlSignalEvent e;
    e.name = s.name;
    const int n = s.method.parameterCount();
    for (int i = 0; i < n; ++i) {
        const QMetaType t = s.method.parameterMetaType(i);
        QVariant v;
        if (t == QMetaType::fromType<QVariant>())
            v = *static_cast<const QVariant*>(argv[i + 1]);
        else
            v = QVariant(t, argv[i + 1]);
        e.args.push_back(toWireValueOrString(v));
    }
    m_signals.push_back(std::move(e));
}

} // namespace vlqt
