// VL.Qt base style: Button. Every value comes from the attached VlStyle (written by SET_STYLE from C#, which
// holds the defaults); unset values are 0 / transparent / Qt font. Sub-items read control.VlStyle: a bare VlStyle
// there would attach a second, empty object to the sub-item.
import QtQuick
import QtQuick.Templates as T
import VLQt.Style

T.Button {
    id: control

    VlStyle.state: VlStyle.stateOf(control.enabled, control.down, control.hovered, control.checked)

    // AUTO_SIZE: content + padding, at least the frame's minimum size.
    implicitWidth: Math.max(VlStyle.frame.minWidth, implicitBackgroundWidth + leftInset + rightInset,
                            implicitContentWidth + leftPadding + rightPadding)
    implicitHeight: Math.max(VlStyle.frame.minHeight, implicitBackgroundHeight + topInset + bottomInset,
                             implicitContentHeight + topPadding + bottomPadding)

    padding: 0
    leftPadding: VlStyle.frame.paddingX
    rightPadding: VlStyle.frame.paddingX
    topPadding: VlStyle.frame.paddingY
    bottomPadding: VlStyle.frame.paddingY
    spacing: 0
    font: VlStyle.text.font

    contentItem: Text {
        text: control.text
        font: control.font
        color: control.VlStyle.text.color
        horizontalAlignment: Text.AlignHCenter
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
