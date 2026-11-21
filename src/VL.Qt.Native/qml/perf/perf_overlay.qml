// Perf overlay (diagnostics/perf_monitor.cpp): the meter (flags & 1), the F5 pages panel (flags & 2) and the timeline,
// display only. The text and the bars are set from C++ (PerfMonitor); PerfGraph/PerfTimeline are C++ items (VLQt.Perf, perf_items.h).
import QtQuick
import VLQt.Perf
Item {
  id: root
  property int flags: 0
  property string meterText: ""
  property string statsText: ""
  property string bodyText: ""
  property string footText: ""
  property int page: 0
  property real barUse: 0
  property real barP95: 0
  property color barColor: "#50dc78"
  property int rendering: 0
  property bool layered: true
  enabled: false // display only, never takes input
  Item {
    id: meter; visible: root.flags & 1
    x: 8; y: 8; width: 340; height: 72
    Rectangle { // background, text, badge: own texture like the F5 panel, re-rendered only when the text changes
      anchors.fill: parent; radius: 3; color: "#c0000000"
      layer.enabled: root.layered
      Text { x: 6; y: 3; text: root.meterText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
      Rectangle { // 1 = GPU (hardware), 2 = CPU (software rendering); 0 = not known yet
        visible: root.rendering > 0
        anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 4
        width: badge.implicitWidth + 6; height: badge.implicitHeight + 2
        color: root.rendering === 2 ? "#e03030" : "#30a050"
        Text { id: badge; anchors.centerIn: parent; text: root.rendering === 2 ? "CPU" : "GPU"
               color: "white"; font.family: "Consolas"; font.pixelSize: 9; font.bold: true }
      }
    }
    PerfGraph { objectName: "graph"; anchors.fill: parent; anchors.margins: 4; anchors.topMargin: 18 }
  }
  Rectangle { // F5 pages: 0 Summary, 1 Timeline, 2 Scene, 3 System, 4 Resources
    id: panel
    visible: root.flags & 2
    x: 8; y: (root.flags & 1) ? meter.y + meter.height + 6 : 8
    width: col.width + 12; height: col.height + 8; radius: 3; color: "#d0000000"
    // Own texture, re-rendered only when the text changes (4 Hz): the scene's per-frame batch updates then see one
    // quad instead of the glyphs. Off on the Qt Quick software backend (layered = false): there the layer draws
    // its content shifted up plus an opaque white area.
    layer.enabled: root.layered
    Column {
      id: col; x: 6; y: 4; spacing: 4
      Text { text: root.statsText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
      Item { // Summary: pump avg against the budget, white tick = p95
        visible: root.page === 0
        width: 300; height: 6
        Rectangle { anchors.fill: parent; color: "#40ffffff" }
        Rectangle { width: Math.min(root.barUse, 1) * parent.width; height: parent.height; color: root.barColor }
        Rectangle { visible: root.barP95 > 0; x: Math.min(root.barP95, 1) * (parent.width - 2); width: 2; height: parent.height; color: "white" }
      }
      Text { visible: text !== ""; text: root.bodyText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
      Item { id: slot; visible: root.page === 1; width: 480; height: 140 } // the timeline's place
      Text { visible: root.page === 1 && text !== ""; text: root.footText; textFormat: Text.StyledText; color: "white"; font.family: "Consolas"; font.pixelSize: 11 }
    }
  }
  // Outside the panel layer: a timeline update only rewrites its vertex buffers, no layer render pass and no text
  // re-render.
  PerfTimeline {
    objectName: "timeline"; visible: panel.visible && root.page === 1
    x: panel.x + col.x + slot.x; y: panel.y + col.y + slot.y; width: slot.width; height: slot.height
  }
}
