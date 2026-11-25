// ReleaseWithinBounds: exclusive grab on press (button semantics, the topmost tap handler wins), tap when released
// inside even after moving out and back.
import QtQuick
TapHandler { gesturePolicy: TapHandler.ReleaseWithinBounds }
