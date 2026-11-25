// Stage of a SHADER link (pass driver, protocol.h "Effect chain"): the stage covers its input plus the link's padding
// (pad: x left, y top, w right, h bottom; first link plus the Group frame, frameLeft/frameTop as for MULTI). The Binding
// points the sourceRect of the input's layer (tex: the element's layer source for the first link, else the previous
// stage) at that area, so the input arrives padded and transparent around it, 1:1 over UV 0..1 of the passes. Always
// through layer.sourceRect, never on the layer's ShaderEffectSource itself: QQuickItemLayer caches the value and skips an
// equal write, and a later MultiEffect on the same layer sets its padded rect there (else it kept our rect, hard edge).
// SINGLE: e1 draws. TWO_PASS: e1 hidden with its own layer (texture provider of e2), e2 draws. PYRAMID: e1 = composite
// draws, the pyramid passes are created by EffectChain::layoutPyramid under the stage, e2 is unused. active false: retired.
import QtQuick
import VLQt.EffectChain
Item {
  id: s
  property Item input: null
  property Item tex: null
  property bool first: true
  property bool active: true
  property bool twoPass: false
  property real frameLeft: 0
  property real frameTop: 0
  property rect pad: Qt.rect(0, 0, 0, 0)
  readonly property Item fx: e1
  readonly property Item fx2: e2
  readonly property real padL: pad.x + frameLeft
  readonly property real padT: pad.y + frameTop
  readonly property real inW: input ? input.width : 0
  readonly property real inH: input ? input.height : 0
  readonly property rect area: Qt.rect((first || !input ? 0 : input.x) - padL, (first || !input ? 0 : input.y) - padT,
    inW + padL + pad.width, inH + padT + pad.height)
  x: area.x; y: area.y; width: area.width; height: area.height
  Binding {
    target: !s.tex ? null : s.first ? s.tex.sourceItem.layer : s.tex.layer; property: "sourceRect"; when: s.active && s.tex !== null
    restoreMode: Binding.RestoreNone
    value: Qt.rect(-s.padL, -s.padT, s.area.width, s.area.height)
  }
  VlqtShaderPass { id: e1; width: s.width; height: s.height; visible: !s.twoPass; layer.enabled: s.twoPass }
  VlqtShaderPass { id: e2; width: s.width; height: s.height; visible: s.twoPass }
}
