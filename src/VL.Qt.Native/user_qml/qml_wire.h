/* user_qml/qml_wire.h - values of the back channel of the QmlElement (protocol.h "QmlElement") in wire form.
 *
 * toWireValue turns a QVariant (a root property or a signal argument) into one of the value types of the protocol:
 * bool, the integer types (clamped to i32), real (f32), string/url, color, vector2d/3d/4d, rect, date (DATE_TIME), font (FONT),
 * and the QML types `point` and `size`, which both arrive as VEC2 (x, y) and (width, height), and lists of these (but fonts) as
 * LIST (protocol.h "Values").
 * The other direction of a list: listFromWire and convertListToType; a url property takes the text the patch sends through
 * urlFromPatch.
 */
#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QDateTime>
#include <QtCore/QUrl>
#include <QtCore/QVariant>
#include <QtGui/QFont>

#include <vector>

namespace vlqt {

class WireReader;

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

/* QVariant (property value or signal argument) to wire form. false = type not supported. A list goes as LIST when all its
 * elements have one wire type (integers and reals together as F32), and as a STRING in JSON form (`[1,"a"]`) when a var list has
 * no common wire type; `error` (when given) says why a list over VLQT_LIST_VALUE_MAX_COUNT elements is not sent, and stays
 * empty for any other reason. */
bool toWireValue(const QVariant& v, QmlWireValue& out, QString* error = nullptr);

/* toWireValue, or the string form of the value when its type has no wire form (protocol.h: "sent as their string form").
 * false only for a list over the limit (error as above): there is no string form for it. */
bool toWireValueOrString(const QVariant& v, QmlWireValue& out, QString* error);

/* The LIST value at the reader (already checked by readListLength) as a QVariantList of the single-value types of its
 * element type (f32 -> double, i32 -> int, bool, string, color -> QColor, rectf -> QRectF, vec2/3/4 -> QVector2D/3D/4D). */
QVariant listFromWire(WireReader& r);

/* The url of a url property for the text the patch sends (a Path or a string): an absolute local path (drive letter or UNC, either
 * separator) is a file URL; any other text is read as a URL, so "https:", "qrc:" and "file:" stay as written and
 * a relative one stays relative (the item that uses it resolves it against its file, QML's rule). QVariant's own conversion would
 * read a path with a drive letter as a URL whose scheme is the letter. */
QUrl urlFromPatch(const QString& text);

/* DATE_TIME: milliseconds since 1970 UTC as a QDateTime in UTC; INT64_MIN is the invalid QDateTime (no date). */
QDateTime dateTimeFromWire(qint64 msUtc);

/* FONT at the reader (length checked by readNamedEntry): the default font of the app with the family (when not empty), the pixel
 * size (at least 1), the weight (clamped to 1..1000) and italic of the value. */
QFont fontFromWire(WireReader& r);

/* A QVariantList from listFromWire as the type `target` of a property: QList<qreal>, <int>, <bool>, <QString>, <QUrl> (through
 * urlFromPatch), <QColor>, <QPointF>, <QSizeF> (from VEC2), <QVector2D>, <QVector3D>, <QVector4D>, <QRectF>, <QDateTime>, or
 * QVariantList / var as they are.
 * false = no such list type or an element does not convert. */
bool convertListToType(const QVariantList& list, QMetaType target, QVariant& out);

/* The patch sends point and size as VEC2 (QVector2D), and QVariant has no conversion from it: v as the QPointF, QPoint, QSizeF
 * or QSize that target names; any other v and target unchanged. */
QVariant adaptVector2D(const QVariant& v, QMetaType target);

} // namespace vlqt
