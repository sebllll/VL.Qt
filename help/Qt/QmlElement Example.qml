import QtQuick

// Example file for the QmlElement node (help patch "HowTo Use a QML file").
// A small gauge: a label, a percent readout and a bar that glides to its value.
//
// Properties declared in the root (property ...) can be set from the patch through the
// Properties pin and come back through the Properties output when QML changes them.
// Signals declared in the root (signal ...) come back through the Signals output.
Item {
    id: root

    // In: set from the patch. 0..1, values outside are clamped.
    property real value: 0.5
    // In: colour of the bar.
    property color accent: "#ff7a3d"
    // In: text above the bar.
    property string label: "Load"

    // Out: the percent the bar shows right now. It changes while the bar glides (an
    // animation), so the patch gets it back as a QML-side change.
    property int percent: Math.round(bar.shown * 100)

    // Out: emitted whenever the bar passes 25, 50, 75 or 100 percent, in either direction.
    signal milestone(int percent)

    // Auto Size takes these. Switch Auto Size off and Size sets width and height instead.
    implicitWidth: 240
    implicitHeight: 56

    onPercentChanged: {
        if (percent % 25 === 0)
            root.milestone(percent)
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

    // The track and the bar. `shown` is the clamped value; the Behavior makes it glide.
    // Animations follow the frame clock of the renderer, so this is deterministic
    // (a QML Timer would run on wall time instead).
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
