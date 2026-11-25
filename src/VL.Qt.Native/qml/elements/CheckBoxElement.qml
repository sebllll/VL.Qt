// CheckBox element (type_controls.cpp), one of the two toggles; SwitchElement.qml is the same but for the type.
// No text (labels are separate Text elements): spacing 0, so the implicit size is the indicator + style padding.
// toggled = user only (click, Space); SET VALUE (checked) never fires it. Tristate is not exposed.
import QtQuick
import QtQuick.Controls
CheckBox {
  spacing: 0
  property int vlqtUserCount: 0
  property bool vlqtUserValue: false
  onToggled: { vlqtUserValue = checked; ++vlqtUserCount }
}
