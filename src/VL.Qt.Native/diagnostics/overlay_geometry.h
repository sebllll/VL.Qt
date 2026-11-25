// The vertex of the overlay geometry (QSGVertexColorMaterial: premultiplied colours), shared by the perf items and the
// debug overlay.
#pragma once

#include <QtGui/QRgb>
#include <QtQuick/QSGGeometry>

namespace vlqt {

using OverlayVertex = QSGGeometry::ColoredPoint2D;

// c is premultiplied: qPremultiply(color) once per primitive, not per vertex.
inline void setVertex(OverlayVertex& v, float x, float y, QRgb c)
{
    v.set(x, y, uchar(qRed(c)), uchar(qGreen(c)), uchar(qBlue(c)), uchar(qAlpha(c)));
}

// The straight-alpha colour of a vertex (software backend: painted with QPainter).
inline QRgb vertexColor(const OverlayVertex& v) { return qUnpremultiply(qRgba(v.r, v.g, v.b, v.a)); }

} // namespace vlqt
