// VL.Qt base style: TextField. Frame = box, Text = text colour + font (selection = Track fill, placeholder =
// half-opaque text colour). "Pressed" state = editing (activeFocus). Values from the attached VlStyle (see Button.qml).
import QtQuick
import QtQuick.Controls.impl
import QtQuick.Controls.Basic.impl
import QtQuick.Templates as T
import VLQt.Style

T.TextField {
    id: control

    VlStyle.state: VlStyle.stateOf(control.enabled, control.activeFocus, control.hovered, false)

    implicitWidth: Math.max(VlStyle.frame.minWidth, implicitBackgroundWidth + leftInset + rightInset,
                            Math.max(contentWidth, placeholder.implicitWidth) + leftPadding + rightPadding)
    implicitHeight: Math.max(VlStyle.frame.minHeight, implicitBackgroundHeight + topInset + bottomInset,
                             contentHeight + topPadding + bottomPadding,
                             placeholder.implicitHeight + topPadding + bottomPadding)

    padding: 0
    leftPadding: VlStyle.frame.paddingX
    rightPadding: VlStyle.frame.paddingX
    topPadding: VlStyle.frame.paddingY
    bottomPadding: VlStyle.frame.paddingY
    font: VlStyle.text.font

    color: VlStyle.text.color
    selectionColor: VlStyle.track.fillColor
    selectedTextColor: VlStyle.text.color
    placeholderTextColor: Qt.rgba(VlStyle.text.color.r, VlStyle.text.color.g, VlStyle.text.color.b, VlStyle.text.color.a * 0.5)
    verticalAlignment: TextInput.AlignVCenter

    ContextMenu.menu: TextEditingContextMenu {
        editor: control
    }

    PlaceholderText {
        id: placeholder
        x: control.leftPadding
        y: control.topPadding
        width: control.width - (control.leftPadding + control.rightPadding)
        height: control.height - (control.topPadding + control.bottomPadding)

        text: control.placeholderText
        font: control.font
        color: control.placeholderTextColor
        verticalAlignment: control.verticalAlignment
        visible: !control.length && !control.preeditText && (!control.activeFocus || control.horizontalAlignment !== Qt.AlignHCenter)
        elide: Text.ElideRight
        renderType: control.renderType
    }

    background: Rectangle {
        color: control.VlStyle.frame.color
        radius: control.VlStyle.frame.radius
        border.color: control.VlStyle.frame.borderColor
        border.width: control.VlStyle.frame.borderWidth
    }
}
