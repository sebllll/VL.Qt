#include "pathelement.h"
#include "protocol.h"

#include <cstring>

namespace vlqt {

namespace {

// Point i of an (x, y) f32 array in the command buffer, which has no guaranteed alignment.
QPointF pointAt(const unsigned char* xy, quint32 i)
{
    float p[2];
    std::memcpy(p, xy + size_t(i) * 8, sizeof p);
    return QPointF(p[0], p[1]);
}

} // namespace

void VlPathElement::setPoints(const unsigned char* xy, quint32 count)
{
    m_points.resize(size_t(count) * 2);
    if (count)
        std::memcpy(m_points.data(), xy, m_points.size() * sizeof(float));
    m_isPath = false;
    rebuildPoints();
}

void VlPathElement::setClosed(bool closed)
{
    if (m_closed == closed)
        return;
    m_closed = closed;
    if (!m_isPath)
        rebuildPoints();
}

void VlPathElement::rebuildPoints()
{
    QPainterPath p;
    const size_t count = m_points.size() / 2;
    if (count) {
        p.reserve(int(count) + (m_closed ? 1 : 0));
        p.moveTo(m_points[0], m_points[1]);
        for (size_t i = 1; i < count; ++i)
            p.lineTo(m_points[2 * i], m_points[2 * i + 1]);
        if (m_closed)
            p.closeSubpath();
    }
    m_path = p;
    // QQuickPath::processPath runs addToPath again on changed(); without it the old path stays on screen (spike Q2).
    emit changed();
}

bool VlPathElement::setPath(const unsigned char* verbs, quint32 verbCount, const unsigned char* xy, quint32 pointCount)
{
    QPainterPath p;
    p.reserve(int(pointCount + verbCount));
    quint32 k = 0;
    for (quint32 v = 0; v < verbCount; ++v) {
        static constexpr quint32 need[] = {1, 1, 2, 3, 0}; // VLQT_VERB_MOVE .. VLQT_VERB_CLOSE
        const unsigned char verb = verbs[v];
        if (verb > VLQT_VERB_CLOSE || k + need[verb] > pointCount)
            return false;
        switch (verb) {
        case VLQT_VERB_MOVE: p.moveTo(pointAt(xy, k)); break;
        case VLQT_VERB_LINE: p.lineTo(pointAt(xy, k)); break;
        case VLQT_VERB_QUAD: p.quadTo(pointAt(xy, k), pointAt(xy, k + 1)); break;
        case VLQT_VERB_CUBIC: p.cubicTo(pointAt(xy, k), pointAt(xy, k + 1), pointAt(xy, k + 2)); break;
        case VLQT_VERB_CLOSE: p.closeSubpath(); break;
        }
        k += need[verb];
    }
    if (k != pointCount)
        return false;
    m_points = {};
    m_isPath = true;
    m_path = p;
    emit changed();
    return true;
}

void VlPathElement::addToPath(QPainterPath& path, const QQuickPathData&)
{
    // QQuickPath::createPath starts with moveTo(startX, startY); as the only element we replace that path (implicitly
    // shared, O(1)). Behind other elements the path is appended.
    if (path.elementCount() <= 1)
        path = m_path;
    else
        path.addPath(m_path);
}

} // namespace vlqt
