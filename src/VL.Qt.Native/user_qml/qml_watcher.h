/* user_qml/qml_watcher.h - QmlWatcher: what the root of a user .qml file declares, as events for the poll.
 *
 * It connects the notify signal of every property and every signal declared in the root (not those of the native base) to
 * itself, without a slot per signal. The connections carry raw ids: QMetaObject::connect(sender, signalIndex, receiver,
 * methodIndex) with a receiver that has no such method. Internally that is QMetaObjectPrivate::connect with no receiver
 * meta object, and the signal then calls QMetaObject::metacall(receiver, InvokeMetaMethod, methodIndex): the id arrives
 * unchanged in the override of qt_metacall. That behaviour of the private class is what this file relies on, so check it on
 * every Qt update (listed in the head comment of elements/elements.h).
 *
 * Deliberately no Q_OBJECT (moc would generate its own qt_metacall). Property k has the id kPropBase + k, signal j the id
 * kSignalBase + j. Qt stores the id in a ushort, so both ranges end below 65536; a root with more properties or signals
 * than the ranges hold is watched only up to the limit and the owner shows one error line for it.
 *
 * Reported through the owner (QmlItem::rejectRootProperty): a property whose value has no wire type, once per property.
 */
#pragma once

#include "user_qml/qml_wire.h"

#include <QtCore/QByteArray>
#include <QtCore/QHash>
#include <QtCore/QMetaMethod>
#include <QtCore/QMetaProperty>
#include <QtCore/QObject>

#include <vector>

class QQuickItem;

namespace vlqt {

class QmlItem;

/* Cap of signals kept per poll (protocol.h "QmlElement"); more are dropped and reported once. */
constexpr int kQmlMaxSignalsPerPoll = 256;

/* Native class behind a QML type: QML-generated meta objects are named "<Class>_QML_<n>" / "<Name>_QMLTYPE_<n>". */
bool isQmlGenerated(const QMetaObject* mo);

class QmlWatcher final : public QObject
{
public:
    static constexpr int kPropBase = 1000;
    static constexpr int kSignalBase = 30000;
    static constexpr int kIdLimit = 65536; // ushort
    static constexpr int kMaxProps = kSignalBase - kPropBase;
    static constexpr int kMaxSignals = kIdLimit - kSignalBase;

    QmlWatcher(QmlItem* owner, QQuickItem* root);

    /* Index into the table of the declared property with this index on the root's meta object, -1 if not watched. */
    int watchedIndex(int propertyIndex) const { return m_propOfIndex.value(propertyIndex, -1); }
    /* The watched property k does not report while it is suppressed (the patch's own set); -1 = none. */
    void suppress(int k) { m_suppress = k; }

    bool hasEvents() const { return !m_dirty.empty() || !m_signals.empty() || m_dropped; }
    /* The changes since the last call: last value of each changed property, every emitted signal. dropped: signals were
     * dropped over the cap since the last call. */
    void take(std::vector<QmlPropertyEvent>& properties, std::vector<QmlSignalEvent>& signalEvents, bool& dropped);

    int qt_metacall(QMetaObject::Call call, int id, void** argv) override;

private:
    struct Prop {
        QMetaProperty prop;
        QByteArray name;
        bool dirty = false;
        bool unsupported = false; // last value had no wire type (reported once through the owner's errors)
    };
    struct Sig {
        QMetaMethod method;
        QByteArray name;
    };

    void onProperty(int k);
    void onSignal(int j, void** argv);

    QmlItem* m_owner;
    QQuickItem* m_root;
    std::vector<Prop> m_props;
    std::vector<Sig> m_sigs;
    QHash<int, int> m_propOfIndex;
    std::vector<int> m_dirty;
    std::vector<QmlSignalEvent> m_signals;
    bool m_dropped = false;
    int m_suppress = -1;
};

} // namespace vlqt
