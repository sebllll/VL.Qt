// Switch element (type_controls.cpp), one of the two toggles; CheckBoxElement.qml is the same but for the type.
// No text (labels are separate Text elements): spacing 0, so the implicit size is the indicator + style padding (Switch
// lane 2 x size). toggled = user only (click, Space); SET VALUE (checked) never fires it. Dragging the handle is not
// exposed; a drag that flips the Switch also sends toggled.
import QtQuick
import QtQuick.Controls
Switch {
  spacing: 0
  property int vlqtUserCount: 0
  property bool vlqtUserValue: false
  onToggled: { vlqtUserValue = checked; ++vlqtUserCount }
}
