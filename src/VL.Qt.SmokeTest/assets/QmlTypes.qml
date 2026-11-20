import QtQuick

// Test file of the SmokeTest scenario "qml": every value type of the protocol in a property and in a second property
// bound to it (the binding result comes back as an event, so the value has made the whole trip patch -> QML -> patch).
Item {
    implicitWidth: 100
    implicitHeight: 50

    property real r: 0
    property real rOut: r
    property int i: 0
    property int iOut: i
    property bool b: false
    property bool bOut: b
    property string s: ""
    property string sOut: s
    property color c: "black"
    property color cOut: c
    property vector2d v2: Qt.vector2d(0, 0)
    property vector2d v2Out: v2
    property vector3d v3: Qt.vector3d(0, 0, 0)
    property vector3d v3Out: v3
    property vector4d v4: Qt.vector4d(0, 0, 0, 0)
    property vector4d v4Out: v4
    property rect box: Qt.rect(0, 0, 0, 0)
    property rect boxOut: box
}
