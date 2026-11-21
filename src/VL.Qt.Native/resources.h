/* resources.h - generic, window independent resource manager of QtNative (images package 02).
 *
 * One manager per process for every resource kind (images from package 03, later shaders and fonts). C# holds only
 * an id; all work happens here. Kept deliberately simple and fully logged (category VL.Qt.Resources): bugs in this
 * layer are hard to debug from vvvv.
 *
 * Ids: u64 = index (low 32 bits) | generation (high 32 bits), assigned by vlqt_res_create. After Released the index is
 * reused with a higher generation, so an old id never reaches a new resource. Unknown or stale ids: warning + -9.
 *
 * States (protocol.h vlqt_res_state): Pending -> Decoding -> Uploading -> Ready | Failed -> Released. Every change goes
 * through one function (transition) that rejects and logs invalid ones and logs valid ones on debug level.
 *
 * Threads:
 *   - Every entry point runs on the vvvv main thread (the thread of vlqt_app_process_events); one mutex still guards
 *     the table, so a stray call from another thread cannot corrupt it.
 *   - A fixed worker pool (default min(4, cores - 1), env VLQT_RES_WORKERS, started at the first job) runs only
 *     Kind::decode on pure data (bytes, QImage); workers never touch Qt Quick, the RHI or the table and never log.
 *     Results come back through one mutex guarded queue.
 *   - Upload (Kind::upload) and destroy (Kind::destroy) run on the main thread: uploads in beginFrame within the byte
 *     budget per frame, frees in endFrame.
 *
 * Refcount: one host reference (C#, dropped by vlqt_res_release) plus element references (acquire/releaseElement,
 * from package 04). When both are zero the resource is freed at the end of the frame (endFrame), never in a worker; a
 * running decode is cancelled or finishes first. After the host release no event is sent for the id any more.
 *
 * Status queue: vlqt_res_poll, records like the window poll: u16 event, u16 len, u64 id, payload (4-byte aligned,
 * len = 8 + payload bytes). STATE u8 state, ERROR UTF-8 text, INFO u32 w, u32 h, u32 format, u64 bytes, WARNING
 * UTF-8 text (Kind::takeWarning after a decode, at most one per resource).
 *
 * Replacement: start on a Ready resource decodes and uploads a second Work next to the current one; the state
 * stays Ready (no STATE record) and the current Work is swapped (old one destroyed) when the new upload is complete,
 * with a new INFO record. A failed replacement makes the resource Failed. start on a Failed resource starts over
 * (Failed -> Decoding). start while a decode/upload runs keeps only the newest Work and starts it afterwards.
 *
 * External content (snapshot, snapshot_resource.h): made on the main thread (beginExternal / finishExternal),
 * no worker and no upload, so Decoding -> Ready is allowed for that kind only; vlqt_res_wait does not block on it.
 *
 * Device lost (device.h): the manager is a device listener. lost: Kind::releaseDevice on every
 * Work no worker holds (textures, staging, running uploads of the old device go, the sources stay). restored: every
 * Ready resource with GPU content gets a replacement from Kind::restore (decode + upload within the budget, the state
 * stays Ready, INFO after the swap, C# sees no change); a running replacement or queued content is renewed the same
 * way; a first upload restarts its decode (Uploading -> Decoding, allowed only here). A decode that ran for the old
 * device (Kind::stale) is restarted when its result comes back, also when it failed. Uploads that fail because the
 * device is removed report no error (Kind::upload contract) and wait for the rebuild instead of ending in Failed.
 * Snapshots are CPU only and stay as they are.
 */
#pragma once

#include "vlqt.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace vlqt::res {

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
struct Work
{
    virtual ~Work() = default;
    std::atomic<bool> cancel{false}; // set on release / shutdown; decode should check it and return early
};

struct Info
{
    uint32_t width = 0, height = 0, format = 0;
};

// One resource kind (VLQT_RES_KIND_*). Stateless, registered once; the per resource data lives in its Work.
class Kind
{
public:
    virtual ~Kind() = default;
    virtual const char* name() const = 0;
    // Worker thread, pure data only. Returns an empty string on success, else the error text (cancel: any text,
    // the result is dropped anyway).
    virtual std::string decode(Work& work) = 0;
    // Main thread: uploads at most budget bytes (budget < 0 = no limit, vlqt_res_wait). Returns the bytes used,
    // sets done when the upload is complete, error on failure. May be called several times (stripes). A removed
    // device is no error: return 0 with done false, the rebuild (device lost) restarts the upload.
    virtual int64_t upload(Work& work, int64_t budget, bool& done, std::string& error) = 0;
    // Main thread: frees everything of the resource (GPU objects too). Called once, also after Failed.
    virtual void destroy(Work& work) { (void)work; }
    // Main thread, before Ready: values of the INFO event.
    virtual Info info(const Work& work) const { (void)work; return {}; }
    // Main thread, after a successful decode: a warning for the host (WARNING record), empty = none.
    virtual std::string takeWarning(Work& work) { (void)work; return {}; }
    // Main thread, while Uploading: bytes still to upload (F5 Resources page; 0 = unknown).
    virtual int64_t pendingBytes(const Work& work) const { (void)work; return 0; }

