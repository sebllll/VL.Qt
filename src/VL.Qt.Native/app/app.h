// State of the application that other parts read: the QGuiApplication, QtNative's deployment folder and the pipeline
// cache file. Written by app/app.cpp (vlqt_app_init, vlqt_app_set_pipeline_cache); read on the Qt GUI thread.
#pragma once

#include <QtCore/QString>

class QGuiApplication;

namespace vlqt {

extern QGuiApplication* g_app;      // one app per process, never deleted; null until vlqt_app_init
extern QString g_nativeDir;         // QtNative's deployment folder (plugins/, qml/, fonts/), empty if unknown
extern QString g_pipelineCachePath; // vlqt_app_set_pipeline_cache, empty = Qt's pipeline cache off

} // namespace vlqt
