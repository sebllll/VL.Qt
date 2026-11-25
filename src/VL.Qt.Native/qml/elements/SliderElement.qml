// Slider element (type_controls.cpp). Value input: vlqtUserCount / vlqtUserValue are the generic user-value channel
// (see "Value inputs" in type_controls.cpp). moved = user only (press/drag, arrow keys, wheel; live, so every drag
// step), SET VALUE/MINIMUM/MAXIMUM never fire it (also not Qt's clamp). snapMode SnapAlways: with STEP > 0 a drag lands
// on from + k * step like keys and wheel; STEP 0 stays continuous. Wheel on (like SpinBox).
import QtQuick
import QtQuick.Controls
Slider {
  wheelEnabled: true
  snapMode: Slider.SnapAlways
  property int vlqtUserCount: 0
  property real vlqtUserValue: 0
  onMoved: { vlqtUserValue = value; ++vlqtUserCount }
}
