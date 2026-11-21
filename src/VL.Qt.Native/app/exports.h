// The conventions of the exports (vlqt.h, top comment): the exception guard and the buffer answer. Shared by the export
// files (app/, window/, diagnostics/, resources/), offscreen.cpp, the backends that fill caller buffers and the test exports (test/test_*.cpp).
#pragma once

#include "../vlqt.h"
#include "last_error.h"

#include <cstring>
#include <exception>
#include <string>

namespace vlqt {

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
