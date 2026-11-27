import QtQuick

// Test file of the SmokeTest scenario "qml-input" (HandlerTest.UserQml.cs): pointer and keyboard input inside user QML.
// Left third: a press gives the key sink the focus, which counts every key. Middle third: a MouseArea that counts
// clicks and takes no focus. Right third: no input of its own, a press reaches the element's handlers or what lies below.
Item {
    id: root

    implicitWidth: 300
    implicitHeight: 100

    property int clicks: 0
    property bool pressed: clickArea.pressed
    property int keys: 0
    property int lastKey: 0

    Item {
        id: sink

        width: 100
        height: 100

        Keys.onPressed: (event) => {
            root.lastKey = event.key
            root.keys++
            event.accepted = true
        }

        MouseArea {
            anchors.fill: parent
            onPressed: sink.forceActiveFocus()
        }
    }

    MouseArea {
        id: clickArea

        x: 100
        width: 100
        height: 100
        onClicked: root.clicks++
    }
}
