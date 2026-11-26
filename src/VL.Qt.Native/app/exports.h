// The conventions of the exports (vlqt.h, top comment): the exception guard, the thread check, the scene handle and the
// buffer answer. Shared by the export files (app/, window/, diagnostics/, resources/), offscreen/offscreen_exports.cpp,
// the backends that fill caller buffers and the test exports (test/test_*.cpp).
//
// Every export states its thread by the helper it runs its body in (vlqt.h, "Threads"):
//   guarded                 any thread
//   guardedGui              the Qt GUI thread (guardedGuiBuffer for a buffer export)
//   withScene               the Qt GUI thread, on a scene handle (a window or an offscreen scene)
//   withWindow              the same, on a window; an offscreen scene answers what the export documents
//   withOffscreen           the same, on an offscreen scene; a window is VLQT_ERR_NULL_HANDLE
#pragma once

#include "../vlqt.h"
#include "../window/scene_handle.h"
#include "last_error.h"

#include <cstring>
#include <exception>
#include <string>

namespace vlqt {

// Returns true if the calling thread is the Qt GUI thread (the thread that called vlqt_app_init), or if vlqt_app_init
// has not run yet: the thread is not known then, and no handle exists. app/app.cpp.
bool onGuiThread();

// VLQT_OK on the Qt GUI thread, else VLQT_ERR_WRONG_THREAD with the text "<function>: not on the Qt GUI thread ...".
inline int checkGuiThread(const char* function)
{
    if (onGuiThread())
        return VLQT_OK;
    return fail(VLQT_ERR_WRONG_THREAD, std::string(function) + ": not on the Qt GUI thread (the thread of vlqt_app_init)");
}

// VLQT_OK if handle is not null and the calling thread is the Qt GUI thread. The null check comes first, so a null
// handle is VLQT_ERR_NULL_HANDLE on any thread, also without Qt.
inline int checkScene(vlqt_scene* handle, const char* function)
{
    if (!handle)
        return fail(VLQT_ERR_NULL_HANDLE, std::string(function) + ": null handle");
    return checkGuiThread(function);
}

// Runs the body of an export. A negative result whose body set no text gets the generic text of its code (no
// allocation), so every failing call sets vlqt_last_error. An exception sets its text and returns VLQT_ERR_INTERNAL
// instead of crossing the C ABI.
template <class F>
int guarded(F&& f)
{
    const unsigned failures = failureCount();
    try {
        const int rc = f();
        if (rc < 0 && failureCount() == failures)
            setLastErrorStatic(resultText(rc));
        return rc;
    } catch (const std::exception& e) {
        setLastError(std::string("exception: ") + e.what());
    } catch (...) {
        setLastErrorStatic("unknown exception");
    }
    return VLQT_ERR_INTERNAL;
}

// guarded for an export of the Qt GUI thread: on another thread the body does not run.
template <class F>
int guardedGui(const char* function, F&& f)
{
    return guarded([&]() -> int {
        if (const int rc = checkGuiThread(function); rc != VLQT_OK)
            return rc;
        return f();
    });
}

// guarded for an export on a scene handle, Qt GUI thread: f(SceneHandle&) runs only for a handle on that thread.
template <class F>
int withScene(vlqt_scene* handle, const char* function, F&& f)
{
    return guarded([&]() -> int {
        if (const int rc = checkScene(handle, function); rc != VLQT_OK)
            return rc;
        return f(*asSceneHandle(handle));
    });
}

// withScene for a window-only export (vlqt_window_*): f runs for a window. An offscreen scene returns onScene, the
// answer the export documents for one: VLQT_OK or another value >= 0 for a no-op, VLQT_ERR_UNSUPPORTED with the text
// "<function>: not supported on an offscreen scene".
template <class F>
int withWindow(vlqt_scene* handle, const char* function, int onScene, F&& f)
{
    return withScene(handle, function, [&](SceneHandle& h) -> int {
        if (!h.isOffscreen())
            return f(h);
        if (onScene < 0)
            return fail(onScene, std::string(function) + ": not supported on an offscreen scene");
        return onScene;
    });
}

// withScene for an offscreen-only export (vlqt_offscreen_*): f(OffscreenScene&) runs for an offscreen scene; a window
// is VLQT_ERR_NULL_HANDLE with the text "<function>: not an offscreen scene".
template <class F>
int withOffscreen(vlqt_scene* handle, const char* function, F&& f)
{
    return withScene(handle, function, [&](SceneHandle& h) -> int {
        if (!h.isOffscreen())
            return fail(VLQT_ERR_NULL_HANDLE, std::string(function) + ": not an offscreen scene");
        return f(*h.offscreen);
    });
}

// First step of a buffer export (vlqt.h, "Buffers"): sets both counts to 0, so every return leaves them defined, and
// checks buf/cap. VLQT_OK or VLQT_ERR_BAD_ARGUMENT.
inline int beginBuffer(const void* buf, int cap, int* written, int* required)
{
    if (written)
        *written = 0;
    if (required)
        *required = 0;
    if (cap < 0 || (!buf && cap > 0))
        return fail(VLQT_ERR_BAD_ARGUMENT, "negative cap, or a null buffer with cap > 0");
    return VLQT_OK;
}

// guardedGui for a buffer export: beginBuffer before the thread check, so a wrong thread leaves both counts 0 too.
template <class F>
int guardedGuiBuffer(const char* function, const void* buf, int cap, int* written, int* required, F&& f)
{
    return guarded([&]() -> int {
        if (const int rc = beginBuffer(buf, cap, written, required); rc != VLQT_OK)
            return rc;
        if (const int rc = checkGuiThread(function); rc != VLQT_OK)
            return rc;
        return f();
    });
}

// Content of size bytes: VLQT_OK with *written = *required = size if it fits into cap (the caller copies it then), else
// VLQT_ERR_BUFFER_TOO_SMALL with *required = size and *written = 0.
inline int bufferFits(long long size, int cap, int* written, int* required)
{
    if (size > 0x7fffffff)
        return fail(VLQT_ERR_UNSUPPORTED, "content larger than 2^31 - 1 bytes");
    if (required)
        *required = int(size);
    if (size > cap)
        return VLQT_ERR_BUFFER_TOO_SMALL;
    if (written)
        *written = int(size);
    return VLQT_OK;
}

// bufferFits, then copies the size bytes of data into buf.
inline int copyOut(const void* data, int size, void* buf, int cap, int* written, int* required)
{
    const int rc = bufferFits(size, cap, written, required);
    if (rc == VLQT_OK && size > 0)
        std::memcpy(buf, data, size_t(size));
    return rc;
}

// copyOut of a text of length bytes plus its terminating NUL (counted).
inline int copyText(const char* text, int length, char* buf, int cap, int* written, int* required)
{
    const int rc = bufferFits((long long)length + 1, cap, written, required);
    if (rc == VLQT_OK) {
        if (length > 0)
            std::memcpy(buf, text, size_t(length));
        buf[length] = 0;
    }
    return rc;
}

} // namespace vlqt
