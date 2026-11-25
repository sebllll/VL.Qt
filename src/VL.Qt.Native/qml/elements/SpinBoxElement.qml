// SpinBox element (type_controls.cpp). Value input: vlqtUserCount / vlqtUserValue are the generic user-value channel
// (see "Value inputs" in type_controls.cpp), set in the handler of the control's user-only signal (valueModified).
// Full int range instead of Qt's 0..99, editable and wheel on; text <-> value locale-free (plain digits, optional '-',
// no group separators, like vvvv's IOBoxes), the validator too (locale C). Qt commits a typed value on Enter or focus
// loss (valueModified); +/-, arrow keys and wheel commit at once.
import QtQuick
import QtQuick.Controls
SpinBox {
  from: -2147483648
  to: 2147483647
  editable: true
  wheelEnabled: true
  locale: Qt.locale("C")
  textFromValue: function(v, l) { return v.toString() }
  valueFromText: function(t, l) { var v = parseInt(t, 10); return isNaN(v) ? value : v }
  property int vlqtUserCount: 0
  property int vlqtUserValue: 0
  onValueModified: { vlqtUserValue = value; ++vlqtUserCount }
}
