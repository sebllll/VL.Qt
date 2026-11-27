/* elements/element_types.h - the type table of the elements: decoded SET values, property and type entries, and the
 * generic QML setters the type files (type_*.cpp) share. */
#pragma once

#include "elements/element_entry.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QByteArray>
#include <QtCore/QMetaProperty>
#include <QtCore/QString>
#include <QtCore/QtTypes>
#include <QtCore/QVariant>
#include <QtGui/QColor>

#include <string>
#include <vector>

class QObject;
class QQuickItem;

namespace vlqt {

/* Return codes of Scene::apply / vlqt_scene_apply: short names for the apply range of vlqt_result (vlqt.h). */
enum ApplyResult {
    ApplyOk = VLQT_OK,
    ApplyBadLength = VLQT_ERR_BAD_LENGTH,           // truncated buffer, wrong payloadLen for op / value type, bad alignment
    ApplyUnknownOp = VLQT_ERR_UNKNOWN_OP,
    ApplyUnknownId = VLQT_ERR_UNKNOWN_OBJECT,
    ApplyUnknownType = VLQT_ERR_UNKNOWN_TYPE,
    ApplyUnknownProperty = VLQT_ERR_UNKNOWN_PROPERTY, // property not supported by the element type (also logged once per type)
    ApplyBadValue = VLQT_ERR_BAD_ARGUMENT,          // value type does not match the property, or the setter rejected the value
    ApplyInvalid = VLQT_ERR_INVALID                 // e.g. CREATE with id 0 / existing id, REPARENT into own subtree
};

/* Decoded SET value. Pointers/strings are only valid during the setter call. */
struct Value {
    quint16 type = 0;          // VLQT_VALUE_TYPE_*
    float f[16] = {};          // f32 (f[0]), color (rgba), rectf (x, y, w, h), mat4 (row-major like Stride)
    qint32 i = 0;              // i32, ivec2 x
    qint32 i2 = 0;             // ivec2 y
    bool b = false;            // bool
    quint64 u = 0;             // u64 (resource id)
    QByteArray utf8;           // string (raw UTF-8, not copied: fromRawData)
    QObject* ref = nullptr;    // ref (resolved id; id 0 = nullptr, i.e. "no reference")
    // f32 array and path: views into the command buffer, not copied. The floats are 4-byte aligned within the buffer,
    // but the buffer itself has no guaranteed alignment: read them with std::memcpy, never through a float*.
    const unsigned char* floats = nullptr; // f32 array: floatCount x f32
    quint32 floatCount = 0;
    const unsigned char* verbs = nullptr;  // path: verbCount x u8 (VLQT_VERB_*, checked; pointCount matches the verbs)
    quint32 verbCount = 0;
    const unsigned char* points = nullptr; // path: pointCount x (x, y) f32
    quint32 pointCount = 0;
};

struct PropDef;

/* A table property resolved against the QML type (once per type and engine). */
struct ResolvedProp {
    const PropDef* def = nullptr;
    QMetaProperty prop;  // property on the item ("enabled")
};

class Elements;

/* Setter: returns false + err on a rejected value (-> ApplyBadValue). rp is resolved (or empty for
 * properties without a QML name, which use the typed C++ API of the item: Rectangle, Text, Image, Shape). */
using Setter = bool (*)(Elements& els, ElementEntry& node, const ResolvedProp& rp, const Value& v, std::string& err);

struct PropDef {
    quint16 id;              // VLQT_PROPERTY_*
    quint16 valueType;       // expected VLQT_VALUE_TYPE_*
    Setter set;
    const char* qmlName;     // QML property of the item ("enabled"), written through its QMetaProperty; nullptr = typed C++ API
};

/* Whether an element type takes AUTO_SIZE (implicit size instead of SIZE). The property itself is not in `props`:
 * Elements::set offers it for every type but None. */
enum class AutoSize : quint8 {
    None,      // always SIZE (Rectangle, Text, Image, Video, Shape)
    Always,    // containers, Button, value inputs, QML: the implicit size applies while AUTO_SIZE is on
    WithInput  // Shader (ShaderEffect): the implicit size is the Input's, so only while it has one; otherwise SIZE
};

/* Everything QtNative needs to know about an element type: one entry per type, defined in its type file. A new type is
 * an entry plus a line in typeTable(); nothing else asks for the type's id. */
struct TypeDef {
    quint16 id;              // VLQT_ELEMENT_TYPE_*
    const char* name;        // the type's name in logs and error texts, and (lower case) on the perf page
    const char* qmlUrl;      // qrc:/vlqt/qml/elements/<Name>Element.qml (nullptr = plain QQuickItem, no component)
    std::vector<PropDef> props; // type-specific (ids >= 256); common ones (1..255) apply to every type
    QQuickItem* (*make)() = nullptr; // C++ item instead of QML (Rectangle, Text, Image, Shape); qmlUrl is ignored then
    AutoSize autoSize = AutoSize::None;
    bool layout = false;     // Row/Column: a Qt positioner that owns x/y of its children (TransformState::inLayout)
    bool container = false;  // Group, Row, Column: its children are elements that the debug overlay measures
    bool styled = false;     // Controls: takes SET_STYLE
    void (*applyAutoSize)(QQuickItem* item, bool on) = nullptr; // the type's own part of applySize: kind of bounds (Group, QML)
    void (*beforeDestroy)(QQuickItem* item) = nullptr;          // Video: stop the player, release the audio tap; called by the Scene
};

const TypeDef* findType(quint16 type);

/* Helpers for setters. */
bool writeQml(QQuickItem* item, const ResolvedProp& rp, const QVariant& v, std::string& err);

inline QColor toColor(const float* f) { return QColor::fromRgbF(f[0], f[1], f[2], f[3]); }
inline QString toQString(const Value& v) { return QString::fromUtf8(v.utf8.constData(), v.utf8.size()); }

/* Generic setters for QML properties (resolved QMetaProperty), element_types.cpp. The types built in C++ (Rectangle,
 * Text, Image, Shape) use typed setters of their own instead: no QVariant per SET. */
bool setQmlBool(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err);
bool setQmlReal(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err);
bool setQmlRect(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err);
bool setQmlInt(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err);
bool setQmlString(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err);

/* i32 enum property: only the listed Qt values are accepted (others -> ApplyBadValue). */
template <int... Allowed>
bool checkEnum(const Value& v, std::string& err)
{
    if (((v.i != Allowed) && ...)) {
        err = "invalid enum value " + std::to_string(v.i);
        return false;
    }
    return true;
}

template <int... Allowed>
bool setQmlEnum(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return checkEnum<Allowed...>(v, err) && writeQml(n.item, rp, v.i, err);
}

/* Entries of the type table (findType), one function per type, defined in its file. */
TypeDef groupType();      // group_item.cpp
TypeDef rectangleType();  // type_rectangle.cpp
TypeDef textType();       // type_text.cpp
TypeDef imageType();      // type_image.cpp
TypeDef rowType();        // type_layout.cpp
TypeDef columnType();     // type_layout.cpp
TypeDef videoType();      // type_video.cpp
TypeDef buttonType();     // type_controls.cpp
TypeDef spinBoxType();    // type_controls.cpp
TypeDef checkBoxType();   // type_controls.cpp
TypeDef switchType();     // type_controls.cpp
TypeDef sliderType();     // type_controls.cpp
TypeDef textFieldType();  // type_controls.cpp
TypeDef shaderType();     // type_shader.cpp
TypeDef qmlType();        // type_qml.cpp
TypeDef shapeType();      // type_shape.cpp

/* Esc in the value inputs TextField and SpinBox (type_controls.cpp), called by the FocusRelease filter for the element
 * that holds the focus: discards the running edit and returns true, the filter then releases the focus. Returns false
 * for every other item, which receives Esc like any key. */
bool discardEdit(QQuickItem* owner);

} // namespace vlqt
