#include "qmlitem.h"

#include "protocol.h"

#include <QtCore/QDebug>
#include <QtCore/QDir>
#include <QtCore/QFileInfo>
#include <QtCore/QMetaMethod>
#include <QtCore/QMetaObject>
#include <QtCore/QMetaProperty>
#include <QtCore/QRectF>
#include <QtCore/QUrl>
#include <QtGui/QColor>
#include <QtGui/QVector2D>
#include <QtGui/QVector3D>
#include <QtGui/QVector4D>
#include <QtQml/QJSValue>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQml/QQmlError>
#include <QtQuick/QQuickWindow>

#include <algorithm>
#include <memory>
#include <cstring>

namespace vlqt {

namespace {

constexpr qsizetype kMaxWireString = 60000;

template <class T>
void append(QByteArray& out, const T& v)
{
    out.append(reinterpret_cast<const char*>(&v), qsizetype(sizeof v));
}

void appendFloats(QByteArray& out, std::initializer_list<float> values)
{
    for (float f : values)
        append(out, f);
}

// Native class behind a QML type: QML-generated meta objects are named "<Class>_QML_<n>" / "<Name>_QMLTYPE_<n>".
bool isQmlGenerated(const QMetaObject* mo) { return mo && QByteArray(mo->className()).contains("_QML"); }

// "path:line:col: description" per error, local files as plain native paths (QQmlComponent::errorString gives file: URLs).
QString errorLines(const QQmlComponent& component)
{
    QStringList lines;
    for (const QQmlError& e : component.errors()) {
        const QUrl url = e.url();
        const QString where = url.isLocalFile() ? QDir::toNativeSeparators(url.toLocalFile()) : url.toString();
        lines << QStringLiteral("%1:%2:%3: %4").arg(where).arg(e.line()).arg(e.column()).arg(e.description());
    }
    return lines.join(u'\n');
}

QString nativeClassName(const QObject* obj)
{
    const QMetaObject* mo = obj->metaObject();
    while (mo && isQmlGenerated(mo))
        mo = mo->superClass();
    return QString::fromLatin1(mo ? mo->className() : obj->metaObject()->className());
}

} // namespace

bool toWireValue(const QVariant& in, QmlWireValue& out)
{
    QVariant v = in;
    if (v.metaType() == QMetaType::fromType<QJSValue>())
        v = v.value<QJSValue>().toVariant();
    out.bytes.clear();
    switch (v.typeId()) {
    case QMetaType::Bool:
        out.type = VLQT_VT_BOOL;
        out.bytes.append(char(v.toBool() ? 1 : 0));
        return true;
    case QMetaType::Int:
    case QMetaType::UInt:
    case QMetaType::Short:
    case QMetaType::UShort:
    case QMetaType::Long:
    case QMetaType::ULong:
    case QMetaType::LongLong:
    case QMetaType::ULongLong:
    case QMetaType::Char:
    case QMetaType::SChar:
    case QMetaType::UChar: {
        out.type = VLQT_VT_I32;
        const qint32 i = qint32(std::clamp<qint64>(v.toLongLong(), INT32_MIN, INT32_MAX));
        append(out.bytes, i);
        return true;
    }
    case QMetaType::Double:
    case QMetaType::Float:
        out.type = VLQT_VT_F32;
        append(out.bytes, float(v.toDouble()));
        return true;
    case QMetaType::QString:
    case QMetaType::QUrl: {
        out.type = VLQT_VT_STRING;
        QByteArray utf8 = v.toString().toUtf8();
        qsizetype n = utf8.size();
        if (n > kMaxWireString) {
            n = kMaxWireString;
            while (n > 0 && (quint8(utf8[n]) & 0xC0) == 0x80) // utf8[n] is the first byte cut off
                --n;
        }
        append(out.bytes, quint32(n));
        out.bytes.append(utf8.constData(), n);
        return true;
    }
    case QMetaType::QColor: {
        const QColor c = v.value<QColor>();
        out.type = VLQT_VT_COLOR;
        appendFloats(out.bytes, {float(c.redF()), float(c.greenF()), float(c.blueF()), float(c.alphaF())});
        return true;
    }
    case QMetaType::QVector2D: {
        const QVector2D q = v.value<QVector2D>();
        out.type = VLQT_VT_VEC2;
        appendFloats(out.bytes, {q.x(), q.y()});
        return true;
    }
    case QMetaType::QVector3D: {
        const QVector3D q = v.value<QVector3D>();
        out.type = VLQT_VT_VEC3;
        appendFloats(out.bytes, {q.x(), q.y(), q.z()});
        return true;
    }
    case QMetaType::QVector4D: {
        const QVector4D q = v.value<QVector4D>();
        out.type = VLQT_VT_VEC4;
        appendFloats(out.bytes, {q.x(), q.y(), q.z(), q.w()});
        return true;
    }
    case QMetaType::QRectF:
    case QMetaType::QRect: {
        const QRectF r = v.toRectF();
        out.type = VLQT_VT_RECTF;
        appendFloats(out.bytes, {float(r.x()), float(r.y()), float(r.width()), float(r.height())});
        return true;
    }
    default:
        return false;
    }
}

namespace {

// String form of a signal argument that has no wire type (protocol.h: "sent as their string form").
QString stringForm(const QVariant& v)
{
    if (v.canConvert<QString>() && !v.toString().isNull())
        return v.toString();
    QString s;
    QDebug(&s).noquote().nospace() << v;
    return s;
}

QmlWireValue wireOrString(const QVariant& in)
{
    QmlWireValue w;
    if (toWireValue(in, w))
        return w;
    toWireValue(stringForm(in.metaType() == QMetaType::fromType<QJSValue>() ? in.value<QJSValue>().toVariant() : in), w);
    return w;
}

} // namespace

// ---------------------------------------------------------------------------------------------
// QmlWatcher: connects the notify signals of the declared properties and the declared signals of the root to itself
// without a slot per signal: the connections carry raw ids (QMetaObject::connect with an index, no receiver meta
// method), the dispatch is the override of qt_metacall. Deliberately no Q_OBJECT (moc would generate its own
// qt_metacall). Property k = id kPropBase + k, signal j = id kSignalBase + j.

class QmlWatcher final : public QObject
{
public:
    static constexpr int kPropBase = 1000;
    static constexpr int kSignalBase = 30000; // ids are stored as ushort by Qt's connections: all below 65536

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

