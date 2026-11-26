// Logging: every message of QtNative and of Qt goes to the log callback of vlqt_app_init (the managed logger: vvvv Log
// window, logging providers). emitLog runs on any thread (Qt messages of render threads, resource workers, Qt's own).
#pragma once

#include "vlqt.h"

class QByteArray;

namespace vlqt::log {

// Sets the callback (vlqt_app_init, never changed afterwards) and installs the Qt message handler that feeds it.
void attach(vlqt_log_fn log);
// Levels: 0 debug, 1 info, 2 warning, 3 critical, 4 fatal (C# maps them to LogLevel); the category is passed verbatim.
void emitLog(int level, const char* category, const QByteArray& msg);
// Cuts the callback at unload (DllMain / ELF destructor, platform/platform_*.cpp). libraryUnload: the library is being
// unmapped (FreeLibrary, dlclose), so Qt's default message handler replaces ours too.
void detach(bool libraryUnload);

} // namespace vlqt::log
