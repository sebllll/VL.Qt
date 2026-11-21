// Text element (type_text.cpp). textFormat PlainText: the C# element is plain text, Qt's default AutoText would
// interpret markup. minimumPixelSize 1 so fontSizeMode Fit can shrink freely; lineHeight is proportional.
import QtQuick
Text { textFormat: Text.PlainText; minimumPixelSize: 1; lineHeightMode: Text.ProportionalHeight }
