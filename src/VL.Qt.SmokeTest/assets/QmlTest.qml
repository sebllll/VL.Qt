import QtQuick

// Test file of the SmokeTest scenario "qml": a red rectangle with a white progress bar, a property in
// (fill, progress), properties QML changes itself (steps, clicks, shown with a Behavior animation) and two signals.
Item {
    id: root

    implicitWidth: 200
    implicitHeight: 100

    property color fill: "#ff0000"
    property real progress: 0
    // Follows progress through a Behavior: changes for 1 s (the frame clock of the test), the patch never sets it.
    property real shown: progress
    // Counts what happens to progress in QML: a change QML makes by itself while the patch sets progress.
    property int steps: 0
    property int clicks: 0

    signal clicked(int n)
    signal finished(string text, real value, bool flag, color tint)

    Behavior on shown { NumberAnimation { duration: 1000 } }

    onProgressChanged: {
        steps++
        if (progress >= 1)
            finished("done", progress, true, fill)
    }

    Rectangle {
        anchors.fill: parent
        color: root.fill
    }

    Rectangle {
        x: 0
        y: parent.height - 20
        width: parent.width * root.shown
        height: 20
        color: "white"
    }

    MouseArea {
        anchors.fill: parent
        onClicked: {
            root.clicks++
            root.clicked(root.clicks)
        }
    }
}
