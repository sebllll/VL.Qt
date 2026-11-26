/* resource_kind.h - the interface of a resource kind and the types that the manager, the kinds and their users share.
 *
 * A Kind (image, snapshot, shader, audio tap) is stateless and registered once in resource_registry.cpp; the data of
 * one resource lives in its Content. Design notes: docs/design/resources.md.
 */
#pragma once

#include "vlqt.h"

#include <QtCore/QByteArray>
#include <QtCore/QLoggingCategory>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <string>

namespace vlqt::res {

// The log category of the whole resource layer ("VL.Qt.Resources"), defined in resources.cpp.
Q_DECLARE_LOGGING_CATEGORY(lcRes)

// An id is index (low 32 bits) | generation (high 32 bits); see docs/design/resources.md.
inline uint32_t indexOf(uint64_t id) { return uint32_t(id & 0xffffffffu); }
inline uint32_t generationOf(uint64_t id) { return uint32_t(id >> 32); }
inline uint64_t makeId(uint32_t index, uint32_t generation) { return (uint64_t(generation) << 32) | index; }

// "#index.generation" for log lines.
inline QByteArray idText(uint64_t id)
{
    return '#' + QByteArray::number(indexOf(id)) + '.' + QByteArray::number(generationOf(id));
}

// Milliseconds of the steady clock, for the time a resource spent in a state.
inline int64_t steadyMs()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// The values are the protocol values of vlqt_resource_state (protocol.h) that C# sees. Decoding means "the content is
// being made" for every kind: a decode on a worker, a snapshot grab in flight.
enum class State : uint8_t
{
    Pending = 1,
    Decoding = 2,
    Uploading = 3,
    Ready = 4,
    Failed = 5,
    Released = 6
};

const char* stateName(State s);

// Data of one resource shared between the main thread and a worker. A worker gets it only while the resource is in
// Decoding and touches nothing else; the main thread reads the decode result after the worker handed it back.
struct Content
{
    virtual ~Content() = default;
    std::atomic<bool> cancel{false}; // set on release / shutdown; decode should check it and return early
};

struct Info
{
    uint32_t width = 0, height = 0, format = 0;
};

// What one Kind::upload call left behind.
enum class UploadStatus
{
    Progress, // uploaded a part, the budget is used up: the rest in the next frame
    Waiting,  // could do nothing now (a copy task runs, the device is removed): other uploads go first
    Done      // the upload is complete
};

// One resource kind (VLQT_RESOURCE_KIND_*). Stateless, registered once; the per resource data lives in its Content.
class Kind
{
public:
    virtual ~Kind() = default;
    virtual const char* name() const = 0;
    // Worker thread, pure data only. Returns an empty string on success, else the error text (cancel: any text,
    // the result is dropped anyway).
    virtual std::string decode(Content& work) = 0;
    // Main thread: uploads at most budget bytes (budget < 0 = no limit, vlqt_res_wait). Returns the bytes used and sets
    // status (UploadStatus), error on failure. May be called several times (stripes). A removed device is no error:
    // return 0 with Waiting, the rebuild (device lost) restarts the upload.
    virtual int64_t upload(Content& work, int64_t budget, UploadStatus& status, std::string& error) = 0;
    // Main thread: frees everything of the resource (GPU objects too). Called once, also after Failed.
    virtual void destroy(Content& work) { (void)work; }
    // Main thread, before Ready: values of the INFO event.
    virtual Info info(const Content& work) const { (void)work; return {}; }
    // Main thread, after a successful decode: a warning for the host (WARNING record), empty = none.
    virtual std::string takeWarning(Content& work) { (void)work; return {}; }
    // Main thread, while Uploading: bytes still to upload (F5 Resources page; 0 = unknown).
    virtual int64_t pendingBytes(const Content& work) const { (void)work; return 0; }

    // --- Device lost (docs/design/resources.md), main thread ---
    // Frees every object of the old device, keeps the source. Called on every Content no worker holds; destroy may follow.
    virtual void releaseDevice(Content& work) { (void)work; }
    // A new Content that makes the same content again on the current device (decode + upload) from the source of work,
    // nullptr = nothing to restore (no GPU content).
    virtual std::shared_ptr<Content> restore(const Content& work) { (void)work; return nullptr; }
    // The Content was started for an older device: its decode result must not be uploaded.
    virtual bool stale(const Content& work) const { (void)work; return false; }
};

// The kind of a VLQT_RESOURCE_KIND_* value, nullptr if unknown (resource_registry.cpp).
Kind* kindOf(int kind);

// Return codes of the C API: short names for the codes of vlqt_result (vlqt.h) that the resources return.
constexpr int okRc = VLQT_OK;
constexpr int errorRc = VLQT_ERR_INTERNAL;      // internal error, details in the log
constexpr int badValueRc = VLQT_ERR_BAD_ARGUMENT; // argument out of range / transition rejected
constexpr int staleIdRc = VLQT_ERR_UNKNOWN_ID;  // unknown, stale or already released id
constexpr int noDataRc = VLQT_ERR_NO_DATA;      // nothing to read yet (no snapshot, no preview, not Ready)
constexpr int wrongThreadRc = VLQT_ERR_WRONG_THREAD; // the manager is main thread only

} // namespace vlqt::res
