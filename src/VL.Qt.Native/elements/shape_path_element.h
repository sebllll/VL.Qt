#pragma once
// ShapePathElement: the one path element of a SHAPE's ShapePath (makeShape in type_shape.cpp). Takes a point list
// (POINTS + CLOSED: polyline or polygon) or a verb/point array (PATH) and builds the QPainterPath in addToPath, like
// Qt's own PathPolyline. A closed point list ends with closeSubpath (a real join at the start point, no cap). The
// latest of POINTS and PATH wins: each replaces the other.
#include <QtGui/QPainterPath>
#include <QtQuick/private/qquickpath_p.h>

#include <vector>

namespace vlqt {

class ShapePathElement : public QQuickCurve
{
    Q_OBJECT
public:
    explicit ShapePathElement(QObject* parent = nullptr) : QQuickCurve(parent) {}

    // Point list of count (x, y) f32 pairs, copied. xy points into the command buffer and may be unaligned (read with
    // memcpy, see Value in element_types.h).
    void setPoints(const unsigned char* xy, quint32 count);
    // Closes the point list (polygon); ignored by a path, kept for the next point list.
    void setClosed(bool closed);
    // Path of verbCount verbs (VLQT_VERB_*) and pointCount (x, y) f32 pairs, converted to a QPainterPath (the copy).
    // False when a verb is unknown or the points do not match the verbs; the old geometry stays then.
    bool setPath(const unsigned char* verbs, quint32 verbCount, const unsigned char* xy, quint32 pointCount);

    void addToPath(QPainterPath& path, const QQuickPathData& data) override;

private:
    void rebuildPoints();

    std::vector<float> m_points; // point list (x, y pairs); empty while a path is set
    bool m_closed = false;
    bool m_isPath = false;       // m_path comes from setPath (m_points unused)
    QPainterPath m_path;
};

} // namespace vlqt
