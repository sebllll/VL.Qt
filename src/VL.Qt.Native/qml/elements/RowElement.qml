// Row element (type_layout.cpp). A Qt positioner: it only sets x of its children. Positioners have no cross-axis
// alignment: childAlignment (Qt::AlignmentFlag = C# VerticalAlignment) sets y of every child after each positioning
// pass (positioningComplete), on a height change of the row and on an alignment change. Reference extent = own height
// (implicit = content with AUTO_SIZE on) minus padding.
import QtQuick
Row {
  property int childAlignment: Qt.AlignTop
  function vlqtAlign() {
    const h = height - topPadding - bottomPadding;
    const f = childAlignment === Qt.AlignBottom ? 1 : childAlignment === Qt.AlignVCenter ? 0.5 : 0;
    for (let i = 0; i < children.length; ++i) { const c = children[i]; c.y = topPadding + (h - c.height) * f; }
  }
  onPositioningComplete: vlqtAlign()
  onHeightChanged: vlqtAlign()
  onChildAlignmentChanged: vlqtAlign()
}
