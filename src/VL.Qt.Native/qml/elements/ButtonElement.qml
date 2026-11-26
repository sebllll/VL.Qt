// Button element (type_controls.cpp). Base style VLQt (style/VLQt/Button.qml), values from SET_STYLE (vlstyle.h).
// Clicks are Qt's clicked signal (release over the button; autoRepeat repeats it while held; disabled = none), counted
// in vlqtClicks and sent as EVENT_CLICK by the poll for every bound Button (Elements::takeClicks).
import QtQuick
import QtQuick.Controls
Button {
  property int vlqtClicks: 0
  onClicked: ++vlqtClicks
}
