// Example file for the QML node factory (help patch "HowTo Create QML Nodes from Files").
// This file in the qml/ folder next to the patch gives the node Gauge: one pin per declaration
// of the root object, the // lines directly above a declaration are the help of its pins.
//
// @summary A bar gauge with a label and a percent readout; the bar glides to its value. Fires Milestone at 25, 50, 75 and 100 percent and Full when it reaches 100.
// @tags meter, bar, progress
import QtQuick

Item {
    id: root

    // Value of the bar, 0 to 1; values outside are clamped.
    property real value: 0.5
    // Colour of the bar.
    property color accent: "#ff7a3d"
    // Text above the bar.
    property string label: "Load"

    // The percent the bar shows right now. It changes while the bar glides.
    readonly property int percent: Math.round(bar.shown * 100)

    // The percent the bar passed: 25, 50, 75 or 100, in either direction.
    signal milestone(int percent)
    // The bar reached 100 percent. Fires only when the animated bar really gets there: a Value that
    // falls again before the bar has caught up (like the LFO ramp in the help patch) never fires it.
    signal full

    // Auto Size takes these. Switch Auto Size off and Size sets width and height instead.
    implicitWidth: 240
    implicitHeight: 56

    onPercentChanged: {
        if (percent % 25 === 0)
            root.milestone(percent)
        if (percent === 100)
            root.full()
    }

    Text {
        anchors.left: parent.left
        anchors.top: parent.top
        text: root.label
        color: "#9aa3b5"
        font.pixelSize: 14
    }

    Text {
        anchors.right: parent.right
        anchors.top: parent.top
        text: root.percent + " %"
        color: "#eef1f7"
        font.pixelSize: 14
        font.bold: true
    }

    // The track and the bar. Animations follow the frame clock of the renderer.
    Rectangle {
        id: bar
        property real shown: Math.max(0, Math.min(1, root.value))

        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 22
        radius: height / 2
        color: "#2b2f3a"

        Behavior on shown {
            NumberAnimation { duration: 400; easing.type: Easing.OutCubic }
        }

        Rectangle {
            width: Math.max(parent.height, parent.width * bar.shown)
            height: parent.height
            radius: height / 2
            color: root.accent
        }
    }
}