    QmlWatcher(QmlItem* owner, QQuickItem* root) : m_owner(owner), m_root(root)
    {
        const QMetaObject* mo = root->metaObject();
        // Declared in the user file: the QML-generated classes of the chain (a QML type derived from another QML type
        // counts with all its QML ancestors), not those of the native base (Item, Rectangle, ...).
        QList<const QMetaObject*> chain;
        for (const QMetaObject* m = mo; m && isQmlGenerated(m); m = m->superClass())
            chain.push_back(m);
        for (const QMetaObject* m : chain) {
            for (int i = m->propertyOffset(); i < m->propertyCount(); ++i) {
                const QMetaProperty p = m->property(i);
                if (!p.isValid() || !p.hasNotifySignal())
                    continue;
                const int k = int(m_props.size());
                Prop e;
                e.prop = p;
                e.name = p.name();
                m_props.push_back(e);
                m_propOfIndex.insert(i, k);
                QMetaObject::connect(root, p.notifySignalIndex(), this, kPropBase + k, Qt::DirectConnection);
            }
        }
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
                m_sigs.push_back(Sig{mm, mm.name()});
                QMetaObject::connect(root, mm.methodIndex(), this, kSignalBase + j, Qt::DirectConnection);
            }
        }
    }

    // Index into the table of the declared property with this index on the root's meta object, -1 if not watched.
    int watchedIndex(int propertyIndex) const { return m_propOfIndex.value(propertyIndex, -1); }
    void suppress(int k) { m_suppress = k; }

    bool hasEvents() const { return !m_dirty.empty() || !m_signals.empty() || m_dropped; }

    void take(std::vector<QmlPropertyEvent>& properties, std::vector<QmlSignalEvent>& signalEvents, bool& dropped)
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

    int qt_metacall(QMetaObject::Call call, int id, void** argv) override
    {
        if (call != QMetaObject::InvokeMetaMethod || id < kPropBase)
            return QObject::qt_metacall(call, id, argv);
        if (id >= kSignalBase)
            onSignal(id - kSignalBase, argv);
        else
            onProperty(id - kPropBase);
        return -1;
    }

private:
    void onProperty(int k)
    {
        if (k < 0 || size_t(k) >= m_props.size() || k == m_suppress) // no echo of the patch's own set
            return;
        Prop& p = m_props[size_t(k)];
        if (!p.dirty) {
            p.dirty = true;
            m_dirty.push_back(k);
        }
    }

    void onSignal(int j, void** argv)
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
            e.args.push_back(wireOrString(v));
        }
        m_signals.push_back(std::move(e));
    }

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

// ---------------------------------------------------------------------------------------------

