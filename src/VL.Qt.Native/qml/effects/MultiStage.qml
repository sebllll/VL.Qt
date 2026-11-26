// Stage of a MULTI link: fx (MultiEffect) has the size of the input and lies where the input lies (input = the root
// for the first link, whose layer texture covers the element's (0, 0, w, h); otherwise the previous stage, whose
// x/y/size are its effect area in root coordinates); the stage covers fx.itemRect (input + padding), its area in root
// = element coordinates. frameLeft/frameTop (first link only): how far an auto-sized Group's frame reaches left of /
// above its origin; Qt's layer covers (0, 0, width, height) of the item, so that part goes into the left/top padding
// (added to the automatic and the manual padding): the effect area includes the whole frame, unclipped. Mask: the
// Binding points the frame's layer.sourceRect at the effect area of this link (protocol.h mask rule), maskEnabled
// follows the frame; MultiEffect keeps a raw pointer to maskSource, so the frame is detached (null) before it goes.
// The shader-switching values (blurEnabled, shadowEnabled, blurMax) are written by EffectChain::writeLink on change only.
// fx stays at (0, 0) of an inner Item that is moved instead: MultiEffect reports itemRect again on every geometry change
// of its own, so a position bound to it would be a binding loop.
import QtQuick
import QtQuick.Effects
Item {
  id: s
  property Item input: null
  property bool first: true
  property Item maskFrame: null
  property real frameLeft: 0
  property real frameTop: 0
  property rect pad: Qt.rect(0, 0, 0, 0)
  readonly property Item fx: e
  readonly property real inX: first || !input ? 0 : input.x
  readonly property real inY: first || !input ? 0 : input.y
  readonly property rect area: Qt.rect(inX + e.itemRect.x, inY + e.itemRect.y, e.itemRect.width, e.itemRect.height)
  x: area.x; y: area.y; width: area.width; height: area.height
  Item {
    x: s.inX - s.area.x; y: s.inY - s.area.y
    MultiEffect {
      id: e
      width: s.input ? s.input.width : 0; height: s.input ? s.input.height : 0
      paddingRect: Qt.rect(s.pad.x + s.frameLeft, s.pad.y + s.frameTop, s.pad.width, s.pad.height)
      maskEnabled: s.maskFrame !== null; maskSource: s.maskFrame
      Binding {
        target: s.maskFrame ? s.maskFrame.layer : null; property: "sourceRect"; when: s.maskFrame !== null
        restoreMode: Binding.RestoreNone
        value: s.area
      }
    }
  }
}
