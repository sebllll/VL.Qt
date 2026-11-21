/* user_qml/qml_wire.h - values of the back channel of the QmlElement (protocol.h "QmlElement") in wire form.
 *
 * toWireValue turns a QVariant (a root property or a signal argument) into one of the value types of the protocol:
 * bool, the integer types (clamped to i32), real (f32), string/url, color, vector2d/3d/4d, rect, and the QML types
 * `point` and `size`, which both arrive as VEC2 (x, y) and (width, height).
 */
#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QVariant>

#include <vector>

namespace vlqt {

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

/* toWireValue, or the string form of the value when its type has no wire form (protocol.h: "sent as their string form"). */
QmlWireValue toWireValueOrString(const QVariant& v);

} // namespace vlqt
