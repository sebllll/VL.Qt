// VL.Qt base style: Switch. Frame = background (padding area), Track = the lane (Track colour off, TrackFill
// on; the state ranks Hovered/Pressed above Checked, so the lane follows `checked`), Indicator = knob.
// Values from the attached VlStyle (see Button.qml). No label by default (spacing 0, lane centred in larger bounds).
import QtQuick
import QtQuick.Templates as T
import VLQt.Style

T.Switch {
    id: control

    VlStyle.state: VlStyle.stateOf(control.enabled, control.down, control.hovered, control.checked)

    implicitWidth: Math.max(VlStyle.frame.minWidth, implicitBackgroundWidth + leftInset + rightInset,
                            implicitContentWidth + leftPadding + rightPadding,
                            implicitIndicatorWidth + leftPadding + rightPadding)
    implicitHeight: Math.max(VlStyle.frame.minHeight, implicitBackgroundHeight + topInset + bottomInset,
                             implicitContentHeight + topPadding + bottomPadding,
                             implicitIndicatorHeight + topPadding + bottomPadding)

    padding: 0
    leftPadding: VlStyle.frame.paddingX
    rightPadding: VlStyle.frame.paddingX
    topPadding: VlStyle.frame.paddingY
    bottomPadding: VlStyle.frame.paddingY
    spacing: 0
    font: VlStyle.text.font

    indicator: Rectangle {
        x: control.text ? control.leftPadding : control.leftPadding + (control.availableWidth - width) / 2
        y: control.topPadding + (control.availableHeight - height) / 2
        implicitWidth: 2 * control.VlStyle.indicator.size
        implicitHeight: control.VlStyle.indicator.size
        radius: control.VlStyle.track.radius
        color: control.checked ? control.VlStyle.track.fillColor : control.VlStyle.track.color

        Rectangle {
            x: Math.max(0, Math.min(parent.width - width, control.visualPosition * parent.width - width / 2))
            width: control.VlStyle.indicator.size
            height: control.VlStyle.indicator.size
            radius: control.VlStyle.indicator.radius
            color: control.VlStyle.indicator.color
            border.color: control.VlStyle.indicator.borderColor
            border.width: control.VlStyle.indicator.borderWidth
        }
    }

    contentItem: Text {
        leftPadding: control.text ? control.indicator.width + control.spacing : 0
        text: control.text
        font: control.font
        color: control.VlStyle.text.color
        verticalAlignment: Text.AlignVCenter
        elide: Text.ElideRight
    }

    background: Rectangle {
        color: control.VlStyle.frame.color
        radius: control.VlStyle.frame.radius
        border.color: control.VlStyle.frame.borderColor
        border.width: control.VlStyle.frame.borderWidth
    }
}
