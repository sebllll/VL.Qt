#include "user_qml/qml_wire.h"

#include "protocol.h"

#include <QtCore/QDebug>
#include <QtCore/QPoint>
#include <QtCore/QPointF>
#include <QtCore/QRectF>
#include <QtCore/QSize>
#include <QtCore/QSizeF>
#include <QtGui/QColor>
#include <QtGui/QVector2D>
#include <QtGui/QVector3D>
#include <QtGui/QVector4D>
#include <QtQml/QJSValue>

#include <algorithm>
#include <cstdint>

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

} // namespace

bool toWireValue(const QVariant& in, QmlWireValue& out)
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
        return false;
    }
}

QmlWireValue toWireValueOrString(const QVariant& in)
{
    QmlWireValue w;
    if (toWireValue(in, w))
        return w;
    toWireValue(stringForm(unwrapJs(in)), w);
    return w;
}

} // namespace vlqt
