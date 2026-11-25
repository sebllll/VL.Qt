// target null: the handler moves/scales nothing, it only reports. The wheel event (QQuickWheelEvent) is no public C++
// API, so the notches are summed up in QML and taken by Handlers::takeWheel.
import QtQuick
WheelHandler {
    target: null
    property point vlqtDelta
    onWheel: (event) => vlqtDelta = Qt.point(vlqtDelta.x + event.angleDelta.x / 120, vlqtDelta.y + event.angleDelta.y / 120)
}