QmlItem::QmlItem(QQuickItem* parent) : QQuickItem(parent)
{
    connect(this, &QQuickItem::windowChanged, this, &QmlItem::watchWindow);
}

QmlItem::~QmlItem()
{
    teardown();
    for (auto& r : m_retired)
        delete r.first.data();
}

void QmlItem::watchWindow(QQuickWindow* window)
{
    disconnect(m_frameConnection);
    if (window && !m_retired.empty())
        m_frameConnection = connect(window, &QQuickWindow::afterAnimating, this, &QmlItem::flushRetired);
}

void QmlItem::setSource(QQmlEngine* engine, const QString& path)
{
    if (engine == m_engine && path == m_path)
        return;
    m_engine = engine;
    m_path = path;
    rebuild(false);
}

void QmlItem::setReload(qint32 counter)
{
    // The initial 0 counts as applied, so the first bump (0 -> 1) already reloads.
    const bool changed = m_reload != counter;
    m_reload = counter;
    if (changed && m_engine && !m_path.isEmpty())
        rebuild(true);
}

void QmlItem::setAutoSize(bool on)
{
    m_auto = on;
    syncSize();
}

void QmlItem::geometryChange(const QRectF& newGeometry, const QRectF& oldGeometry)
{
    QQuickItem::geometryChange(newGeometry, oldGeometry);
    if (newGeometry.size() != oldGeometry.size())
        syncSize();
}

void QmlItem::syncSize()
{
    if (!m_root)
        return;
    if (m_auto) {
        if (m_rootSized) {
            m_root->resetWidth();
            m_root->resetHeight();
            m_rootSized = false;
        }
    } else {
        m_root->setWidth(width());
        m_root->setHeight(height());
        m_rootSized = true;
    }
}

void QmlItem::syncImplicit()
{
    // The root's width/height: its implicit size, or the size the QML declares for it (it is the implicit size of this
    // item, which Auto Size takes). Not while Auto Size is off: the root then follows this item's size.
    setImplicitSize(m_root ? m_root->width() : 0, m_root ? m_root->height() : 0);
}

void QmlItem::teardown()
{
    delete m_watcher;
    m_watcher = nullptr;
    if (m_root) {
        QQuickItem* old = m_root;
        m_root = nullptr;
        retire(old);
    }
}

void QmlItem::retire(QQuickItem* old)
{
    old->disconnect(this);
    old->setVisible(false);
    old->setParentItem(nullptr);
    if (!window()) { // never rendered in a window: nothing can refer to it
        delete old;
        return;
    }
    m_retired.emplace_back(old, 0);
    watchWindow(window());
}

void QmlItem::flushRetired()
{
    for (size_t i = 0; i < m_retired.size();) {
        if (m_retired[i].second >= 1) {
            delete m_retired[i].first.data();
            m_retired.erase(m_retired.begin() + qsizetype(i));
        } else {
            ++m_retired[i].second;
            ++i;
        }
    }
    if (m_retired.empty())
        disconnect(m_frameConnection);
}

void QmlItem::rebuild(bool clearCache)
{
    teardown();
    m_loadError.clear();
    m_propErrors.clear(); // the properties below are applied again and report again
    m_dropNotice = false;
    m_rootSized = false;
    auto finish = [this] {
        syncImplicit();
        updateErrorText();
    };
    if (!m_engine || m_path.isEmpty())
        return finish();
    if (!QFileInfo::exists(m_path)) {
        m_loadError = QStringLiteral("%1: file not found").arg(m_path);
        return finish();
    }
    if (clearCache)
        m_engine->clearComponentCache(); // components the file imports by relative path are read again as well
    // Windows/macOS: Qt compares the case of the file name with the one on disk; the canonical path has it right.
    const QString canonical = QFileInfo(m_path).canonicalFilePath();
    const QUrl url = QUrl::fromLocalFile(canonical.isEmpty() ? m_path : canonical);
    auto load = [&] {
        auto c = std::make_unique<QQmlComponent>(m_engine);
        c->loadUrl(url, QQmlComponent::PreferSynchronous);
        return c;
    };
    std::unique_ptr<QQmlComponent> loaded = load();
    // The engine caches the directory listing: a file that is new in a directory it already read (another File in the same
    // folder) is "case mismatch" until the cache is cleared. Once, and only then, so that creating QML elements does not
    // clear the cache of the other items.
    if (!clearCache && loaded->isError() && !loaded->errors().isEmpty()
        && loaded->errors().constFirst().description().contains(QLatin1String("case mismatch"))) {
        m_engine->clearComponentCache();
        loaded = load();
    }
    QQmlComponent& component = *loaded;
    if (component.isLoading()) {
        m_loadError = QStringLiteral("%1: the file did not load synchronously").arg(m_path);
        return finish();
    }
    QObject* obj = component.isError() ? nullptr : component.create();
    if (!obj) {
        m_loadError = errorLines(component);
        if (m_loadError.isEmpty())
            m_loadError = QStringLiteral("%1: the file could not be created").arg(m_path);
        return finish();
    }
    auto* item = qobject_cast<QQuickItem*>(obj);
    if (!item) {
        const QString type = nativeClassName(obj);
        delete obj;
        m_loadError = QStringLiteral("%1: the root must be an Item (QQuickItem), it is a %2").arg(m_path, type);
        return finish();
    }
    // Runtime errors of the creation (e.g. a failing binding) stay in the component's error list.
    if (component.isError())
        m_loadError = errorLines(component);
    QQmlEngine::setObjectOwnership(item, QQmlEngine::CppOwnership);
    item->setParentItem(this);
    m_root = item;
    connect(item, &QQuickItem::widthChanged, this, &QmlItem::syncImplicit);
    connect(item, &QQuickItem::heightChanged, this, &QmlItem::syncImplicit);
    m_watcher = new QmlWatcher(this, item);
    syncSize();
    syncImplicit();
    for (const QString& name : std::as_const(m_order))
        applyProperty(name, m_values.value(name));
    finish();
}

