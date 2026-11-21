/* resources.h - the interface of the resource manager that vlqt.cpp and the kinds call: the C API backends, the frame
 * hooks, the F5 overview and the test hooks. A resource is one id for C#; the manager decodes, uploads and frees its
 * content. The contract (ids, states, threads, reference counts, replacement, device loss) is in
 * docs/design/resources.md; the interface of a kind is in resource_kind.h.
 */
#pragma once

#include "resources/resource_kind.h"

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace vlqt::res {

// --- C API backends (vlqt.h vlqt_res_*), main thread ---
int create(int kind, uint64_t* outId);
int release(uint64_t id);
int setBudget(int bytes);
int poll(unsigned char* out, int cap, int* written, int* required); // a buffer export (vlqt.h)
int wait(uint64_t id, int timeoutMs);
int list(void* out, int cap); // vlqt_res_info*
void shutdown();

// Starts the decode of a resource with its Content (LOAD / SET_PIXELS), or a replacement (docs/design/resources.md).
// The Content must be the one the kind expects; kind != 0 is checked against the resource's kind. 0 / -9 / -7 (wrong kind).
int start(uint64_t id, std::shared_ptr<Content> work, int kind = 0);

// Current Content of a Ready resource of kind, else nullptr (main thread).
std::shared_ptr<Content> readyContent(uint64_t id, int kind);

// Kind (VLQT_RESOURCE_KIND_*) of a live, not host released id, else -9 (warning with what, nullptr = silent).
int kindOfId(uint64_t id, const char* what);

// The id is live and Failed (silent for unknown ids). Main thread.
bool isFailed(uint64_t id);

// Content made on the main thread without worker and upload (snapshot): Pending -> Decoding -> Ready | Failed.
// beginExternal: Pending/Failed -> Decoding (Failed: the old Content goes); Ready stays Ready (replacement, the current
// Content stays readable); Decoding stays Decoding (the caller keeps only its newest request). 0 / -9 / -7 (wrong kind).
int beginExternal(uint64_t id, int kind);
// finishExternal: result of beginExternal. work != nullptr: new content with bytes (CPU bytes, INFO u64), Decoding ->
// Ready or swap on a Ready resource, INFO before STATE Ready / after the swap. work == nullptr: Failed with error
// (RESOURCE_EVENT_ERROR). Returns 0, -9 if the id is gone or released by the host (result dropped, work destroyed).
int finishExternal(uint64_t id, std::shared_ptr<Content> work, int64_t bytes, const std::string& error);

// Changes whenever a resource gets new Ready content (first Ready or a replacement swap). Elements compare it once
// per pump instead of asking every resource (QtImageItem::afterResourceFrame). Main thread.
uint64_t contentEpoch();
// Changes whenever a resource becomes Failed (first load or replacement), e.g. a ShaderEffect stops drawing.
uint64_t failureEpoch();

// Element references. 0 / -9.
int acquire(uint64_t id);
int releaseElement(uint64_t id);

// --- Frame hooks, main thread (vlqt_app_process_events_for) ---
void beginFrame(); // takes worker results, uploads within the budget (before the windows render)
void endFrame();   // frees resources whose references are all gone

// --- F5 overlay, Resources page, main thread ---
struct OverviewRow
{
    uint64_t id;
    const char* kind; // Kind::name()
    State state;
    int64_t bytes;    // uploaded
    int hostRefs, elementRefs;
    int64_t ageMs;    // time in the current state (Ready: since the current content)
};
struct OverviewTotals
{
    int64_t budget = 0, usedLastFrame = 0; // upload bytes per frame / used in the last beginFrame
    int64_t uploadBytes = 0;               // still to upload (Kind::pendingBytes of the queued uploads)
    int uploadJobs = 0, decodeJobs = 0;    // queued uploads / decodes waiting for a worker
};
// Live resources into rows (cleared, capacity kept) and the upload totals, under one lock.
void overview(std::vector<OverviewRow>& rows, OverviewTotals& out);

// --- Test hooks (test/test_resource_kind.cpp) ---
// The kind VLQT_RESOURCE_KIND_TEST (vlqt_test.h), defined in test/test_resource_kind.cpp.
Kind* testKind();
// vlqt_test_res_transition: the state machine's transition (VLQT_RESOURCE_STATE_*). 0 / -7 (rejected, logged) / -9.
int testTransition(uint64_t id, int from, int to);

} // namespace vlqt::res
