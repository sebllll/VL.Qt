#include "user_qml/qml_item.h"

#include "user_qml/qml_watcher.h"

#include <QtCore/QDateTime>
#include <QtCore/QDir>
#include <QtCore/QFileInfo>
#include <QtCore/QMetaObject>
#include <QtCore/QMetaProperty>
#include <QtCore/QPoint>
#include <QtCore/QPointF>
#include <QtCore/QSize>
#include <QtCore/QSizeF>
#include <QtCore/QUrl>
#include <QtGui/QVector2D>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQml/QQmlError>
#include <QtQuick/QQuickWindow>

#include <memory>

namespace vlqt {

namespace {

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

// Per engine: size and modification time of each file at its last compile. The engine keeps compiled components by URL
// and does not look at the file again before clearComponentCache(), so a new item of a file that changed on disk would
// get the old component (QML-06). Owned by the engine (child object), so it goes with it.
class FileStamps : public QObject
{
public:
    static FileStamps* of(QQmlEngine* engine)
    {
        // No Q_OBJECT (this class is not in a header for moc), so the child is found by its name.
        auto* s = engine->findChild<QObject*>(QLatin1String("vlqtQmlFileStamps"), Qt::FindDirectChildrenOnly);
        return s ? static_cast<FileStamps*>(s) : new FileStamps(engine);
    }

    /* Records the file as compiled now. true = it differs from the last record (a record exists). */
    bool changedSince(const QFileInfo& file)
    {
        const Stamp now{file.lastModified(), file.size()};
        const auto it = m_stamps.find(file.canonicalFilePath());
        const bool changed = it != m_stamps.end() && !(it.value() == now);
        m_stamps.insert(file.canonicalFilePath(), now);
        return changed;
    }

private:
    explicit FileStamps(QObject* parent) : QObject(parent) { setObjectName(QLatin1String("vlqtQmlFileStamps")); }

    struct Stamp {
        QDateTime modified;
        qint64 size = 0;
        bool operator==(const Stamp& o) const { return modified == o.modified && size == o.size; }
    };
    QHash<QString, Stamp> m_stamps;
};

QString nativeClassName(const QObject* obj)
{
    const QMetaObject* mo = obj->metaObject();
    while (mo && isQmlGenerated(mo))
        mo = mo->superClass();
    return QString::fromLatin1(mo ? mo->className() : obj->metaObject()->className());
}

} // namespace

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
    m_buildPending = true;
}

void QmlItem::setReload(qint32 counter)
{
    // The initial 0 counts as applied, so the first bump (0 -> 1) already reloads.
    const bool changed = m_reload != counter;
    m_reload = counter;
    if (changed && m_engine && !m_path.isEmpty()) {
        m_buildPending = true;
        m_clearCache = true;
    }
}

void QmlItem::build()
{
    if (!m_buildPending)
        return;
    const bool clearCache = m_clearCache;
    m_buildPending = false;
    m_clearCache = false;
    rebuild(clearCache);
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
    // A file that changed on disk is read again even when no reload came (see FileStamps); one that did not change keeps
    // its compiled component, so that creating QML elements does not clear the cache of the other items.
    const bool changedOnDisk = FileStamps::of(m_engine)->changedSince(QFileInfo(m_path));
    if (clearCache || changedOnDisk)
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
    // Creation in three steps instead of component.create(): the root exists, the watcher is connected and the values set so
    // far go in as initial properties (Qt refuses a root with a `required property` that has no value at completeCreate),
    // and only then the bindings and handlers of the file run, so that what QML derives from the values is reported.
    QObject* obj = component.isError() ? nullptr : component.beginCreate(m_engine->rootContext());
    auto* item = qobject_cast<QQuickItem*>(obj);
    if (item) {
        QQmlEngine::setObjectOwnership(item, QQmlEngine::CppOwnership);
        m_root = item;
        m_watcher = new QmlWatcher(this, item);
        for (const QString& name : std::as_const(m_order))
            applyProperty(name, m_values.value(name), &component);
    }
    if (obj)
        component.completeCreate();
    if (!obj) {
        m_loadError = errorLines(component);
        if (m_loadError.isEmpty())
            m_loadError = QStringLiteral("%1: the file could not be created").arg(m_path);
        return finish();
    }
    if (!item) {
        const QString type = nativeClassName(obj);
        delete obj;
        m_loadError = QStringLiteral("%1: the root must be an Item (QQuickItem), it is a %2").arg(m_path, type);
        return finish();
    }
    // Runtime errors of the creation (e.g. a failing binding) stay in the component's error list.
    if (component.isError())
        m_loadError = errorLines(component);
    item->setParentItem(this);
    connect(item, &QQuickItem::widthChanged, this, &QmlItem::syncImplicit);
    connect(item, &QQuickItem::heightChanged, this, &QmlItem::syncImplicit);
    syncSize();
    syncImplicit();
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

void QmlItem::applyProperty(const QString& name, const QVariant& value, QQmlComponent* creating)
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
    // The patch sends point and size as a Vector2 (VEC2); QVariant has no conversion from it.
    if (v.metaType() == QMetaType::fromType<QVector2D>()) {
        const QVector2D q = v.value<QVector2D>();
        if (target == QMetaType::fromType<QPointF>())
            v = QPointF(q.x(), q.y());
        else if (target == QMetaType::fromType<QPoint>())
            v = QPointF(q.x(), q.y()).toPoint();
        else if (target == QMetaType::fromType<QSizeF>())
            v = QSizeF(q.x(), q.y());
        else if (target == QMetaType::fromType<QSize>())
            v = QSizeF(q.x(), q.y()).toSize();
    }
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
    // While the root is created Qt wants the values through the component (this is what satisfies a required property).
    bool ok = true;
    if (creating)
        creating->setInitialProperties(m_root, QVariantMap{{name, v}});
    else
        ok = p.write(m_root, v);
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
