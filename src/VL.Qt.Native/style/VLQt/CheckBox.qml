// VL.Qt base style: CheckBox. Frame = background (padding area), box = Track (like the Slider
// groove and the Switch track), check mark = Indicator (like the handle and the knob).
// Values from the attached VlStyle (see Button.qml). No label by default (spacing 0, box centred in larger bounds).
import QtQuick
import QtQuick.Templates as T
import VLQt.Style

T.CheckBox {
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
        implicitWidth: control.VlStyle.indicator.size
        implicitHeight: control.VlStyle.indicator.size
        color: control.VlStyle.track.color
        radius: control.VlStyle.indicator.radius
        border.color: control.VlStyle.indicator.borderColor
        border.width: control.VlStyle.indicator.borderWidth

        // Check mark: an L (right and bottom bar) turned by 45 degrees.
        Item {
            readonly property real thickness: Math.max(1.5, parent.width * 0.12)
            width: parent.width * 0.3
            height: parent.width * 0.55
            x: (parent.width - width) / 2
            y: (parent.height - height) / 2 - parent.height * 0.08
            rotation: 45
            visible: control.checkState === Qt.Checked

            Rectangle {
                x: parent.width - parent.thickness
                width: parent.thickness
                height: parent.height
                color: control.VlStyle.indicator.color
            }
            Rectangle {
                y: parent.height - parent.thickness
                width: parent.width
                height: parent.thickness
                color: control.VlStyle.indicator.color
            }
        }

        // Partially checked (tristate; not exposed): a bar.
        Rectangle {
            x: parent.width * 0.25
            y: (parent.height - height) / 2
            width: parent.width * 0.5
            height: Math.max(1.5, parent.width * 0.12)
            color: control.VlStyle.indicator.color
            visible: control.checkState === Qt.PartiallyChecked
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
