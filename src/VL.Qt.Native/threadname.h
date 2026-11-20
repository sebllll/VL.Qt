#pragma once

// Names for the threads of QtNative, so profiles and dumps show them. The same names on every platform, at most
// 15 characters (the limit of pthread_setname_np on Linux): "vlqt-gui", "vlqt-res-N".

#include <QtCore/QtGlobal>

#include <cstdio>
#include <cstring>

#ifdef Q_OS_WIN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif
#ifdef Q_OS_LINUX
#include <pthread.h>
#endif

namespace vlqt {

// Names the calling thread. Best effort: a failure is ignored. Not on other platforms.
inline void setCurrentThreadName(const char* name)
{
#ifdef Q_OS_WIN
    wchar_t wide[16] = {};
    for (int i = 0; i < 15 && name[i]; i++)
        wide[i] = wchar_t(name[i]); // ASCII names only
    SetThreadDescription(GetCurrentThread(), wide);
#elif defined(Q_OS_LINUX)
    pthread_setname_np(pthread_self(), name);
#else
    (void)name;
#endif
}

// Name for the Qt GUI thread. It is the thread of the host (the vvvv main thread, the SmokeTest main thread), not one
// of ours: named only while it has no name of its own, so a name the host gave it stays.
inline void nameGuiThreadIfUnnamed(const char* name)
{
#ifdef Q_OS_WIN
    PWSTR existing = nullptr;
    if (SUCCEEDED(GetThreadDescription(GetCurrentThread(), &existing))) {
        const bool unnamed = !existing || !existing[0];
        if (existing)
            LocalFree(existing);
        if (!unnamed)
            return;
    }
    setCurrentThreadName(name);
#elif defined(Q_OS_LINUX)
    // An unnamed thread carries the name of the executable (/proc/self/comm is the one of the main thread).
    char own[16] = {};
    char process[16] = {};
    if (pthread_getname_np(pthread_self(), own, sizeof own) == 0) {
        if (std::FILE* f = std::fopen("/proc/self/comm", "r")) {
            if (std::fgets(process, sizeof process, f)) {
                for (char* p = process; *p; p++)
                    if (*p == '\n')
                        *p = 0;
            }
            std::fclose(f);
        }
        if (process[0] && std::strcmp(own, process) != 0)
            return; // someone named it
    }
    setCurrentThreadName(name);
#else
    (void)name;
#endif
}

} // namespace vlqt
