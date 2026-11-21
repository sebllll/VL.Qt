// Private Qt API: QQuickShape, QQuickShapePath, QQuickShapeTrim (typed setters); check on Qt updates (list: elements/elements.h).
#include "elements/element_types.h"
#include "elements/shape_path_element.h"

#include <QtQuickShapes/private/qquickshape_p.h>         // SHAPE built in C++, typed setters

#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Shape (protocol.h 960..): QQuickShape with the CurveRenderer and one QQuickShapePath whose only path element is
// a ShapePathElement (shape_path_element.h). Built in C++ (no QML snippet), so the setters reach the ShapePath and the element
// directly (typed, no QVariant writes). Defaults as the C# Shape element: no geometry, not closed, fill
// transparent, stroke white with width -1 (no stroke), cap Flat, join Miter, miter limit 4, solid, trim 0/1/0, NonZero.
// The item keeps width/height 0: the shape draws outside its frame, the element size is the extent of the geometry.

struct ShapeItem final : QQuickShape {
    QQuickShapePath* path = nullptr;   // child of the item
    ShapePathElement* element = nullptr;  // child of path
};

QQuickItem* makeShape()
{
    // The QML creation order: classBegin, children, componentComplete (QQuickPath connects the elements' changed() to
    // processPath there, QQuickShape creates its renderer). Both are protected in the classes, public in QQmlParserStatus.
    auto* shape = new ShapeItem();
    QQmlParserStatus* shapeStatus = shape;
    shapeStatus->classBegin();
    shape->setPreferredRendererType(QQuickShape::CurveRenderer);
    auto* sp = new QQuickShapePath(shape);
    QQmlParserStatus* pathStatus = sp;
    pathStatus->classBegin();
    // Qt's ShapePath defaults differ (width 1, fill white, SquareCap, BevelJoin, miter limit 2, OddEvenFill).
    sp->setStrokeColor(Qt::white);
    sp->setStrokeWidth(-1);
    sp->setFillColor(Qt::transparent);
    sp->setCapStyle(QQuickShapePath::FlatCap);
    sp->setJoinStyle(QQuickShapePath::MiterJoin);
    sp->setMiterLimit(4);
    sp->setFillRule(QQuickShapePath::WindingFill);
    auto* element = new ShapePathElement(sp);
    auto elements = sp->pathElements();
    elements.append(&elements, element);
    pathStatus->componentComplete();
    auto data = shape->data();
    data.append(&data, sp);
    shapeStatus->componentComplete();
    shape->path = sp;
    shape->element = element;
    return shape;
}

QQuickShapePath* shapePath(const ElementEntry& n) { return static_cast<ShapeItem*>(n.item)->path; }

bool setShapePoints(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.floatCount % 2) {
        err = "point list needs x, y pairs, got " + std::to_string(v.floatCount) + " values";
        return false;
    }
    static_cast<ShapeItem*>(n.item)->element->setPoints(v.floats, v.floatCount / 2);
    return true;
}

bool setShapeClosed(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    static_cast<ShapeItem*>(n.item)->element->setClosed(v.b);
    return true;
}

bool setShapePath(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    // Verbs and point count are checked in Scene::set already; the element checks again before it touches the data.
    if (static_cast<ShapeItem*>(n.item)->element->setPath(v.verbs, v.verbCount, v.points, v.pointCount))
        return true;
    err = "path verbs and points do not match";
    return false;
}

bool setShapeFill(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    shapePath(n)->setFillColor(toColor(v.f));
    return true;
}

bool setShapeStrokeColor(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    shapePath(n)->setStrokeColor(toColor(v.f));
    return true;
}

// < 0 = no stroke (Qt draws none for a negative width).
bool setShapeStrokeWidth(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    shapePath(n)->setStrokeWidth(v.f[0]);
    return true;
}

bool setShapeCap(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    static constexpr QQuickShapePath::CapStyle caps[] = {QQuickShapePath::FlatCap, QQuickShapePath::SquareCap,
                                                         QQuickShapePath::RoundCap};
    if (v.i < 0 || v.i > 2) {
        err = "cap must be 0 (Flat), 1 (Square) or 2 (Round), got " + std::to_string(v.i);
        return false;
    }
    shapePath(n)->setCapStyle(caps[v.i]);
    return true;
}

bool setShapeJoin(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    static constexpr QQuickShapePath::JoinStyle joins[] = {QQuickShapePath::MiterJoin, QQuickShapePath::BevelJoin,
                                                           QQuickShapePath::RoundJoin};
    if (v.i < 0 || v.i > 2) {
        err = "join must be 0 (Miter), 1 (Bevel) or 2 (Round), got " + std::to_string(v.i);
        return false;
    }
    shapePath(n)->setJoinStyle(joins[v.i]);
    return true;
}

