// Rectangle element (type_rectangle.cpp). border.width 0 matches the C# default StrokeWidth 0: Qt's default width is 1
// and draws a border as soon as border.color is set, so a lone SET border color must not show one.
import QtQuick
Rectangle { border.width: 0 }
