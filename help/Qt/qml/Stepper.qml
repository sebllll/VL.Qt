// Example file for the QML node factory (help patch "HowTo Interact with QML"): pointer and keyboard input
// inside a file. A MouseArea sets the value from the mouse (click or drag) and gives the bar the keyboard focus; Keys
// change the value with the arrow keys, Enter commits and gives the focus back. The value and a signal go back to
// the patch through the pins.
//
// @summary A bar you set with the mouse (click or drag) or the Left and Right keys. A click gives it the focus, a frame shows it. Enter commits and releases the focus; the mouse release commits too.
// @tags input, pointer, mouse, keyboard, focus, slider
import QtQuick

Item {
    id: root

    // Value of the bar, 0 to 1. Input and output: the patch can set it, the mouse and the keys change it, and the
    // output shows the last value QML set. The input sends only when it changes, so it does not fight the mouse.
    property real value: 0.5
    // How much one press of Left or Right changes Value.
    property real step: 0.1

    // Fires on Enter and when the mouse button is released after a click or a drag. The argument is Value.
    signal committed(real value)

    // Auto Size takes these. Switch Auto Size off and Size sets width and height instead.
    implicitWidth: 240
    implicitHeight: 56

    // The bar. It is the item that takes the keyboard focus, so the Keys handlers sit here.
    Item {
        id: bar

        anchors.fill: parent

        // Left and Right change Value, Enter and Return commit it and give the focus back: with focus off no key
        // reaches the bar and the frame goes, like after a click outside. Other keys go on to the parents.
        Keys.onLeftPressed: root.value = Math.max(0, root.value - root.step)
        Keys.onRightPressed: root.value = Math.min(1, root.value + root.step)
        Keys.onReturnPressed: commit()
        Keys.onEnterPressed: commit()

        function commit() {
            root.committed(root.value)
            bar.focus = false
        }

        Rectangle {
            anchors.fill: parent
            radius: 10
            color: "#2b2f3a"
        }

        // The filled part of the bar.
        Rectangle {
            width: parent.width * root.value
            height: parent.height
            radius: 10
            color: "#ff7a3d"
        }

        Text {
            anchors.centerIn: parent
            text: root.value.toFixed(2)
            color: "#eef1f7"
            font.pixelSize: 18
            font.bold: true
        }

        // The focus frame: visible only while the bar has the keyboard focus.
        Rectangle {
            anchors.fill: parent
            radius: 10
            color: "transparent"
            border.width: 3
            border.color: "#ffffff"
            visible: bar.activeFocus
        }

        // A press gives the bar the focus and sets the value; while the button is down the mouse is grabbed,
        // so dragging outside the bar still works (the position is clamped).
        MouseArea {
            anchors.fill: parent

            onPressed: (mouse) => {
                bar.forceActiveFocus()
                root.value = Math.max(0, Math.min(1, mouse.x / width))
            }
            onPositionChanged: (mouse) => root.value = Math.max(0, Math.min(1, mouse.x / width))
            onReleased: root.committed(root.value)
        }
    }
}
