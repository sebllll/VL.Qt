#include "elements/elements.h"
#include "effects/effect_chain.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QUrl>
#include <QtQml/QQmlComponent>
#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickItem>

#include <cmath>
#include <cstring>

Q_LOGGING_CATEGORY(lcElements, "vlqt.elements")

namespace vlqt {

namespace {

// EFFECT_* (block 832): keys of SET_EFFECT_CHAIN MULTI entries only (effects/effect_chain.cpp); SET with one is -6.
bool isEffectProp(quint16 prop) { return prop >= 832 && prop <= VLQT_MULTI_EFFECT_KEY_MASK_INVERTED; }

const PropDef* findProp(const TypeDef& type, quint16 prop)
{
    if (isEffectProp(prop))
        return nullptr; // only via SET_EFFECT_CHAIN
    if (prop == VLQT_PROPERTY_AUTO_SIZE)
        return type.autoSize != AutoSize::None ? &autoSizeProp() : nullptr;
    const auto& list = prop >= 256 ? type.props : commonProps();
    for (const PropDef& p : list)
        if (p.id == prop)
            return &p;
    return nullptr;
}

// Number of float components of a float value type that Qt would take unchecked (0 = no float value).
int floatComponents(quint16 valueType)
{
    switch (valueType) {
    case VLQT_VALUE_TYPE_F32: return 1;
    case VLQT_VALUE_TYPE_VEC2: return 2;
    case VLQT_VALUE_TYPE_COLOR:
    case VLQT_VALUE_TYPE_RECTF: return 4;
    case VLQT_VALUE_TYPE_MAT4: return 16;
    default: return 0;
    }
}

} // namespace

Elements::Elements(QQmlEngine* e) : engine(e) {}

Elements::~Elements()
{
    // Items are deleted by the Scene before; components only hold compiled QML.
    for (auto& kv : types)
        delete kv.second.component;
}

QQuickItem* Elements::create(const TypeDef& type, std::string& err)
{
    TypeCache& cache = types[type.id];
    QQuickItem* item = nullptr;
    if (type.make) {
        item = type.make();
    } else if (!type.qmlUrl) {
        item = new QQuickItem();
    } else {
        if (!cache.component) {
            cache.component = new QQmlComponent(engine, QUrl(QString::fromLatin1(type.qmlUrl)));
        }
        QQmlComponent* comp = cache.component;
        QObject* obj = comp->isError() ? nullptr : comp->create();
        item = qobject_cast<QQuickItem*>(obj);
        if (!item) {
            delete obj;
            err = std::string("type ") + type.name + " could not be created: " + comp->errorString().toStdString();
            return nullptr;
        }
        QQmlEngine::setObjectOwnership(item, QQmlEngine::CppOwnership);
    }
    if (!cache.resolved)
        resolve(type, cache, item);
    return item;
}

bool Elements::readMedia(const ElementEntry& node, float out[2]) const
{
    const auto it = types.find(node.type);
    if (it == types.end() || !it->second.position.isValid() || !it->second.duration.isValid())
        return false;
    const TypeCache& c = it->second;
    // MediaPlayer reports milliseconds (qint64); 0 until the media is loaded.
    out[0] = float(c.position.read(node.item).toLongLong() / 1000.0);
    out[1] = float(c.duration.read(node.item).toLongLong() / 1000.0);
    return true;
}

QString Elements::readError(const ElementEntry& node) const
{
    const auto it = types.find(node.type);
    QString own;
    if (it != types.end() && it->second.error.isValid())
        own = it->second.error.read(node.item).toString(); // implicitly shared, no copy of the text
    // Content problems of the effect chain ("link N: ..."); joined only while both have text.
    if (!node.effectChain || node.effectChain->error().isEmpty())
        return own;
    return own.isEmpty() ? node.effectChain->error() : own + u'\n' + node.effectChain->error();
}

quint32 Elements::takeClicks(const ElementEntry& node)
{
    const auto it = types.find(node.type);
    if (it == types.end() || !it->second.clicks.isValid())
        return 0;
    const int c = it->second.clicks.read(node.item).toInt();
    if (c > 0)
        it->second.clicks.write(node.item, 0);
    return c > 0 ? quint32(c) : 0;
}

bool Elements::pollsState(quint16 type) const
{
    const auto it = types.find(type);
    if (it == types.end())
        return false;
    const TypeCache& c = it->second;
    return (c.position.isValid() && c.duration.isValid()) || c.error.isValid() || c.clicks.isValid() || (c.userCount.isValid() && c.userValue.isValid());
}

bool Elements::takeUserValue(const ElementEntry& node, QVariant& out)
{
    const auto it = types.find(node.type);
    if (it == types.end() || !it->second.userCount.isValid() || !it->second.userValue.isValid())
        return false;
    const TypeCache& c = it->second;
    if (c.userCount.read(node.item).toInt() <= 0)
        return false;
    c.userCount.write(node.item, 0);
    out = c.userValue.read(node.item);
    return true;
}

void Elements::resolve(const TypeDef& type, TypeCache& cache, QQuickItem* first)
{
    // All instances of a QML component share the property layout (not necessarily one meta object): resolve each table
    // property once, on the first item.
    cache.resolved = true;
    const QMetaObject* mo = first->metaObject();
    if (const int ip = mo->indexOfProperty("vlqtPosition"); ip >= 0)
        cache.position = mo->property(ip);
    if (const int idur = mo->indexOfProperty("vlqtDuration"); idur >= 0)
        cache.duration = mo->property(idur);
    if (const int ie = mo->indexOfProperty("vlqtError"); ie >= 0)
        cache.error = mo->property(ie);
    if (const int ic = mo->indexOfProperty("vlqtClicks"); ic >= 0)
        cache.clicks = mo->property(ic);
    if (const int iu = mo->indexOfProperty("vlqtUserCount"); iu >= 0)
        cache.userCount = mo->property(iu);
    if (const int iv = mo->indexOfProperty("vlqtUserValue"); iv >= 0)
        cache.userValue = mo->property(iv);
    for (const PropDef& p : type.props) {
        if (!p.qmlName)
            continue;
        ResolvedProp rp;
        rp.def = &p;
        const char* dot = std::strchr(p.qmlName, '.');
        const QByteArray head = dot ? QByteArray(p.qmlName, int(dot - p.qmlName)) : QByteArray(p.qmlName);
        const int idx = mo->indexOfProperty(head.constData());
        if (idx >= 0)
            rp.prop = mo->property(idx);
        if (rp.prop.isValid() && dot) {
            QObject* group = rp.prop.read(first).value<QObject*>();
            const int sidx = group ? group->metaObject()->indexOfProperty(dot + 1) : -1;
            if (sidx >= 0)
                rp.sub = group->metaObject()->property(sidx);
            else
                rp.prop = QMetaProperty();
        }
        if (rp.prop.isValid())
            cache.props[p.id] = rp;
        else
            qCWarning(lcElements, "type %s: QML property '%s' (id %d) not found, SET will fail", type.name, p.qmlName, p.id);
    }
}

int Elements::set(ElementEntry& node, quint16 prop, const Value& v, std::string& err)
{
    const TypeDef* type = node.def;
    const PropDef* def = findProp(*type, prop);
    TypeCache& cache = types[node.type];
    const ResolvedProp* rp = nullptr;
    ResolvedProp api; // properties without a QML name (C++ API): only def is set
    if (def && def->qmlName) {
        auto it = cache.props.find(prop);
        if (it != cache.props.end())
            rp = &it->second;
    } else if (def) {
        api.def = def;
        rp = &api;
    }
    if (!rp) {
        err = "property " + std::to_string(prop) + " not supported by type " + type->name
            + (isEffectProp(prop) ? " (EFFECT_* only via SET_EFFECT_CHAIN)" : "");
        if (cache.warned.insert(prop).second)
            qCWarning(lcElements, "%s", err.c_str());
        return ApplyUnknownProperty;
    }
    if (def->valueType != v.type) {
        err = "property " + std::to_string(prop) + " expects value type " + std::to_string(def->valueType)
            + ", got " + std::to_string(v.type);
        return ApplyBadValue;
    }
    // One rule for every float value: NaN/inf never reach a setter (Qt takes them unchecked, qRound is undefined for them).
    for (int i = 0, n = floatComponents(v.type); i < n; ++i)
        if (!std::isfinite(v.f[i])) {
            err = "property " + std::to_string(prop) + ": value must be finite";
            return ApplyBadValue;
        }
    std::string reason;
    if (!def->set(*this, node, *rp, v, reason)) {
        err = "property " + std::to_string(prop) + ": " + reason;
        return ApplyBadValue;
    }
    return ApplyOk;
}

} // namespace vlqt