// ShapePath.miterLimit is an int: the f32 is rounded (docs/decisions/paths.md).
bool setShapeMiterLimit(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    shapePath(n)->setMiterLimit(int(std::lround(std::clamp(v.f[0], 0.f, 1.0e6f))));
    return true;
}

// Dash lengths in stroke-width units (C# divides by the width). Empty or all zero = solid line.
bool setShapeDash(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    QList<qreal> pattern;
    pattern.reserve(qsizetype(v.floatCount));
    qreal sum = 0;
    for (quint32 i = 0; i < v.floatCount; ++i) {
        float d;
        std::memcpy(&d, v.floats + size_t(i) * 4, sizeof d); // the buffer has no guaranteed alignment
        if (!std::isfinite(d) || d < 0) {
            err = "dash lengths must be finite and >= 0";
            return false;
        }
        pattern.append(d);
        sum += d;
    }
    QQuickShapePath* sp = shapePath(n);
    if (sum > 0) {
        sp->setDashPattern(pattern);
        sp->setStrokeStyle(QQuickShapePath::DashLine);
    } else {
        sp->setStrokeStyle(QQuickShapePath::SolidLine);
    }
    return true;
}

bool setShapeDashOffset(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    shapePath(n)->setDashOffset(v.f[0]);
    return true;
}

// Trim (QQuickShapeTrim, Qt 6.10): created on the first trim value, a pure trim change draws without changed().
template <void (QQuickShapeTrim::*Set)(qreal)>
bool setShapeTrim(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string&)
{
    (shapePath(n)->trim()->*Set)(v.f[0]);
    return true;
}

bool setShapeFillRule(Elements&, ElementEntry& n, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.i < 0 || v.i > 1) {
        err = "fill rule must be 0 (NonZero) or 1 (EvenOdd), got " + std::to_string(v.i);
        return false;
    }
    shapePath(n)->setFillRule(v.i == 0 ? QQuickShapePath::WindingFill : QQuickShapePath::OddEvenFill);
    return true;
}

std::vector<PropDef> shapeProps()
{
    return {
        {VLQT_PROPERTY_SHAPE_POINTS, VLQT_VALUE_TYPE_F32_ARRAY, setShapePoints, nullptr},
        {VLQT_PROPERTY_SHAPE_CLOSED, VLQT_VALUE_TYPE_BOOL, setShapeClosed, nullptr},
        {VLQT_PROPERTY_SHAPE_PATH, VLQT_VALUE_TYPE_PATH, setShapePath, nullptr},
        {VLQT_PROPERTY_SHAPE_FILL, VLQT_VALUE_TYPE_COLOR, setShapeFill, nullptr},
        {VLQT_PROPERTY_SHAPE_STROKE_COLOR, VLQT_VALUE_TYPE_COLOR, setShapeStrokeColor, nullptr},
        {VLQT_PROPERTY_SHAPE_STROKE_WIDTH, VLQT_VALUE_TYPE_F32, setShapeStrokeWidth, nullptr},
        {VLQT_PROPERTY_SHAPE_CAP, VLQT_VALUE_TYPE_I32, setShapeCap, nullptr},
        {VLQT_PROPERTY_SHAPE_JOIN, VLQT_VALUE_TYPE_I32, setShapeJoin, nullptr},
        {VLQT_PROPERTY_SHAPE_MITER_LIMIT, VLQT_VALUE_TYPE_F32, setShapeMiterLimit, nullptr},
        {VLQT_PROPERTY_SHAPE_DASH, VLQT_VALUE_TYPE_F32_ARRAY, setShapeDash, nullptr},
        {VLQT_PROPERTY_SHAPE_DASH_OFFSET, VLQT_VALUE_TYPE_F32, setShapeDashOffset, nullptr},
        {VLQT_PROPERTY_SHAPE_TRIM_START, VLQT_VALUE_TYPE_F32, setShapeTrim<&QQuickShapeTrim::setStart>, nullptr},
        {VLQT_PROPERTY_SHAPE_TRIM_END, VLQT_VALUE_TYPE_F32, setShapeTrim<&QQuickShapeTrim::setEnd>, nullptr},
        {VLQT_PROPERTY_SHAPE_TRIM_OFFSET, VLQT_VALUE_TYPE_F32, setShapeTrim<&QQuickShapeTrim::setOffset>, nullptr},
        {VLQT_PROPERTY_SHAPE_FILL_RULE, VLQT_VALUE_TYPE_I32, setShapeFillRule, nullptr},
    };
}

} // namespace

TypeDef shapeType()
{
    return {VLQT_ELEMENT_TYPE_SHAPE, "SHAPE", nullptr, shapeProps(), makeShape};
}

} // namespace vlqt
