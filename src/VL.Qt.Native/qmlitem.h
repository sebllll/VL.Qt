/* qmlitem.h - the QmlElement (protocol.h "QmlElement"): loads a user .qml file into the element tree.
 *
 * QmlItem is an item that holds the instance of the file's root (a QQuickItem) as its only child item. It
 *  - compiles the file from a file: URL on the engine of the scene (relative imports resolve), rebuilds the instance on
 *    QML_RELOAD (component cache of the engine cleared first) and applies every property set so far again,
 *  - sets properties of the root by name (SET_QML_PROPERTIES, QVariant conversion, errors stay with the name until the
 *    entry is set again or the file is reloaded),
 *  - watches the properties and signals declared in the root (QmlWatcher in qmlitem.cpp) and keeps their changes as wire
 *    records for the poll (scene/measure.cpp collectQml): the last value of each changed property, every emitted signal
 *    (cap kQmlMaxSignalsPerPoll). A change caused by the patch's own set is not recorded (no echo, like EVENT_VALUE).
 *  - reports problems as the QML property vlqtError (load/compile/type errors with file:line, rejected properties), which
 *    the poll sends as EVENT_ERROR like for Image and Video (Elements::readError).
 * Size: Auto Size on = the item takes the root's width/height (its implicit size, or a width/height the QML declares),
 * off = the item's size is written to the root.
 */
#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QHash>
#include <QtCore/QList>
#include <QtCore/QMap>
#include <QtCore/QPointer>
#include <QtCore/QString>
#include <QtCore/QVariant>
#include <QtQuick/QQuickItem>

#include <utility>
#include <vector>

class QQmlEngine;
class QQuickWindow;

namespace vlqt {

class QmlWatcher;

/* Cap of signals kept per poll (protocol.h "QmlElement"); more are dropped and reported once. */
constexpr int kQmlMaxSignalsPerPoll = 256;

/* One record of the back channel, values already in wire form (VLQT_VALUE_TYPE_*, bytes like SET: i32, f32, bool u8, string u32
 * length + UTF-8, color/rectf/vecN as f32), not yet padded. */
struct QmlWireValue {
    quint16 type = 0;
    QByteArray bytes;
};
struct QmlPropertyEvent {
    QByteArray name; // UTF-8
    QmlWireValue value;
};
struct QmlSignalEvent {
    QByteArray name; // UTF-8
    std::vector<QmlWireValue> args;
};

/* QVariant (property value or signal argument) to wire form. false = type not supported. */
bool toWireValue(const QVariant& v, QmlWireValue& out);

class QmlItem : public QQuickItem
{
    Q_OBJECT
    Q_PROPERTY(QString vlqtError READ error) // read by the poll (Elements::readError), see the head comment
public:
    explicit QmlItem(QQuickItem* parent = nullptr);
    ~QmlItem() override;

    /* QML_SOURCE: absolute path, empty = nothing loaded. Same path again: nothing. */
    void setSource(QQmlEngine* engine, const QString& path);
    /* QML_RELOAD: a counter value different from the last one (initially 0, the C# default that is never sent) rebuilds the
     * instance with a cleared component cache when a file is loaded; without a file the value is only recorded. */
    void setReload(qint32 counter);
    /* AUTO_SIZE (see the head comment). */
    void setAutoSize(bool on);
    /* SET_QML_PROPERTIES: remembers the value (re-applied after a reload) and writes it to the root if one is loaded. A
     * property that does not exist, is read-only or cannot take the value shows in vlqtError, nothing else happens. */
    void setRootProperty(const QString& name, const QVariant& value);
    /* The patch sent something that cannot be a QML value (unsupported value type): error line for that name. */
    void rejectRootProperty(const QString& name, const QString& message);

    QString error() const { return m_errorText; }
    /* Changes of the root since the last call (see the head comment); false = none. dropped: signals were dropped over
     * the cap since the last call. */
    bool hasEvents() const;
    void takeEvents(std::vector<QmlPropertyEvent>& properties, std::vector<QmlSignalEvent>& signalEvents, bool& dropped);
    /* "signals dropped" is part of vlqtError for one poll: the poll calls these around its EVENT_ERROR check. */
    void noteDropped();
    void clearDropNotice();

protected:
    void geometryChange(const QRectF& newGeometry, const QRectF& oldGeometry) override;

private:
    void rebuild(bool clearCache);
    void teardown();
    void retire(QQuickItem* old);
    void flushRetired();
    void applyProperty(const QString& name, const QVariant& value);
    void setPropertyError(const QString& name, const QString& message);
    void syncSize();
    void syncImplicit();
    void updateErrorText();
    void watchWindow(QQuickWindow* window);

    QQmlEngine* m_engine = nullptr;
    QString m_path;
    qint32 m_reload = 0;           // last QML_RELOAD counter applied (or recorded while nothing was loaded)
    QQuickItem* m_root = nullptr;   // the instance, child item, CppOwnership, deleted by this item
    QmlWatcher* m_watcher = nullptr;
    bool m_auto = true;
    bool m_rootSized = false;       // the root's width/height were written by syncSize (reset again on Auto Size)
    bool m_dropNotice = false;
    QString m_loadError;
    QMap<QString, QString> m_propErrors; // name -> message
    QString m_errorText;
    QList<QString> m_order;              // names in the order first set
    QHash<QString, QVariant> m_values;   // last value per name
    // Former instances, deleted one frame later like other items: age 0 = retired this frame.
    std::vector<std::pair<QPointer<QQuickItem>, int>> m_retired;
    QMetaObject::Connection m_frameConnection;
};

} // namespace vlqt
