// Column element (type_layout.cpp). A Qt positioner: it only sets y of its children. Positioners have no cross-axis
// alignment: childAlignment (Qt::AlignmentFlag = C# HorizontalAlignment) sets x of every child after each positioning
// pass (positioningComplete), on a width change of the column and on an alignment change. Reference extent = own width
// (implicit = content with AUTO_SIZE on) minus padding.
import QtQuick
Column {
  property int childAlignment: Qt.AlignLeft
  function vlqtAlign() {
    const w = width - leftPadding - rightPadding;
    const f = childAlignment === Qt.AlignRight ? 1 : childAlignment === Qt.AlignHCenter ? 0.5 : 0;
    for (let i = 0; i < children.length; ++i) { const c = children[i]; c.x = leftPadding + (w - c.width) * f; }
  }
  onPositioningComplete: vlqtAlign()
  onWidthChanged: vlqtAlign()
  onChildAlignmentChanged: vlqtAlign()
}
