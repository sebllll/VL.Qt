#include "elements/element_types.h"

#include <QtQml/QQmlParserStatus>
#include <QtQuick/private/qquickrectangle_p.h> // QQuickRectangle: built in C++, typed setters

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Rectangle: a QQuickRectangle built in C++, written through its typed setters (Qt compares first, so an unchanged
// value is a no-op, and no QVariant is made per SET).

QQuickItem* makeRectangle()
{
    // The QML creation order, as for a Rectangle {} element: classBegin, properties, componentComplete (both are
    // protected in the class, public in QQmlParserStatus).
    auto* r = new QQuickRectangle();
    QQmlParserStatus* status = r;
    status->classBegin();
    // Qt's default border width is 1 and draws a border as soon as the border color is set. The C# default
    // StrokeWidth is 0, so a lone SET border color must not show one.
    r->border()->setWidth(0);
    status->componentComplete();
    return r;
}

QQuickRectangle* rect(ElementEntry& n) { return static_cast<QQuickRectangle*>(n.item); }

bool setColor(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    rect(n)->setColor(toColor(v.f));
    return true;
}

bool setRadius(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    rect(n)->setRadius(v.f[0]);
    return true;
}

bool setBorderColor(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    rect(n)->border()->setColor(toColor(v.f));
    return true;
}

bool setBorderWidth(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    rect(n)->border()->setWidth(v.f[0]);
    return true;
}

std::vector<PropDef> rectangleProps()
{
    return {
        {VLQT_PROPERTY_COLOR, VLQT_VALUE_TYPE_COLOR, setColor, nullptr},
        {VLQT_PROPERTY_RADIUS, VLQT_VALUE_TYPE_F32, setRadius, nullptr},
        {VLQT_PROPERTY_BORDER_COLOR, VLQT_VALUE_TYPE_COLOR, setBorderColor, nullptr},
        {VLQT_PROPERTY_BORDER_WIDTH, VLQT_VALUE_TYPE_F32, setBorderWidth, nullptr},
    };
}

} // namespace

TypeDef rectangleType()
{
    return {VLQT_ELEMENT_TYPE_RECTANGLE, "RECTANGLE", nullptr, rectangleProps(), makeRectangle};
}

} // namespace vlqt
