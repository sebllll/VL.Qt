// Never loaded at runtime. windeployqt --qmldir (and tools/deploy-linux.sh) scan this folder and deploy every QML module
// imported by a .qml file in it (plus its DLLs/plugins): the files of elements/, effects/, handlers/ and perf/ declare
// their own imports. This file only lists the modules that no file imports, because QtNative uses them from C++;
// only LGPL modules (see tools/check-licenses.ps1).
import QtQuick.Shapes   // Shape, ShapePath (SHAPE built in C++, elements/type_shape.cpp)
import QtQuick.Controls.Basic // fallback of the own style VLQt (picked in app/app.cpp); other styles are trimmed (trim-deploy/trim-linux)