void QmlItem::setRootProperty(const QString& name, const QVariant& value)
{
    if (!m_values.contains(name))
        m_order.push_back(name);
    m_values.insert(name, value);
    m_propErrors.remove(name);
    if (m_root)
        applyProperty(name, value);
    updateErrorText();
}

void QmlItem::rejectRootProperty(const QString& name, const QString& message)
{
    setPropertyError(name, message);
    updateErrorText();
}

void QmlItem::setPropertyError(const QString& name, const QString& message)
{
    m_propErrors.insert(name, message);
}

void QmlItem::applyProperty(const QString& name, const QVariant& value)
{
    const QMetaObject* mo = m_root->metaObject();
    const int index = mo->indexOfProperty(name.toUtf8().constData());
    if (index < 0) {
        setPropertyError(name, QStringLiteral("the root has no such property"));
        return;
    }
    const QMetaProperty p = mo->property(index);
    if (!p.isWritable()) {
        setPropertyError(name, QStringLiteral("property '%1' is read-only").arg(name));
        return;
    }
    QVariant v = value;
    const QMetaType target = p.metaType();
    // var properties and enums take the value as it is, everything else goes through QVariant's conversion.
    if (!p.isEnumType() && target.id() != QMetaType::QVariant && v.metaType() != target && !v.convert(target)) {
        setPropertyError(name, QStringLiteral("a %1 value cannot be converted to the type '%2' of property '%3'")
                                   .arg(QString::fromLatin1(value.typeName()), QString::fromLatin1(p.typeName()), name));
        return;
    }
    // The patch's own set is not reported back: the notify signal of this property fires synchronously in write().
    const int watched = m_watcher ? m_watcher->watchedIndex(index) : -1;
    if (m_watcher)
        m_watcher->suppress(watched);
    const bool ok = p.write(m_root, v);
    if (m_watcher)
        m_watcher->suppress(-1);
    if (!ok)
        setPropertyError(name, QStringLiteral("writing property '%1' failed").arg(name));
}

void QmlItem::updateErrorText()
{
    QStringList lines;
    if (!m_loadError.isEmpty())
        lines << m_loadError;
    for (auto it = m_propErrors.cbegin(); it != m_propErrors.cend(); ++it)
        lines << QStringLiteral("%1: %2").arg(it.key(), it.value());
    if (m_dropNotice)
        lines << QStringLiteral("signals dropped (more than %1 in one poll)").arg(kQmlMaxSignalsPerPoll);
    m_errorText = lines.join(u'\n');
}

bool QmlItem::hasEvents() const { return m_watcher && m_watcher->hasEvents(); }

void QmlItem::takeEvents(std::vector<QmlPropertyEvent>& properties, std::vector<QmlSignalEvent>& signalEvents, bool& dropped)
{
    dropped = false;
    if (!m_watcher)
        return;
    m_watcher->take(properties, signalEvents, dropped);
    updateErrorText(); // an unsupported property type may have been added
}

void QmlItem::noteDropped()
{
    m_dropNotice = true;
    updateErrorText();
}

void QmlItem::clearDropNotice()
{
    if (!m_dropNotice)
        return;
    m_dropNotice = false;
    updateErrorText();
}

} // namespace vlqt
