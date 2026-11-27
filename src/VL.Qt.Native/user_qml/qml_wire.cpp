#include "user_qml/qml_wire.h"

#include "protocol.h"
#include "wire.h"

#include <QtCore/QDebug>
#include <QtCore/QList>
#include <QtCore/QPoint>
#include <QtCore/QPointF>
#include <QtCore/QRectF>
#include <QtCore/QSize>
#include <QtCore/QSizeF>
#include <QtCore/QUrl>
#include <QtGui/QColor>
#include <QtGui/QVector2D>
#include <QtGui/QVector3D>
#include <QtGui/QVector4D>
#include <QtQml/QJSValue>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <type_traits>

namespace vlqt {

namespace {

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

// String form of a signal argument that has no wire type.
QString stringForm(const QVariant& v)
{
    if (v.canConvert<QString>() && !v.toString().isNull())
        return v.toString();
    QString s;
    QDebug(&s).noquote().nospace() << v;
    return s;
}

QVariant unwrapJs(const QVariant& v)
{
    return v.metaType() == QMetaType::fromType<QJSValue>() ? v.value<QJSValue>().toVariant() : v;
}

// ---------------------------------------------------------------------------------------------
// Lists out of QML

// The wire type of the elements of a QList<T>, for a list without elements.
template <class T>
constexpr quint16 declaredElementType()
{
    if constexpr (std::is_same_v<T, bool>)
        return VLQT_VALUE_TYPE_BOOL;
    else if constexpr (std::is_integral_v<T>)
        return VLQT_VALUE_TYPE_I32;
    else if constexpr (std::is_floating_point_v<T>)
        return VLQT_VALUE_TYPE_F32;
    else if constexpr (std::is_same_v<T, QString> || std::is_same_v<T, QUrl>)
        return VLQT_VALUE_TYPE_STRING;
    else if constexpr (std::is_same_v<T, QColor>)
        return VLQT_VALUE_TYPE_COLOR;
    else if constexpr (std::is_same_v<T, QVector3D>)
        return VLQT_VALUE_TYPE_VEC3;
    else if constexpr (std::is_same_v<T, QVector4D>)
        return VLQT_VALUE_TYPE_VEC4;
    else if constexpr (std::is_same_v<T, QRectF> || std::is_same_v<T, QRect>)
        return VLQT_VALUE_TYPE_RECTF;
    else
        return VLQT_VALUE_TYPE_VEC2; // QVector2D, QPointF, QPoint, QSizeF, QSize
}

// A LIST value (protocol.h "Values"). The elements are encoded like single values and all have one wire type, or integers and
// reals together, which go as F32. An empty list carries `declared`, the element type of its C++ type. false = not sendable
// (an element of another type, mixed types, a nested list, or too many elements: then `error` says so).
template <class At>
bool encodeList(qsizetype count, quint16 declared, At at, QmlWireValue& out, QString* error)
{
    if (count > VLQT_LIST_VALUE_MAX_COUNT) {
        if (error)
            *error = QStringLiteral("a list of %1 elements cannot be sent to the patch (the limit is %2)")
                         .arg(count)
                         .arg(VLQT_LIST_VALUE_MAX_COUNT);
        return false;
    }
    for (const bool realOnly : {false, true}) {
        quint16 type = count ? 0 : declared;
        QByteArray body;
        bool mixed = false;
        for (qsizetype i = 0; i < count; ++i) {
            QmlWireValue w;
            if (!toWireValue(at(i), w) || w.type == VLQT_VALUE_TYPE_LIST)
                return false;
            if (realOnly && w.type == VLQT_VALUE_TYPE_I32) {
                qint32 n = 0;
                std::memcpy(&n, w.bytes.constData(), sizeof n);
                w.type = VLQT_VALUE_TYPE_F32;
                w.bytes.clear();
                append(w.bytes, float(n));
            }
            if (i == 0)
                type = w.type;
            mixed = w.type != type;
            if (mixed)
                break;
            body.append(w.bytes);
            if (type == VLQT_VALUE_TYPE_STRING)
                body.append(QByteArray(align4(int(w.bytes.size())) - int(w.bytes.size()), '\0'));
        }
        if (mixed)
            continue; // integers and reals together: once more with every integer as a real
        out.type = VLQT_VALUE_TYPE_LIST;
        out.bytes.clear();
        append(out.bytes, type);
        append(out.bytes, quint16(0));
        append(out.bytes, quint32(count));
        out.bytes.append(body);
        return true;
    }
    return false;
}

// -1 = v is not a QList<T>, else 1 (sent) or 0 (not sendable).
template <class T>
int encodeTyped(const QVariant& v, QmlWireValue& out, QString* error)
{
    if (v.metaType() != QMetaType::fromType<QList<T>>())
        return -1;
    const QList<T> list = v.value<QList<T>>();
    const auto at = [&](qsizetype i) { return QVariant::fromValue(list.at(i)); };
    return encodeList(list.size(), declaredElementType<T>(), at, out, error) ? 1 : 0;
}

bool listToWire(const QVariant& v, QmlWireValue& out, QString* error)
{
    if (v.metaType() == QMetaType::fromType<QVariantList>()) {
        const QVariantList list = v.toList();
        return encodeList(list.size(), VLQT_VALUE_TYPE_F32, [&](qsizetype i) { return list.at(i); }, out, error);
    }
    int result = -1;
    const auto tryTypes = [&](auto... t) { ((result = result >= 0 ? result : encodeTyped<decltype(t)>(v, out, error)), ...); };
    tryTypes(double{}, float{}, int{}, bool{}, QString{}, QUrl{}, QColor{}, QPointF{}, QPoint{}, QSizeF{}, QSize{}, QVector2D{},
             QVector3D{}, QVector4D{}, QRectF{}, QRect{});
    return result == 1;
}

// ---------------------------------------------------------------------------------------------
// Lists into QML

// -1 = target is not a QList<T>, else 1 (converted into out) or 0 (an element does not convert).
template <class T>
int convertTyped(const QVariantList& in, QMetaType target, QVariant& out)
{
    if (target != QMetaType::fromType<QList<T>>())
        return -1;
    const QMetaType elem = QMetaType::fromType<T>();
    QList<T> list;
    list.reserve(in.size());
    for (const QVariant& e : in) {
        QVariant c = adaptVector2D(e, elem);
        if (c.metaType() != elem && !c.convert(elem))
            return 0;
        list.push_back(c.value<T>());
    }
    out = QVariant::fromValue(std::move(list));
    return 1;
}

} // namespace

QVariant adaptVector2D(const QVariant& v, QMetaType target)
{
    if (v.metaType() != QMetaType::fromType<QVector2D>())
        return v;
    const QVector2D q = v.value<QVector2D>();
    if (target == QMetaType::fromType<QPointF>())
        return QPointF(q.x(), q.y());
    if (target == QMetaType::fromType<QPoint>())
        return QPointF(q.x(), q.y()).toPoint();
    if (target == QMetaType::fromType<QSizeF>())
        return QSizeF(q.x(), q.y());
    if (target == QMetaType::fromType<QSize>())
        return QSizeF(q.x(), q.y()).toSize();
    return v;
}

QVariant listFromWire(WireReader& r)
{
    const quint16 type = r.u16();
    r.u16(); // reserved
    const quint32 count = r.u32();
    QVariantList list;
    list.reserve(qsizetype(count));
    float f[4] = {};
    for (quint32 i = 0; i < count && r.ok(); ++i) {
        switch (type) {
        case VLQT_VALUE_TYPE_F32: list.push_back(double(r.f32())); break;
        case VLQT_VALUE_TYPE_I32: list.push_back(int(r.i32())); break;
        case VLQT_VALUE_TYPE_BOOL: list.push_back(r.u8() != 0); break;
        case VLQT_VALUE_TYPE_STRING: {
            const quint32 n = r.u32();
            list.push_back(QString::fromUtf8(reinterpret_cast<const char*>(r.bytes(n, true)), qsizetype(n)));
            break;
        }
        case VLQT_VALUE_TYPE_COLOR:
            r.copy(f, 16);
            list.push_back(QColor::fromRgbF(f[0], f[1], f[2], f[3]));
            break;
        case VLQT_VALUE_TYPE_RECTF:
            r.copy(f, 16);
            list.push_back(QRectF(f[0], f[1], f[2], f[3]));
            break;
        case VLQT_VALUE_TYPE_VEC2:
            r.copy(f, 8);
            list.push_back(QVector2D(f[0], f[1]));
            break;
        case VLQT_VALUE_TYPE_VEC3:
            r.copy(f, 12);
            list.push_back(QVector3D(f[0], f[1], f[2]));
            break;
        case VLQT_VALUE_TYPE_VEC4:
            r.copy(f, 16);
            list.push_back(QVector4D(f[0], f[1], f[2], f[3]));
            break;
        default: return QVariantList(); // the decoder has checked the element type
        }
    }
    return list;
}

bool convertListToType(const QVariantList& list, QMetaType target, QVariant& out)
{
    if (target.id() == QMetaType::QVariant || target == QMetaType::fromType<QVariantList>()) {
        out = list;
        return true;
    }
    int result = -1;
    const auto tryTypes = [&](auto... t) { ((result = result >= 0 ? result : convertTyped<decltype(t)>(list, target, out)), ...); };
    tryTypes(double{}, int{}, bool{}, QString{}, QColor{}, QPointF{}, QSizeF{}, QVector2D{}, QVector3D{}, QVector4D{}, QRectF{});
    return result == 1;
}

bool toWireValue(const QVariant& in, QmlWireValue& out, QString* error)
{
    const QVariant v = unwrapJs(in);
    out.bytes.clear();
    switch (v.typeId()) {
    case QMetaType::Bool:
        out.type = VLQT_VALUE_TYPE_BOOL;
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
        out.type = VLQT_VALUE_TYPE_I32;
        const qint32 i = qint32(std::clamp<qint64>(v.toLongLong(), INT32_MIN, INT32_MAX));
        append(out.bytes, i);
        return true;
    }
    case QMetaType::Double:
    case QMetaType::Float:
        out.type = VLQT_VALUE_TYPE_F32;
        append(out.bytes, float(v.toDouble()));
        return true;
    case QMetaType::QString:
    case QMetaType::QUrl: {
        out.type = VLQT_VALUE_TYPE_STRING;
        const QByteArray utf8 = v.toString().toUtf8();
        append(out.bytes, quint32(utf8.size()));
        out.bytes.append(utf8);
        return true;
    }
    case QMetaType::QColor: {
        const QColor c = v.value<QColor>();
        out.type = VLQT_VALUE_TYPE_COLOR;
        appendFloats(out.bytes, {float(c.redF()), float(c.greenF()), float(c.blueF()), float(c.alphaF())});
        return true;
    }
    case QMetaType::QVector2D: {
        const QVector2D q = v.value<QVector2D>();
        out.type = VLQT_VALUE_TYPE_VEC2;
        appendFloats(out.bytes, {q.x(), q.y()});
        return true;
    }
    case QMetaType::QPointF:
    case QMetaType::QPoint: {
        const QPointF p = v.toPointF();
        out.type = VLQT_VALUE_TYPE_VEC2;
        appendFloats(out.bytes, {float(p.x()), float(p.y())});
        return true;
    }
    case QMetaType::QSizeF:
    case QMetaType::QSize: {
        const QSizeF s = v.toSizeF();
        out.type = VLQT_VALUE_TYPE_VEC2;
        appendFloats(out.bytes, {float(s.width()), float(s.height())});
        return true;
    }
    case QMetaType::QVector3D: {
        const QVector3D q = v.value<QVector3D>();
        out.type = VLQT_VALUE_TYPE_VEC3;
        appendFloats(out.bytes, {q.x(), q.y(), q.z()});
        return true;
    }
    case QMetaType::QVector4D: {
        const QVector4D q = v.value<QVector4D>();
        out.type = VLQT_VALUE_TYPE_VEC4;
        appendFloats(out.bytes, {q.x(), q.y(), q.z(), q.w()});
        return true;
    }
    case QMetaType::QRectF:
    case QMetaType::QRect: {
        const QRectF r = v.toRectF();
        out.type = VLQT_VALUE_TYPE_RECTF;
        appendFloats(out.bytes, {float(r.x()), float(r.y()), float(r.width()), float(r.height())});
        return true;
    }
    default:
        return listToWire(v, out, error);
    }
}

bool toWireValueOrString(const QVariant& in, QmlWireValue& out, QString* error)
{
    QString why;
    if (toWireValue(in, out, &why))
        return true;
    if (!why.isEmpty()) { // a list over the limit has no string form either
        if (error)
            *error = why;
        return false;
    }
    toWireValue(stringForm(unwrapJs(in)), out);
    return true;
}

} // namespace vlqt
