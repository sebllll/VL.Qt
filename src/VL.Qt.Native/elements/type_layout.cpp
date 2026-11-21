#include "elements/element_types.h"
#include "elements/geometry.h"
#include "elements/positioner_align.h"

#include <QtQml/QQmlParserStatus>
#include <QtQuick/private/qquickpositioners_p.h> // QQuickRow, QQuickColumn: built in C++, typed setters

#include <utility>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Row/Column: Qt positioners (QtQuick Row/Column, not QtQuick.Layouts) built in C++ and written through their typed
// setters, no QML file and no JavaScript. They only set the main-axis coordinate (Column: y, Row: x) and never touch
// child sizes, so the SIZE of a child stays its size. Positioners have no cross-axis alignment: PositionerAlign (a child
// object of the positioner) adds it, CHILD_ALIGNMENT (Qt::AlignmentFlag, = C# HorizontalAlignment / VerticalAlignment)
// sets its factor. Children with width or height 0 or visible false take no space (Qt skips them); PositionerAlign
// skips the invisible ones as well. The layout owns x/y of its children (no anchor placement, ANCHOR is only the pivot).
// TRANSFORM of a child stays visual: it acts in the child's local coordinates after positioning
// and does not move siblings.

// The QML creation order, as for a Row {} element: classBegin, properties, componentComplete (the positioner does not
// position before that).
template <class Positioner>
QQuickItem* makePositioner(PositionerAlign::Axis axis)
{
    auto* p = new Positioner();
    QQmlParserStatus* status = p;
    status->classBegin();
    new PositionerAlign(p, axis);
    status->componentComplete();
    return p;
}

QQuickItem* makeColumn() { return makePositioner<QQuickColumn>(PositionerAlign::Axis::Horizontal); }
QQuickItem* makeRow() { return makePositioner<QQuickRow>(PositionerAlign::Axis::Vertical); }

QQuickBasePositioner* positionerOf(ElementEntry& n) { return static_cast<QQuickBasePositioner*>(n.item); }

bool setSpacing(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    positionerOf(n)->setSpacing(v.f[0]);
    return true;
}

bool setPadding(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    positionerOf(n)->setPadding(v.f[0]);
    return true;
}

// Only the three flags of the axis are accepted (others -> ApplyBadValue).
template <int Start, int End, int Center>
bool setChildAlignment(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (!checkEnum<Start, End, Center>(v, err))
        return false;
    PositionerAlign::of(n.item)->setFactor(v.i == Start ? 0 : v.i == End ? 1 : 0.5);
    return true;
}

std::vector<PropDef> columnProps()
{
    return {
        {VLQT_PROPERTY_SPACING, VLQT_VALUE_TYPE_F32, setSpacing, nullptr},
        {VLQT_PROPERTY_PADDING, VLQT_VALUE_TYPE_F32, setPadding, nullptr},
        // Qt::AlignLeft/Right/HCenter (= C# HorizontalAlignment)
        {VLQT_PROPERTY_CHILD_ALIGNMENT, VLQT_VALUE_TYPE_I32, setChildAlignment<Qt::AlignLeft, Qt::AlignRight, Qt::AlignHCenter>, nullptr},
    };
}

std::vector<PropDef> rowProps()
{
    return {
        {VLQT_PROPERTY_SPACING, VLQT_VALUE_TYPE_F32, setSpacing, nullptr},
        {VLQT_PROPERTY_PADDING, VLQT_VALUE_TYPE_F32, setPadding, nullptr},
        // Qt::AlignTop/Bottom/VCenter (= C# VerticalAlignment)
        {VLQT_PROPERTY_CHILD_ALIGNMENT, VLQT_VALUE_TYPE_I32, setChildAlignment<Qt::AlignTop, Qt::AlignBottom, Qt::AlignVCenter>, nullptr},
    };
}

TypeDef positioner(quint16 id, const char* name, QQuickItem* (*make)(), std::vector<PropDef> props)
{
    TypeDef t{id, name, nullptr, std::move(props), make};
    t.autoSize = AutoSize::Always; // implicit size = content + padding
    t.layout = true;
    t.container = true;
    return t;
}

} // namespace

TypeDef rowType()
{
    return positioner(VLQT_ELEMENT_TYPE_ROW, "ROW", makeRow, rowProps());
}

TypeDef columnType()
{
    return positioner(VLQT_ELEMENT_TYPE_COLUMN, "COLUMN", makeColumn, columnProps());
}

} // namespace vlqt
