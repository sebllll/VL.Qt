// VL.Qt base style: SpinBox. Frame = box, Text = value text, Indicator = the two buttons (- and + side by side
// at the right, each a square of the box height; own state per button via VlStyle.value). "Pressed" state of the
// control = a button is pressed. Values from the attached VlStyle (see Button.qml).
import QtQuick
import QtQuick.Controls.impl
import QtQuick.Controls.Basic.impl
import QtQuick.Templates as T
import VLQt.Style

T.SpinBox {
    id: control

    // Button edge: box height inside the frame border.
    readonly property real buttonSize: Math.max(0, height - 2 * VlStyle.frame.borderWidth)
    // Radius of the frame's inner edge (inside the border): the outer corners of the + button follow it, so the
    // button does not stick out over the rounded frame corners. Capped by the button edge.
    readonly property real innerRadius: Math.min(buttonSize / 2, Math.max(0, VlStyle.frame.radius - VlStyle.frame.borderWidth))

    VlStyle.state: VlStyle.stateOf(control.enabled, control.up.pressed || control.down.pressed, control.hovered, false)

    implicitWidth: Math.max(VlStyle.frame.minWidth, implicitBackgroundWidth + leftInset + rightInset,
                            implicitContentWidth + leftPadding + rightPadding)
    implicitHeight: Math.max(VlStyle.frame.minHeight, implicitBackgroundHeight + topInset + bottomInset,
                             implicitContentHeight + topPadding + bottomPadding)

    padding: 0
    leftPadding: VlStyle.frame.paddingX
    rightPadding: VlStyle.frame.paddingX + 2 * buttonSize + VlStyle.frame.borderWidth
    topPadding: VlStyle.frame.paddingY
    bottomPadding: VlStyle.frame.paddingY
    font: VlStyle.text.font

    validator: IntValidator {
        locale: control.locale.name
        bottom: Math.min(control.from, control.to)
        top: Math.max(control.from, control.to)
    }

    contentItem: TextInput {
        z: 2
        text: control.displayText
        clip: width < implicitWidth

        font: control.font
        color: control.VlStyle.text.color
        selectionColor: control.VlStyle.track.fillColor
        selectedTextColor: control.VlStyle.text.color
        horizontalAlignment: Qt.AlignLeft
        verticalAlignment: Qt.AlignVCenter

        selectByMouse: true
        readOnly: !control.editable
        validator: control.validator
        inputMethodHints: Qt.ImhFormattedNumbersOnly

        ContextMenu.menu: TextEditingContextMenu {
            editor: parent
        }
    }

    // The buttons read control.VlStyle.revision, so a changed table re-evaluates value().
    up.indicator: Rectangle {
        id: upButton
        readonly property int vlState: control.VlStyle.stateOf(control.enabled, control.up.pressed, control.up.hovered, false)
        readonly property color mark: (control.VlStyle.revision, control.VlStyle.value(VlStyle.TextColor, vlState))
        x: control.width - control.VlStyle.frame.borderWidth - width
        y: control.VlStyle.frame.borderWidth
        width: control.buttonSize
        height: control.buttonSize
        color: (control.VlStyle.revision, control.VlStyle.value(VlStyle.IndicatorColor, vlState))
        radius: (control.VlStyle.revision, control.VlStyle.value(VlStyle.IndicatorRadius, vlState))
        topRightRadius: control.innerRadius
        bottomRightRadius: control.innerRadius
        border.color: (control.VlStyle.revision, control.VlStyle.value(VlStyle.BorderColor, vlState))
        border.width: (control.VlStyle.revision, control.VlStyle.value(VlStyle.IndicatorBorderWidth, vlState))

        Rectangle {
            x: (parent.width - width) / 2
            y: (parent.height - height) / 2
            width: parent.width / 3
            height: Math.max(1, parent.width / 12)
            color: upButton.mark
        }
        Rectangle {
            x: (parent.width - width) / 2
            y: (parent.height - height) / 2
            width: Math.max(1, parent.width / 12)
            height: parent.width / 3
            color: upButton.mark
        }
    }

    down.indicator: Rectangle {
        id: downButton
        readonly property int vlState: control.VlStyle.stateOf(control.enabled, control.down.pressed, control.down.hovered, false)
        readonly property color mark: (control.VlStyle.revision, control.VlStyle.value(VlStyle.TextColor, vlState))
        x: control.width - control.VlStyle.frame.borderWidth - 2 * width
        y: control.VlStyle.frame.borderWidth
        width: control.buttonSize
        height: control.buttonSize
        color: (control.VlStyle.revision, control.VlStyle.value(VlStyle.IndicatorColor, vlState))
        radius: (control.VlStyle.revision, control.VlStyle.value(VlStyle.IndicatorRadius, vlState))
        border.color: (control.VlStyle.revision, control.VlStyle.value(VlStyle.BorderColor, vlState))
        border.width: (control.VlStyle.revision, control.VlStyle.value(VlStyle.IndicatorBorderWidth, vlState))

        Rectangle {
            x: (parent.width - width) / 2
            y: (parent.height - height) / 2
            width: parent.width / 3
            height: Math.max(1, parent.width / 12)
            color: downButton.mark
        }
    }

    background: Rectangle {
        color: control.VlStyle.frame.color
        radius: control.VlStyle.frame.radius
        border.color: control.VlStyle.frame.borderColor
        border.width: control.VlStyle.frame.borderWidth
    }
}
