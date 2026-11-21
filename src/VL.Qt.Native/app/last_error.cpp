#include "last_error.h"

#include "../vlqt.h"

#include <QtCore/QString>

namespace vlqt {

namespace {

// Per thread (CAPI-01): exports documented for any thread (vlqt_shader_reflect, vlqt_audio_tap_read,
// vlqt_image_write_file, a vlqt_font_add on the wrong thread) fail without racing the main thread's text.
thread_local std::string t_text;
thread_local const char* t_static = nullptr; // set: t_text is unused
thread_local unsigned t_failures = 0;

} // namespace

void setLastError(const char* message)
{
    t_text = message ? message : "";
    t_static = nullptr;
    ++t_failures;
}

void setLastError(std::string message)
{
    t_text = std::move(message);
    t_static = nullptr;
    ++t_failures;
}

void setLastError(const QString& message)
{
    setLastError(message.toStdString());
}

void setLastErrorStatic(const char* message)
{
    t_static = message ? message : "";
    ++t_failures;
}

const char* lastError()
{
    return t_static ? t_static : t_text.c_str();
}

unsigned failureCount()
{
    return t_failures;
}

int fail(int code, const QString& message)
{
    setLastError(message);
    return code;
}

const char* resultText(int code)
{
    switch (code) {
    case VLQT_ERR_NULL_HANDLE: return "null handle, or a handle of the wrong kind";
    case VLQT_ERR_BAD_LENGTH: return "bad length or alignment";
    case VLQT_ERR_UNKNOWN_OP: return "unknown op";
    case VLQT_ERR_UNKNOWN_OBJECT: return "unknown object id";
    case VLQT_ERR_UNKNOWN_TYPE: return "unknown element type";
    case VLQT_ERR_UNKNOWN_PROPERTY: return "unknown property";
    case VLQT_ERR_BAD_ARGUMENT: return "bad argument (details in the log)";
    case VLQT_ERR_INVALID: return "invalid command";
    case VLQT_ERR_UNKNOWN_ID: return "unknown, stale or released id (details in the log)";
    case VLQT_ERR_NO_DATA: return "no data yet";
    case VLQT_ERR_WRONG_THREAD: return "called on the wrong thread";
    case VLQT_ERR_BUFFER_TOO_SMALL: return "buffer too small: *required holds the size";
    case VLQT_ERR_UNSUPPORTED: return "not supported";
    case VLQT_ERR_IO: return "file could not be read or written";
    case VLQT_ERR_NOT_INITIALIZED: return "app not initialized (vlqt_app_init)";
    case VLQT_ERR_UNBOUND: return "the offscreen scene is not bound";
    case VLQT_ERR_ADAPTER_MISMATCH: return "the foreign device is on another adapter";
    case VLQT_ERR_OPEN_FAILED: return "the shared texture could not be opened";
    case VLQT_ERR_FORMAT_UNSUPPORTED: return "texture format not supported";
    case VLQT_ERR_FENCE_FAILED: return "fence creation or sharing failed";
    case VLQT_ERR_DEVICE_LOST: return "QtNative's device is lost";
    case VLQT_ERR_INTERNAL: return "internal error (details in the log)";
    default: return code < 0 ? "error" : "";
    }
}

} // namespace vlqt

extern "C" {

VLQT_API const char* vlqt_last_error(void)
{
    return vlqt::lastError();
}

} // extern "C"
