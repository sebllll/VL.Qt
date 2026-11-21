#include "elements/element_types.h"

#include <QtCore/QRectF>
#include <QtQuick/QQuickItem>

#include <unordered_map>

namespace vlqt {

// ---------------------------------------------------------------------------------------------
// Generic setters for QML properties (resolved QMetaProperty).

bool setQmlBool(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, v.b, err);
}

bool setQmlReal(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, qreal(v.f[0]), err);
}

bool setQmlRect(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, QRectF(v.f[0], v.f[1], v.f[2], v.f[3]), err);
}

bool setQmlInt(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, int(v.i), err);
}

bool setQmlString(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    return writeQml(n.item, rp, toQString(v), err);
}

namespace {

// ---------------------------------------------------------------------------------------------
// Type table: one entry per element type, defined in its type file. A new type or property is an entry, no new export.

const std::unordered_map<quint16, TypeDef>& typeTable()
{
    static const std::unordered_map<quint16, TypeDef> t = {
        {VLQT_ELEMENT_TYPE_GROUP, groupType()},
        {VLQT_ELEMENT_TYPE_RECTANGLE, rectangleType()},
        {VLQT_ELEMENT_TYPE_TEXT, textType()},
        {VLQT_ELEMENT_TYPE_IMAGE, imageType()},
        {VLQT_ELEMENT_TYPE_ROW, rowType()},
        {VLQT_ELEMENT_TYPE_COLUMN, columnType()},
        {VLQT_ELEMENT_TYPE_VIDEO, videoType()},
        {VLQT_ELEMENT_TYPE_BUTTON, buttonType()},
        {VLQT_ELEMENT_TYPE_SPIN_BOX, spinBoxType()},
        {VLQT_ELEMENT_TYPE_CHECK_BOX, checkBoxType()},
        {VLQT_ELEMENT_TYPE_SWITCH, switchType()},
        {VLQT_ELEMENT_TYPE_SLIDER, sliderType()},
        {VLQT_ELEMENT_TYPE_TEXT_FIELD, textFieldType()},
        {VLQT_ELEMENT_TYPE_SHADER, shaderType()},
        {VLQT_ELEMENT_TYPE_QML, qmlType()},
        {VLQT_ELEMENT_TYPE_SHAPE, shapeType()},
    };
    return t;
}

} // namespace

const TypeDef* findType(quint16 type)
{
    const auto& t = typeTable();
    auto it = t.find(type);
    return it != t.end() ? &it->second : nullptr;
}

bool writeQml(QQuickItem* item, const ResolvedProp& rp, const QVariant& v, std::string& err)
{
    if (!rp.prop.write(item, v)) {
        err = std::string("writing QML property '") + rp.def->qmlName + "' failed";
        return false;
    }
    return true;
}

} // namespace vlqt
