// Never loaded at runtime. windeployqt --qmldir scans this folder and deploys every QML module
// imported here (plus its DLLs/plugins). Keep the list in sync with the imports used by the
// element snippets in elements.cpp; only LGPL modules (see tools/check-licenses.ps1).
import QtQuick          // Item, Rectangle, Text, Image, Row, Column, Matrix4x4
import QtQuick.Effects  // MultiEffect (effect layer)
import QtMultimedia     // Video: MediaPlayer, AudioOutput, VideoOutput (FFmpeg backend)
import QtQuick.Controls // Button, SpinBox (element snippets import QtQuick.Controls); CheckBox/Switch/Slider/TextField (#301) need nothing more
import QtQuick.Controls.Basic // style Basic (set in vlqt.cpp); other styles are trimmed (trim-deploy/trim-linux)
Item {
    MultiEffect {}
}
