// TextField element (type_controls.cpp). Value input: vlqtUserCount / vlqtUserValue are the generic user-value channel
// (see "Value inputs" in type_controls.cpp). editingFinished with changed text only: vlqtCommitted is the text last sent.
import QtQuick
import QtQuick.Controls
TextField {
  property string vlqtCommitted: ""
  property int vlqtUserCount: 0
  property string vlqtUserValue: ""
  onEditingFinished: { if (text !== vlqtCommitted) { vlqtCommitted = text; vlqtUserValue = text; ++vlqtUserCount } }
}
