import QtQuick

// Key sink of the SmokeTest scenario "keyshortcuts": a click gives it the focus, then it remembers the keys that
// reach the scene (the last key code and how many).
Item {
    id: root

    implicitWidth: 100
    implicitHeight: 100

    property int lastKey: 0
    property int count: 0

    focus: true

    Keys.onPressed: (event) => {
        lastKey = event.key
        count++
        event.accepted = true
    }

    MouseArea {
        anchors.fill: parent
        onPressed: root.forceActiveFocus()
    }
}
