#include "elements/element_types.h"

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Rectangle. border.width 0 matches the C# default StrokeWidth 0: Qt's default width is 1 and
// draws a border as soon as border.color is set, so a lone SET border color must not show one.

constexpr const char* kRectangleQml = "import QtQuick\nRectangle { border.width: 0 }\n";

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
