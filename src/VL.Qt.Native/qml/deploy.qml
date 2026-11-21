// Never loaded at runtime. windeployqt --qmldir scans this folder and deploys every QML module
// imported here (plus its DLLs/plugins). Keep the list in sync with the imports used by the
// element snippets in elements/type_*.cpp; only LGPL modules (see tools/check-licenses.ps1).
import QtQuick          // Item, Rectangle, Text, Image, Row, Column, Matrix4x4
import QtQuick.Effects  // MultiEffect (effect layer)
import QtQuick.Shapes   // Shape, ShapePath (SHAPE built in C++, elements/type_shape.cpp)
import QtMultimedia     // Video: MediaPlayer, AudioOutput, VideoOutput (FFmpeg backend)
import QtQuick.Controls // Button, SpinBox, CheckBox, Switch, Slider, TextField (the element snippets import QtQuick.Controls)
import QtQuick.Controls.Basic // fallback of the own style VLQt (picked in app/app.cpp); other styles are trimmed (trim-deploy/trim-linux)
Item {
    MultiEffect {}
}