    // --- Device lost (see above), main thread ---
    // Frees every object of the old device, keeps the source. Called on every Work no worker holds; destroy may follow.
    virtual void releaseDevice(Work& work) { (void)work; }
    // A new Work that makes the same content again on the current device (decode + upload) from the source of work,
    // nullptr = nothing to restore (no GPU content).
    virtual std::shared_ptr<Work> restore(const Work& work) { (void)work; return nullptr; }
    // The Work was started for an older device: its decode result must not be uploaded.
    virtual bool stale(const Work& work) const { (void)work; return false; }
};

// Return codes of the C API: short names for the codes of vlqt_result (vlqt.h) that the resources return.
constexpr int okRc = VLQT_OK;
constexpr int errorRc = VLQT_ERR_INTERNAL;      // internal error, details in the log
constexpr int badValueRc = VLQT_ERR_BAD_ARGUMENT; // argument out of range / transition rejected
constexpr int staleIdRc = VLQT_ERR_UNKNOWN_ID;  // unknown, stale or already released id
constexpr int noDataRc = VLQT_ERR_NO_DATA;      // nothing to read yet (no snapshot, no preview, not Ready)

// --- C API backends (vlqt.h vlqt_res_*), main thread ---
int create(int kind, uint64_t* outId);
int release(uint64_t id);
int setBudget(int bytes);
int poll(unsigned char* out, int cap, int* written, int* required); // a buffer export (vlqt.h)
int wait(uint64_t id, int timeoutMs);
int list(void* out, int cap); // vlqt_res_info*
void shutdown();

// Starts the decode of a resource with its Work (package 03: LOAD / SET_PIXELS), or a replacement (see above). The
// Work must be the one the kind expects; kind != 0 is checked against the resource's kind. 0 / -9 / -7 (wrong kind).
int start(uint64_t id, std::shared_ptr<Work> work, int kind = 0);

// Current Work of a Ready resource of kind, else nullptr (main thread).
std::shared_ptr<Work> readyWork(uint64_t id, int kind);

// Kind (VLQT_RES_KIND_*) of a live, not host released id, else -9 (warning with what, nullptr = silent).
int kindOfId(uint64_t id, const char* what);

// The id is live and Failed (silent for unknown ids). Main thread.
bool isFailed(uint64_t id);

// Content made on the main thread without worker and upload (snapshot): Pending -> Decoding -> Ready | Failed.
// beginExternal: Pending/Failed -> Decoding (Failed: the old Work goes); Ready stays Ready (replacement, the current
// Work stays readable); Decoding stays Decoding (the caller keeps only its newest request). 0 / -9 / -7 (wrong kind).
int beginExternal(uint64_t id, int kind);
// finishExternal: result of beginExternal. work != nullptr: new content with bytes (CPU bytes, INFO u64), Decoding ->
// Ready or swap on a Ready resource, INFO before STATE Ready / after the swap. work == nullptr: Failed with error
// (RES_EV_ERROR). Returns 0, -9 if the id is gone or released by the host (result dropped, work destroyed).
int finishExternal(uint64_t id, std::shared_ptr<Work> work, int64_t bytes, const std::string& error);

// Changes whenever a resource gets new Ready content (first Ready or a replacement swap). Elements compare it once
// per pump instead of asking every resource (QtImageItem::afterResourceFrame). Main thread.
uint64_t contentEpoch();
// Changes whenever a resource becomes Failed (first load or replacement), e.g. a ShaderEffect stops drawing.
uint64_t failureEpoch();

// Element references (package 04). 0 / -9.
int acquire(uint64_t id);
int releaseElement(uint64_t id);

// --- Frame hooks, main thread (vlqt_app_process_events_for) ---
void beginFrame(); // takes worker results, uploads within the budget (before the windows render)
void endFrame();   // frees resources whose references are all gone

// --- F5 overlay, Resources page, main thread ---
struct Row
{
    uint64_t id;
    const char* kind; // Kind::name()
    State state;
    int64_t bytes;    // uploaded
    int hostRefs, elementRefs;
    int64_t ageMs;    // time in the current state (Ready: since the current content)
};
struct Overview
{
    int64_t budget = 0, usedLastFrame = 0; // upload bytes per frame / used in the last beginFrame
    int64_t uploadBytes = 0;               // still to upload (Kind::pendingBytes of the queued uploads)
    int uploadJobs = 0, decodeJobs = 0;    // queued uploads / decodes waiting for a worker
};
// Live resources into rows (cleared, capacity kept) and the upload totals, under one lock.
void snapshot(std::vector<Row>& rows, Overview& out);

// --- Test hooks (test/test_resource_kind.cpp) ---
// The kind VLQT_RES_KIND_TEST (vlqt_test.h), defined in test/test_resource_kind.cpp.
Kind* testKind();
// vlqt_test_res_transition: the state machine's transition (VLQT_RES_STATE_*). 0 / -7 (rejected, logged) / -9.
int testTransition(uint64_t id, int from, int to);

} // namespace vlqt::res
