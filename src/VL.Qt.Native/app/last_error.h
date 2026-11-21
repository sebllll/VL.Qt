// The error text of vlqt_last_error (vlqt.h, top comment): one per thread, set on every failing call of an export.
#pragma once

#include <string>
#include <utility>

class QString;

namespace vlqt {

// Sets the calling thread's text (a copy).
void setLastError(const char* message);
void setLastError(std::string message);
void setLastError(const QString& message);
// Sets a text that lives as long as the process (a literal) without copying it: no allocation, so also on the audio
// thread of vlqt_res_read_audio.
void setLastErrorStatic(const char* message);
// The calling thread's text, empty if none; valid until the next setLastError* on the same thread.
const char* lastError();
// Number of texts set on the calling thread so far: the guard (exports.h) sees whether a body set one.
unsigned failureCount();
// The generic text of a vlqt_result code (a literal); "" for a code >= 0.
const char* resultText(int code);

// Sets the calling thread's text and returns code, a negative vlqt_result: return fail(VLQT_ERR_NULL_HANDLE, "...").
inline int fail(int code, const char* message)
{
    setLastError(message);
    return code;
}
inline int fail(int code, std::string message)
{
    setLastError(std::move(message));
    return code;
}
int fail(int code, const QString& message);

} // namespace vlqt
