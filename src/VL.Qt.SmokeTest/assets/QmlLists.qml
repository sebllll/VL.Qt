import QtQuick

// Test file of the SmokeTest scenario "qml": lists in a var property (a JavaScript array in QML) and lists that QML makes itself,
// which come back to the patch as list values. Typed list properties: QmlTypes.qml.
Item {
    implicitWidth: 100
    implicitHeight: 50

    // The patch's list goes in as a JavaScript array of the elements and comes back out of the bound copy.
    property var v: []
    property var vOut: v

    // Lists made by QML: n changes them (a property of the patch), bigN makes a long one.
    property int n: 0
    property list<int> counted: { var a = []; for (var k = 0; k < n; ++k) a.push(k * 3); return a }
    property var halves: { var a = []; for (var k = 0; k < n; ++k) a.push(k + 0.5); return a }
    property var strings: { var a = []; for (var k = 0; k < n; ++k) a.push("s" + "ä€".repeat(k)); return a }
    property var mixedNumbers: n > 0 ? [1, 2.5, 3] : []
    property var mixedTypes: n > 0 ? [1, "a"] : []
    property int bigN: 0
    property var big: { var a = []; for (var k = 0; k < bigN; ++k) a.push(k); return a }

    // Lists made inside a signal; a list of mixed types is sent as a JSON string.
    signal made(var values)
    signal oddOne(var values)
    onNChanged: {
        made(n > 0 ? [n, n + 0.5] : [])
        oddOne(n > 0 ? [1, "a"] : [])
    }
}
