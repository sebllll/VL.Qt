/* resource_manager.h - the manager of the resource table: slots, state machine, reference counts, replacement,
 * external content, uploads and device loss.
 *
 * One manager per process for every resource kind. Every entry point runs on the main thread; m_mutex guards the table
 * and, shared with it, the queues of the worker pool. Design notes: docs/design/resources.md.
 */
#pragma once

#include "resources/resource_events.h"
#include "resources/resource_kind.h"
#include "resources/resource_workers.h"
#include "resources/resources.h"

#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace vlqt::res {

constexpr int64_t defaultBudget = 8 << 20; // bytes uploaded per frame

struct Slot
{
    uint32_t generation = 1;
    bool used = false;
    int kind = 0;
    State state = State::Released;
    std::shared_ptr<Content> work;
    std::shared_ptr<Content> replacement; // new content of a Ready resource, in Decoding/Uploading next to `work`
    std::shared_ptr<Content> waiting;     // newest start while a decode/upload runs, started afterwards
    bool replacementUploading = false;    // the replacement is decoded and in m_uploads
    bool warned = false;                  // a WARNING record was sent (at most one per resource)
    int64_t replacementBytes = 0;         // uploaded bytes of the replacement
    int hostRefs = 0, elementRefs = 0;
    bool inWorker = false;     // a job of this resource is queued or running in a worker
    bool freeQueued = false;   // in toFree: both refs are zero, freed at a frame end
    int64_t bytes = 0;         // uploaded bytes
    int64_t sinceMs = 0;       // steadyMs() of the last state change (F5 Resources page)
};

class Manager
{
public:
    // --- API (main thread; m_mutex guards everything) ---
    int create(int kind, uint64_t* outId);
    int release(uint64_t id);
    int setBudget(int bytes);
    int poll(unsigned char* out, int cap, int* written, int* required);
    int start(uint64_t id, std::shared_ptr<Content> work, int kind);
    std::shared_ptr<Content> readyContent(uint64_t id, int kind);
    bool isFailed(uint64_t id);
    int kindOfId(uint64_t id, const char* what);
    int beginExternal(uint64_t id, int kind);
    int finishExternal(uint64_t id, std::shared_ptr<Content> work, int64_t bytes, const std::string& error);
    int wait(uint64_t id, int timeoutMs);
    int list(vlqt_res_info* out, int cap);
    void shutdown();
    int acquire(uint64_t id);
    int releaseElement(uint64_t id);

    // --- Frame hooks ---
    void beginFrame();
    // F5 Resources page: live resources and the upload totals under one lock; rows keeps its capacity.
    void overview(std::vector<OverviewRow>& rows, OverviewTotals& out);
    void endFrame();
    // Ids that got new Ready content or became Failed up to the last beginFrame; valid until the next one (main thread).
    const std::vector<uint64_t>& changedThisFrame() const { return m_changed; }

    // --- Device lost (device.h listener, main thread inside device::rebuild) ---
    // Frees every object of the old device; Contents a worker holds are restarted when their result comes back.
    void deviceLost();

    // New device: everything with GPU content uploads again within the budget.
    void deviceRestored();

    // test hook: vlqt_test_res_transition (test/test_resource_kind.cpp).
    int testTransition(uint64_t id, int from, int to);

private:
    // Slot of a live id or nullptr; what = API name for the warning (nullptr = internal lookup, silent).
    Slot* find(uint64_t id, const char* what);
    bool transition(uint64_t id, Slot& s, State from, State to, bool restart = false);
    void fail(uint64_t id, Slot& s, const std::string& error);
    void handleResult(const Result& r);

    // One upload call within budget (< 0 = no limit); returns the bytes used. Leaves Ready, Failed or Uploading
    // (or Decoding when queued content started).
    int64_t uploadStep(uint64_t id, Slot& s, int64_t budget);

    void pushInfo(uint64_t id, const Slot& s);

    // At most one WARNING record per resource; every warning is logged.
    void warn(uint64_t id, Slot& s, const std::string& text);

    // A decode or upload of the resource runs (first content or replacement).
    static bool busy(const Slot& s) { return s.state == State::Decoding || s.state == State::Uploading || s.replacement; }
    static bool decoding(const Slot& s) { return s.state == State::Decoding || (s.replacement && !s.replacementUploading); }
    static bool uploading(const Slot& s) { return s.state == State::Uploading || (s.replacement && s.replacementUploading); }

    // New content: Pending/Failed -> Decoding, or a replacement next to the current content of a Ready resource.
    bool begin(uint64_t id, Slot& s, std::shared_ptr<Content> work);
    void enqueue(uint64_t id, Slot& s, const std::shared_ptr<Content>& work);
    void startQueued(uint64_t id, Slot& s);

    // The replacement failed: its Content becomes the current one (without texture), the resource Failed.
    void failReplacement(uint64_t id, Slot& s, const std::string& error);

    // Replaces w by a new Content for the current device (Kind::restore); false = nothing to restore, w stays.
    static bool renew(Kind* kind, std::shared_ptr<Content>& w);
    void maybeQueueFree(uint64_t id, Slot& s);

    // Main thread, never while a worker holds the Content.
    void free(uint64_t id, Slot& s);
    void pushState(uint64_t id, const Slot& s);
    // Records a content change or a failure for the next beginFrame (under m_mutex).
    void noteChanged(uint64_t id);

    std::mutex m_mutex; // guards everything below and the queues of m_pool
    WorkerPool m_pool{m_mutex};
    ResourceEventQueue m_events;
    std::vector<Slot> m_slots;
    std::vector<uint32_t> m_free;
    std::deque<uint64_t> m_uploads;
    std::vector<uint64_t> m_toFree;
    int64_t m_budget = defaultBudget;
    int64_t m_usedLastFrame = 0; // upload bytes of the last beginFrame
    std::vector<uint64_t> m_changedPending; // changes since the last beginFrame (m_mutex)
    std::vector<uint64_t> m_changed;        // what the last beginFrame published, read by changedThisFrame
};

// The one manager, created at the first use and never deleted (like g_app): workers may still run while the process ends.
Manager& manager();
// The manager if it exists already, else nullptr: the frame hooks and shutdown must not create it.
Manager* existingManager();

} // namespace vlqt::res
