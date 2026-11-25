#pragma once
// PositionerAlign: the cross-axis alignment of a Row or Column. Qt's positioners only set the main axis (Row: x,
// Column: y) and have no cross-axis alignment; this child object of the positioner sets the other coordinate of the
// children after every positioning pass and on a size change of the cross axis. Like Qt's own positioners it skips
// children that are not visible (VISIBLE off: no space and no alignment, they are aligned when they come back).
// Private Qt API: QQuickBasePositioner, QQuickItemPrivate::childItems and explicitVisible; check on Qt updates (list:
// elements/elements.h).
#include <QtCore/QObject>
#include <QtCore/QtTypes>

class QQuickBasePositioner;

namespace vlqt {

class PositionerAlign : public QObject
{
    Q_OBJECT
public:
    enum class Axis { Horizontal, Vertical }; // the axis that is aligned: Column aligns x, Row aligns y

    // Owned by the positioner (QObject child), so it dies with it.
    PositionerAlign(QQuickBasePositioner* positioner, Axis axis);

    // 0 = start (Left/Top), 0.5 = center, 1 = end (Right/Bottom) of the content extent; aligns at once.
    void setFactor(qreal factor);

    // The helper of a positioner made by makePositioner, nullptr for another item.
    static PositionerAlign* of(QObject* positioner);

private:
    void align();

    QQuickBasePositioner* m_positioner;
    Axis m_axis;
    qreal m_factor = 0;
};

} // namespace vlqt
