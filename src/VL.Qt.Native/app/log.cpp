#include "app/log.h"

#include <QtCore/QByteArray>
#include <QtCore/QString>
#include <QtCore/QtGlobal>

#include <atomic>
#include <cstdio>

namespace {

// Atomic: cut at unload (DllMain / ELF destructor), possibly on another thread than a logging one (exit() from a
// side thread, found by ThreadSanitizer); emitLog reads it once.
std::atomic<vlqt_log_fn> g_log{nullptr};

void messageHandler(QtMsgType type, const QMessageLogContext& ctx, const QString& msg)
{
    int level = 0;
    switch (type) {
    case QtDebugMsg:    level = 0; break;
    case QtInfoMsg:     level = 1; break;
    case QtWarningMsg:  level = 2; break;
    case QtCriticalMsg: level = 3; break;
    case QtFatalMsg:    level = 4; break;
    }
    vlqt::log::emitLog(level, ctx.category, msg.toUtf8());
}

} // namespace

namespace vlqt::log {

void attach(vlqt_log_fn log)
{
    g_log = log;
    qInstallMessageHandler(messageHandler);
}

// every message of QtNative and of Qt goes to the managed logger (vvvv Log window, logging providers).
// VLQT_LOG_STDERR (set before process start) additionally mirrors everything to stderr: a diagnostic for exports
// without a console logging provider, not a second log path of the library.
void emitLog(int level, const char* category, const QByteArray& msg)
{
    const char* cat = category && *category ? category : "default";
    if (const auto log = g_log.load())
        log(level, cat, msg.constData());
    static const bool toStderr = qEnvironmentVariableIsSet("VLQT_LOG_STDERR");
    if (toStderr || level >= 3) // Critical/Fatal always also on stderr (exports without a log provider)
        std::fprintf(stderr, "[%s] %s\n", cat, msg.constData());
}

void detach(bool libraryUnload)
{
    g_log = nullptr;
    if (libraryUnload)
        qInstallMessageHandler(nullptr);
}

} // namespace vlqt::log
