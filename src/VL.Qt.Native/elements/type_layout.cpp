#include "elements/element_types.h"
#include "elements/geometry.h"

#include <utility>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Row/Column: Qt positioners (QtQuick Row/Column, not QtQuick.Layouts). They only set the main-axis
// coordinate (Column: y, Row: x) and never touch child sizes, so the SIZE of a child stays its size.
// Positioners have no cross-axis alignment: the one-liner adds an int property childAlignment
// (Qt::AlignmentFlag, = C# HorizontalAlignment / VerticalAlignment) and sets the cross-axis coordinate
// of every child after each positioning pass (positioningComplete), on a size change of the container
// and on an alignment change. Reference extent = own width/height (implicit = content with AUTO_SIZE
// on) minus padding. Children with width or height 0 or visible false are skipped by Qt (not laid
// out). The layout owns x/y of its children (no anchor placement, ANCHOR is only the pivot).
// TRANSFORM of a child stays visual: it acts in the child's local coordinates after positioning
// and does not move siblings.

constexpr const char* kColumnQml =
    "import QtQuick\n"
    "Column {\n"
    "  property int childAlignment: Qt.AlignLeft\n"
    "  function vlqtAlign() {\n"
    "    const w = width - leftPadding - rightPadding;\n"
    "    const f = childAlignment === Qt.AlignRight ? 1 : childAlignment === Qt.AlignHCenter ? 0.5 : 0;\n"
    "    for (let i = 0; i < children.length; ++i) { const c = children[i]; c.x = leftPadding + (w - c.width) * f; }\n"
    "  }\n"
    "  onPositioningComplete: vlqtAlign()\n"
    "  onWidthChanged: vlqtAlign()\n"
    "  onChildAlignmentChanged: vlqtAlign()\n"
    "}\n";

constexpr const char* kRowQml =
    "import QtQuick\n"
    "Row {\n"
    "  property int childAlignment: Qt.AlignTop\n"
    "  function vlqtAlign() {\n"
    "    const h = height - topPadding - bottomPadding;\n"
    "    const f = childAlignment === Qt.AlignBottom ? 1 : childAlignment === Qt.AlignVCenter ? 0.5 : 0;\n"
    "    for (let i = 0; i < children.length; ++i) { const c = children[i]; c.y = topPadding + (h - c.height) * f; }\n"
    "  }\n"
    "  onPositioningComplete: vlqtAlign()\n"
    "  onHeightChanged: vlqtAlign()\n"
    "  onChildAlignmentChanged: vlqtAlign()\n"
    "}\n";

std::vector<PropDef> columnProps()
{
    return {
        {VLQT_PROPERTY_SPACING, VLQT_VALUE_TYPE_F32, setQmlReal, "spacing"},
        {VLQT_PROPERTY_PADDING, VLQT_VALUE_TYPE_F32, setQmlReal, "padding"},
        // Qt::AlignLeft/Right/HCenter (= C# HorizontalAlignment)
        {VLQT_PROPERTY_CHILD_ALIGNMENT, VLQT_VALUE_TYPE_I32, setQmlEnum<Qt::AlignLeft, Qt::AlignRight, Qt::AlignHCenter>, "childAlignment"},
    };
}

std::vector<PropDef> rowProps()
{
    return {
        {VLQT_PROPERTY_SPACING, VLQT_VALUE_TYPE_F32, setQmlReal, "spacing"},
        {VLQT_PROPERTY_PADDING, VLQT_VALUE_TYPE_F32, setQmlReal, "padding"},
        // Qt::AlignTop/Bottom/VCenter (= C# VerticalAlignment)
        {VLQT_PROPERTY_CHILD_ALIGNMENT, VLQT_VALUE_TYPE_I32, setQmlEnum<Qt::AlignTop, Qt::AlignBottom, Qt::AlignVCenter>, "childAlignment"},
    };
}

TypeDef positioner(quint16 id, const char* name, const char* qml, std::vector<PropDef> props)
{
    TypeDef t{id, name, qml, std::move(props)};
    t.autoSize = AutoSize::Always; // implicit size = content + padding
    t.layout = true;
    t.container = true;
    return t;
}

} // namespace

TypeDef rowType()
{
    return positioner(VLQT_ELEMENT_TYPE_ROW, "ROW", kRowQml, rowProps());
}

TypeDef columnType()
{
    return positioner(VLQT_ELEMENT_TYPE_COLUMN, "COLUMN", kColumnQml, columnProps());
}

} // namespace vlqt
