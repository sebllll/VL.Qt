import QtQuick

// Test file of the SmokeTest scenario "qml": every value type of the protocol (and the QML types point and size, which are
// sent as VEC2) and a list of each in a property and in a second property bound to it (the binding result comes back as an event,
// so the value has made the whole trip patch -> QML -> patch). Lists in a var property and lists made by QML: QmlLists.qml.
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
    property point pt: Qt.point(0, 0)
    property point ptOut: pt
    property size sz: Qt.size(0, 0)
    property size szOut: sz

    property list<real> rl
    property list<real> rlOut: rl
    property list<double> dl
    property list<double> dlOut: dl
    property list<int> il
    property list<int> ilOut: il
    property list<bool> bl
    property list<bool> blOut: bl
    property list<string> sl
    property list<string> slOut: sl
    property list<color> cl
    property list<color> clOut: cl
    property list<point> ptl
    property list<point> ptlOut: ptl
    property list<size> szl
    property list<size> szlOut: szl
    property list<vector2d> v2l
    property list<vector2d> v2lOut: v2l
    property list<vector3d> v3l
    property list<vector3d> v3lOut: v3l
    property list<vector4d> v4l
    property list<vector4d> v4lOut: v4l
    property list<rect> boxl
    property list<rect> boxlOut: boxl

    // A signal argument of the type point arrives as a Vector2, like the property.
    signal located(point p)
    onPtChanged: located(pt)

    // A list argument: the declared element type decides what an empty list is (real -> floats, point -> Vector2, string -> strings).
    signal measured(list<real> values)
    onRlChanged: measured(rl)
    signal tagged(list<string> names)
    onSlChanged: tagged(sl)
    signal placed(list<point> points)
    onPtlChanged: placed(ptl)
}
