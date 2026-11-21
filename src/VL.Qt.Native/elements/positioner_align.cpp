// Private Qt API: QQuickBasePositioner, QQuickItemPrivate::childItems and explicitVisible; check on Qt updates (list: elements/elements.h).
#include "elements/positioner_align.h"

#include <QtQuick/private/qquickitem_p.h>        // childItems and explicitVisible of the children
#include <QtQuick/private/qquickpositioners_p.h> // QQuickBasePositioner: paddings, positioningComplete

namespace vlqt {

PositionerAlign::PositionerAlign(QQuickBasePositioner* positioner, Axis axis)
    : QObject(positioner), m_positioner(positioner), m_axis(axis)
{
    connect(positioner, &QQuickBasePositioner::positioningComplete, this, &PositionerAlign::align);
    // The extent the children are aligned in: a Row's height, a Column's width (implicit = content while AUTO_SIZE is on).
    if (axis == Axis::Vertical)
        connect(positioner, &QQuickItem::heightChanged, this, &PositionerAlign::align);
    else
        connect(positioner, &QQuickItem::widthChanged, this, &PositionerAlign::align);
}

PositionerAlign* PositionerAlign::of(QObject* positioner)
{
    return positioner->findChild<PositionerAlign*>(QString(), Qt::FindDirectChildrenOnly);
}

void PositionerAlign::setFactor(qreal factor)
{
    if (m_factor == factor)
        return;
    m_factor = factor;
    align();
}

void PositionerAlign::align()
{
    const bool vertical = m_axis == Axis::Vertical;
    const qreal start = vertical ? m_positioner->topPadding() : m_positioner->leftPadding();
    const qreal end = vertical ? m_positioner->bottomPadding() : m_positioner->rightPadding();
    const qreal extent = (vertical ? m_positioner->height() : m_positioner->width()) - start - end;
    for (QQuickItem* c : QQuickItemPrivate::get(m_positioner)->childItems) {
        if (!QQuickItemPrivate::get(c)->explicitVisible)
            continue;
        if (vertical)
            c->setY(start + (extent - c->height()) * m_factor);
        else
            c->setX(start + (extent - c->width()) * m_factor);
    }
}

} // namespace vlqt
