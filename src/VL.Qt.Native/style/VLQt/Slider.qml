// VL.Qt base style (#292): Slider. Frame = background (padding area), Track = groove + TrackFill (value part),
// Indicator = handle. Values from the attached VlStyle (see Button.qml).
import QtQuick
import QtQuick.Templates as T
import VLQt.Style

T.Slider {
    id: control

    VlStyle.state: VlStyle.stateOf(control.enabled, control.pressed, control.hovered, false)

    // AUTO_SIZE: along the axis at least 8 handle widths, across the handle (or the track) plus padding.
    implicitWidth: Math.max(VlStyle.frame.minWidth,
                            horizontal ? 8 * VlStyle.indicator.size + leftPadding + rightPadding
                                       : Math.max(VlStyle.indicator.size, VlStyle.track.thickness) + leftPadding + rightPadding)
    implicitHeight: Math.max(VlStyle.frame.minHeight,
                             horizontal ? Math.max(VlStyle.indicator.size, VlStyle.track.thickness) + topPadding + bottomPadding
                                        : 8 * VlStyle.indicator.size + topPadding + bottomPadding)

    padding: 0
    leftPadding: VlStyle.frame.paddingX
    rightPadding: VlStyle.frame.paddingX
    topPadding: VlStyle.frame.paddingY
    bottomPadding: VlStyle.frame.paddingY

    background: Rectangle {
        color: control.VlStyle.frame.color
        radius: control.VlStyle.frame.radius
        border.color: control.VlStyle.frame.borderColor
        border.width: control.VlStyle.frame.borderWidth

        Rectangle {
            x: control.horizontal ? control.leftPadding : control.leftPadding + (control.availableWidth - width) / 2
            y: control.horizontal ? control.topPadding + (control.availableHeight - height) / 2 : control.topPadding
            width: control.horizontal ? control.availableWidth : control.VlStyle.track.thickness
            height: control.horizontal ? control.VlStyle.track.thickness : control.availableHeight
            radius: control.VlStyle.track.radius
            color: control.VlStyle.track.color

            Rectangle {
                x: 0
                y: control.horizontal ? 0 : parent.height - height
                width: control.horizontal ? control.position * parent.width : parent.width
                height: control.horizontal ? parent.height : control.position * parent.height
                radius: control.VlStyle.track.radius
                color: control.VlStyle.track.fillColor
            }
        }
    }

    handle: Rectangle {
        x: control.leftPadding + (control.horizontal ? control.visualPosition * (control.availableWidth - width)
                                                     : (control.availableWidth - width) / 2)
        y: control.topPadding + (control.horizontal ? (control.availableHeight - height) / 2
                                                    : control.visualPosition * (control.availableHeight - height))
        implicitWidth: control.VlStyle.indicator.size
        implicitHeight: control.VlStyle.indicator.size
        color: control.VlStyle.indicator.color
        radius: control.VlStyle.indicator.radius
        border.color: control.VlStyle.indicator.borderColor
        border.width: control.VlStyle.indicator.borderWidth
    }
}
