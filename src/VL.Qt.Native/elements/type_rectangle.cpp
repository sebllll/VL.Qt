#include "elements/element_types.h"

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Rectangle (qml/elements/RectangleElement.qml).

constexpr const char* kRectangleQml = "qrc:/vlqt/qml/elements/RectangleElement.qml";

std::vector<PropDef> rectangleProps()
{
    return {
        {VLQT_PROPERTY_COLOR, VLQT_VALUE_TYPE_COLOR, setQmlColor, "color"},
        {VLQT_PROPERTY_RADIUS, VLQT_VALUE_TYPE_F32, setQmlReal, "radius"},
        {VLQT_PROPERTY_BORDER_COLOR, VLQT_VALUE_TYPE_COLOR, setQmlColor, "border.color"},
        {VLQT_PROPERTY_BORDER_WIDTH, VLQT_VALUE_TYPE_F32, setQmlReal, "border.width"},
    };
}

} // namespace

TypeDef rectangleType()
{
    return {VLQT_ELEMENT_TYPE_RECTANGLE, "RECTANGLE", kRectangleQml, rectangleProps()};
}

} // namespace vlqt
