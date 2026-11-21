#include "resources.h"

#include "app/exports.h"
#include "audio_tap.h"
#include "device.h"
#include "image_resource.h"
#include "protocol.h"
#include "shader_resource.h"
#include "snapshot_resource.h"
#include "threadname.h"
#include "vlqt.h"
#include "vlqt_test.h" // test hook: VLQT_RES_KIND_TEST

#include <QtCore/QByteArray>
#include <QtCore/QLoggingCategory>
#include <QtCore/QtGlobal>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

namespace vlqt::res {

namespace {

Q_LOGGING_CATEGORY(lcRes, "VL.Qt.Resources")

// Counts content changes of Ready resources (first Ready, replacement swap); see contentEpoch.
std::atomic<uint64_t> s_contentEpoch{0};
// Counts transitions to Failed; see failureEpoch.
std::atomic<uint64_t> s_failureEpoch{0};

constexpr uint32_t maxSlots = 1u << 20;      // ~1M live resources; more is a leak in the host
constexpr int64_t defaultBudget = 8 << 20;   // bytes uploaded per frame
constexpr int maxErrorText = 4000;           // ERROR payload cap (u16 len)

uint32_t indexOf(uint64_t id) { return uint32_t(id & 0xffffffffu); }
uint32_t generationOf(uint64_t id) { return uint32_t(id >> 32); }
uint64_t makeId(uint32_t index, uint32_t generation) { return (uint64_t(generation) << 32) | index; }

// "#index.generation" for log lines.
QByteArray idText(uint64_t id)
{
    return '#' + QByteArray::number(indexOf(id)) + '.' + QByteArray::number(generationOf(id));
}

bool validState(int s) { return s >= int(State::Pending) && s <= int(State::Released); }

// Kinds whose content is made on the main thread without worker and upload (beginExternal / finishExternal).
bool externalKind(int kind) { return kind == VLQT_RES_KIND_SNAPSHOT || kind == VLQT_RES_KIND_AUDIO_TAP; }

// The allowed transitions; everything else is rejected by transition(). external = content made on the main thread
// without upload (snapshot, audio tap): Decoding -> Ready directly. restart = device lost during the first upload:
// Uploading -> Decoding (decode again for the new device).
bool allowed(State from, State to, bool external, bool restart)
{
    if (to == State::Released)
        return from != State::Released;
    switch (from) {
    case State::Pending:   return to == State::Decoding || to == State::Failed;
    case State::Decoding:  return to == State::Uploading || to == State::Failed || (external && to == State::Ready);
    case State::Uploading: return to == State::Ready || to == State::Failed || (restart && to == State::Decoding);
    case State::Ready:     return to == State::Failed;   // failed replacement
    case State::Failed:    return to == State::Decoding; // new content after a failure
    default:               return false;
    }
}

struct Slot
{
    uint32_t generation = 1;
    bool used = false;
    int kind = 0;
    State state = State::Released;
    std::shared_ptr<Work> work;
    std::shared_ptr<Work> next;   // replacement of a Ready resource in Decoding/Uploading
    std::shared_ptr<Work> queued; // newest start while a decode/upload runs, started afterwards
    bool nextUploading = false;   // next is decoded and in m_uploads
    bool warned = false;          // a WARNING record was sent (at most one per resource)
    int64_t nextBytes = 0;        // uploaded bytes of next
    int hostRefs = 0, elementRefs = 0;
    bool inWorker = false;     // a job of this resource is queued or running in a worker
    bool freeQueued = false;   // in toFree: both refs are zero, freed at a frame end
    int64_t bytes = 0;         // uploaded bytes
    int64_t sinceMs = 0;       // steadyMs() of the last state change (F5 Resources page)
};

int64_t steadyMs()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

struct Job
{
    uint64_t id;
    Kind* kind;
    std::shared_ptr<Work> work;
};

struct Result
{
    uint64_t id;
    std::string error;
};

// Worker thread (or the thread of vlqt_res_wait): the decode on pure data, without the manager's mutex.
std::string runDecode(const Job& job)
{
    try {
        return job.work->cancel.load() ? "cancelled" : job.kind->decode(*job.work);
    } catch (const std::exception& e) {
        return std::string("exception in decode: ") + e.what();
    } catch (...) {
        return "unknown exception in decode";
    }
}

class Manager
{
public:
    // --- API (main thread; m_mutex guards everything) ---

    int create(int kind, uint64_t* outId)
    {
        std::lock_guard lock(m_mutex);
        if (!outId) {
            qCWarning(lcRes, "create: null outId");
            return badValueRc;
        }
        *outId = 0;
        if (!kindOf(kind)) {
            qCWarning(lcRes, "create: unknown kind %d", kind);
            return badValueRc;
        }
        uint32_t index;
        if (!m_free.empty()) {
            index = m_free.back();
            m_free.pop_back();
        } else {
            if (m_slots.size() >= maxSlots) {
                qCCritical(lcRes, "create: more than %u live resources, refused (released ones are freed at the frame "
                                  "end; is the host leaking ids?)", maxSlots - 1);
                return errorRc;
            }
            if (m_slots.empty())
                m_slots.emplace_back(); // index 0 stays unused
            index = uint32_t(m_slots.size());
            m_slots.emplace_back();
        }
        Slot& s = m_slots[index];
        s.used = true;
        s.kind = kind;
        s.state = State::Pending;
        s.work.reset();
        s.next.reset();
        s.queued.reset();
        s.nextUploading = s.warned = false;
        s.nextBytes = 0;
        s.hostRefs = 1;
        s.elementRefs = 0;
        s.inWorker = false;
        s.freeQueued = false;
        s.bytes = 0;
        s.sinceMs = steadyMs();
        const uint64_t id = makeId(index, s.generation);
        *outId = id;
        qCDebug(lcRes, "%s %s: created (Pending)", idText(id).constData(), kindOf(kind)->name());
        pushState(id, s);
        return okRc;
    }

    int release(uint64_t id)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, "release");
        if (!s)
            return staleIdRc;
        if (s->hostRefs == 0) {
            qCWarning(lcRes, "release: %s already released by the host", idText(id).constData());
            return staleIdRc;
        }
        s->hostRefs = 0;
        dropEvents(id); // no event for the id any more, also not one queued before the release
        qCDebug(lcRes, "%s: host release (%s, %d element refs)", idText(id).constData(), stateName(s->state),
            s->elementRefs);
        maybeQueueFree(id, *s);
        return okRc;
    }

    int setBudget(int bytes)
    {
        std::lock_guard lock(m_mutex);
        if (bytes <= 0) {
            qCWarning(lcRes, "set_budget: %d bytes rejected (must be > 0)", bytes);
            return badValueRc;
        }
        m_budget = bytes;
        qCInfo(lcRes, "upload budget %d bytes per frame", bytes);
        return okRc;
    }

    int poll(unsigned char* out, int cap, int* written, int* required)
    {
        if (const int rc = beginBuffer(out, cap, written, required); rc != okRc)
            return rc;
        std::lock_guard lock(m_mutex);
        const int rc = copyOut(m_events.constData(), int(m_events.size()), out, cap, written, required);
        if (rc == okRc)
            m_events.resize(0);
        return rc;
    }

    int start(uint64_t id, std::shared_ptr<Work> work, int kind)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, "start");
        if (!s)
            return staleIdRc;
        if (!work || s->hostRefs == 0) {
            qCWarning(lcRes, "start: %s %s", idText(id).constData(), !work ? "without work" : "already released");
            return !work ? badValueRc : staleIdRc;
        }
        if (kind != 0 && s->kind != kind) {
            qCWarning(lcRes, "start: %s is a %s resource, not a %s one", idText(id).constData(), kindOf(s->kind)->name(),
                kindOf(kind) ? kindOf(kind)->name() : "?");
            return badValueRc;
        }
        if (busy(*s)) { // keep only the newest content, started when the running one is done
            if (s->queued)
                kindOf(s->kind)->destroy(*s->queued);
            s->queued = std::move(work);
            qCDebug(lcRes, "%s: new content queued (%s)", idText(id).constData(), stateName(s->state));
            return okRc;
        }
        return begin(id, *s, std::move(work)) ? okRc : badValueRc;
    }

    std::shared_ptr<Work> readyWork(uint64_t id, int kind)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, nullptr);
        return s && s->kind == kind && s->state == State::Ready ? s->work : nullptr;
    }

    bool isFailed(uint64_t id)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, nullptr);
        return s && s->state == State::Failed;
    }

    int kindOfId(uint64_t id, const char* what)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, what);
        if (!s)
            return staleIdRc;
        if (s->hostRefs == 0) {
            if (what)
                qCWarning(lcRes, "%s: %s already released by the host", what, idText(id).constData());
            return staleIdRc;
        }
        return s->kind;
    }

    int beginExternal(uint64_t id, int kind)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, "snapshot");
        if (!s)
            return staleIdRc;
        if (s->hostRefs == 0) {
            qCWarning(lcRes, "snapshot: %s already released", idText(id).constData());
            return staleIdRc;
        }
        if (s->kind != kind) {
            qCWarning(lcRes, "snapshot: %s is a %s resource, not a %s one", idText(id).constData(), kindOf(s->kind)->name(),
                kindOf(kind) ? kindOf(kind)->name() : "?");
            return badValueRc;
        }
        switch (s->state) {
        case State::Pending:
        case State::Failed:
            if (s->work) // Failed: the old content goes
                kindOf(s->kind)->destroy(*s->work);
            s->work.reset();
            s->bytes = 0;
            return transition(id, *s, s->state, State::Decoding) ? okRc : badValueRc;
        case State::Ready:    // replacement: the current content stays readable
        case State::Decoding: // the caller keeps only its newest request
            return okRc;
        default:
            qCWarning(lcRes, "snapshot: %s is %s", idText(id).constData(), stateName(s->state));
            return badValueRc;
        }
    }

    int finishExternal(uint64_t id, std::shared_ptr<Work> work, int64_t bytes, const std::string& error)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, nullptr);
        if (!s || s->hostRefs == 0 || s->freeQueued) { // released during the grab: drop the result
            if (s && work)
                kindOf(s->kind)->destroy(*work);
            qCDebug(lcRes, "%s: snapshot result dropped (released)", idText(id).constData());
            return staleIdRc;
        }
        Kind* kind = kindOf(s->kind);
        if (!work) {
            fail(id, *s, error.empty() ? std::string("snapshot failed") : error);
            return okRc;
        }
        if (s->state == State::Ready) {
            if (s->work)
                kind->destroy(*s->work);
            s->work = std::move(work);
            s->bytes = bytes;
            s->sinceMs = steadyMs();
            pushInfo(id, *s);
            s_contentEpoch++;
            qCDebug(lcRes, "%s: snapshot replaced", idText(id).constData());
            return okRc;
        }
        if (s->state != State::Decoding) {
            qCWarning(lcRes, "%s: snapshot result in state %s dropped", idText(id).constData(), stateName(s->state));
            kind->destroy(*work);
            return badValueRc;
        }
        s->work = std::move(work);
        s->bytes = bytes;
        pushInfo(id, *s);
        transition(id, *s, State::Decoding, State::Ready);
        s_contentEpoch++;
        return okRc;
    }

    int wait(uint64_t id, int timeoutMs)
    {
        std::unique_lock lock(m_mutex);
        Slot* s = find(id, "wait");
        if (!s)
            return staleIdRc;
        if (s->hostRefs == 0) { // still in the table (element ref, running decode), but dead for the host
            qCWarning(lcRes, "wait: %s already released by the host", idText(id).constData());
            return staleIdRc;
        }
        if (externalKind(s->kind)) // no worker job: a grab finishes in a later pump, a tap is Ready at init
            return int(s->state);
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(std::max(0, timeoutMs));
        if (decoding(*s)) {
            // Still queued: run the decode here (completely, the timeout applies only to waiting for a worker).
            // Already in a worker: wait for exactly its result.
            auto queued = std::find_if(m_jobs.begin(), m_jobs.end(), [id](const Job& j) { return j.id == id; });
            if (queued != m_jobs.end()) {
                Job job = std::move(*queued);
                m_jobs.erase(queued);
                lock.unlock();
                Result r{id, runDecode(job)};
                lock.lock();
                m_results.push_back(std::move(r));
            }
            auto mine = [&] {
                return std::find_if(m_results.begin(), m_results.end(), [id](const Result& r) { return r.id == id; });
            };
            if (!m_resultCv.wait_until(lock, deadline, [&] { return mine() != m_results.end(); })) {
                s = find(id, nullptr);
                qCWarning(lcRes, "wait: %s timed out after %d ms (%s)", idText(id).constData(), timeoutMs,
                    s ? stateName(s->state) : "gone");
                return s ? int(s->state) : staleIdRc;
            }
            auto it = mine();
            Result r = std::move(*it);
            m_results.erase(it);
            handleResult(r);
        }
        s = find(id, nullptr); // the table may have changed while unlocked
        if (!s) {
            qCWarning(lcRes, "wait: %s released while waiting", idText(id).constData());
            return staleIdRc;
        }
        if (uploading(*s) && !s->freeQueued) {
            m_uploads.erase(std::remove(m_uploads.begin(), m_uploads.end(), id), m_uploads.end());
            uploadStep(id, *s, -1);
        }
        return int(s->state);
    }

    int list(vlqt_res_info* out, int cap)
    {
        std::lock_guard lock(m_mutex);
        int n = 0;
        for (size_t i = 1; i < m_slots.size(); i++) {
            const Slot& s = m_slots[i];
            if (!s.used)
                continue;
            if (out && n < cap)
                out[n] = {makeId(uint32_t(i), s.generation), s.kind, int(s.state), s.bytes, s.hostRefs, s.elementRefs};
            n++;
        }
        return n;
    }

    void shutdown()
    {
        std::vector<std::thread> threads;
        {
            std::lock_guard lock(m_mutex);
            m_stop = true;
            for (Slot& s : m_slots)
                for (Work* w : {s.work.get(), s.next.get()})
                    if (s.used && w)
                        w->cancel = true;
            threads.swap(m_threads);
        }
        m_jobCv.notify_all();
        for (std::thread& t : threads)
            t.join();

        std::lock_guard lock(m_mutex);
        int open = 0;
        for (size_t i = 1; i < m_slots.size(); i++) {
            Slot& s = m_slots[i];
            if (!s.used)
                continue;
            if (s.hostRefs > 0 || s.elementRefs > 0)
                open++;
            s.inWorker = false;
            free(makeId(uint32_t(i), s.generation), s);
        }
        if (open > 0)
            qCWarning(lcRes, "shutdown: %d resource(s) still referenced (not released by the host or an element)", open);
        if (!threads.empty() || open > 0)
            qCInfo(lcRes, "shutdown: %d worker(s) joined, %d resource(s) freed", int(threads.size()), open);
        m_jobs.clear();
        m_results.clear();
        m_uploads.clear();
        m_toFree.clear();
        m_events.resize(0);
        m_stop = false; // the next start creates the workers again
    }

    int acquire(uint64_t id)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, "acquire");
        if (!s)
            return staleIdRc;
        if (s->freeQueued) {
            qCWarning(lcRes, "acquire: %s has no references left, it is freed at the frame end", idText(id).constData());
            return staleIdRc;
        }
        s->elementRefs++;
        return okRc;
    }

    int releaseElement(uint64_t id)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, "release element");
        if (!s)
            return staleIdRc;
        if (s->elementRefs <= 0) {
            qCWarning(lcRes, "release element: %s has no element references", idText(id).constData());
            return staleIdRc;
        }
        s->elementRefs--;
        maybeQueueFree(id, *s);
        return okRc;
    }

    // --- Frame hooks ---

    void beginFrame()
    {
        std::lock_guard lock(m_mutex);
        while (!m_results.empty()) {
            Result r = std::move(m_results.front());
            m_results.pop_front();
            handleResult(r);
        }
        int64_t left = m_budget;
        while (!m_uploads.empty() && left > 0) {
            const uint64_t id = m_uploads.front();
            Slot* s = find(id, nullptr);
            if (!s || !uploading(*s) || s->freeQueued) { // released meanwhile: endFrame frees it
                m_uploads.pop_front();
                continue;
            }
            left -= uploadStep(id, *s, left);
            if (uploading(*s)) // budget used up, the rest in the next frame (queued content starts in a worker)
                break;
            m_uploads.pop_front();
        }
        m_usedLastFrame = m_budget - left;
    }

    // F5 Resources page: live resources and the upload totals under one lock; rows keeps its capacity.
    void snapshot(std::vector<Row>& rows, Overview& out)
    {
        std::lock_guard lock(m_mutex);
        const int64_t now = steadyMs();
        rows.clear();
        for (size_t i = 1; i < m_slots.size(); i++) {
            const Slot& s = m_slots[i];
            if (s.used)
                rows.push_back({makeId(uint32_t(i), s.generation), kindOf(s.kind)->name(), s.state, s.bytes, s.hostRefs, s.elementRefs,
                    now - s.sinceMs});
        }
        out = {};
        out.budget = m_budget;
        out.usedLastFrame = m_usedLastFrame;
        for (const uint64_t id : m_uploads) {
            const Slot* s = find(id, nullptr);
            if (!s || !uploading(*s) || s->freeQueued) // same filter as beginFrame
                continue;
            const Work* w = s->next ? s->next.get() : s->work.get(); // a replacement uploads next
            if (!w)
                continue;
            out.uploadJobs++;
            out.uploadBytes += kindOf(s->kind)->pendingBytes(*w);
        }
        out.decodeJobs = int(m_jobs.size());
    }

    void endFrame()
    {
        std::lock_guard lock(m_mutex);
        if (m_toFree.empty())
            return;
        size_t keep = 0;
        for (const uint64_t id : m_toFree) {
            Slot* s = find(id, nullptr);
            if (!s)
                continue;
            if (s->inWorker) { // the decode finishes or cancels first; next frame end
                m_toFree[keep++] = id;
                continue;
            }
            free(id, *s);
        }
        m_toFree.resize(keep);
    }

    // --- Device lost (device.h listener, main thread inside device::rebuild) ---

    // Frees every object of the old device; Works a worker holds are restarted when their result comes back.
    void deviceLost()
    {
        std::lock_guard lock(m_mutex);
        for (Slot& s : m_slots) {
            if (!s.used)
                continue;
            Kind* kind = kindOf(s.kind);
            const Work* inWorker = s.inWorker ? (s.next ? s.next.get() : s.work.get()) : nullptr;
            for (Work* w : {s.work.get(), s.next.get(), s.queued.get()})
                if (w && w != inWorker)
                    kind->releaseDevice(*w);
        }
    }

    // New device: everything with GPU content uploads again within the budget.
    void deviceRestored()
    {
        std::lock_guard lock(m_mutex);
        int restarted = 0;
        for (size_t i = 1; i < m_slots.size(); i++) {
            Slot& s = m_slots[i];
            if (!s.used || s.freeQueued)
                continue;
            const uint64_t id = makeId(uint32_t(i), s.generation);
            Kind* kind = kindOf(s.kind);
            if (s.queued)
                renew(kind, s.queued);
            if (s.inWorker) // handleResult restarts it (stale)
                continue;
            if (s.next) { // replacement in its upload: start it over
                if (!renew(kind, s.next))
                    continue;
                m_uploads.erase(std::remove(m_uploads.begin(), m_uploads.end(), id), m_uploads.end());
                s.nextUploading = false;
                s.nextBytes = 0;
                enqueue(id, s, s.next);
            } else if (s.state == State::Ready) { // replacement, the state stays Ready
                std::shared_ptr<Work> fresh = kind->restore(*s.work);
                if (!fresh)
                    continue;
                s.next = std::move(fresh);
                s.nextBytes = 0;
                s.nextUploading = false;
                enqueue(id, s, s.next);
            } else if (s.state == State::Uploading) { // first upload: decode again
                if (!renew(kind, s.work))
                    continue;
                m_uploads.erase(std::remove(m_uploads.begin(), m_uploads.end(), id), m_uploads.end());
                s.bytes = 0;
                transition(id, s, State::Uploading, State::Decoding, true);
                enqueue(id, s, s.work);
            } else {
                continue;
            }
            restarted++;
        }
        if (restarted > 0)
            qCInfo(lcRes, "device lost: %d resource(s) upload again", restarted);
    }

    // test hook: vlqt_test_res_transition (test/test_resource_kind.cpp).
    int testTransition(uint64_t id, int from, int to)
    {
        std::lock_guard lock(m_mutex);
        Slot* s = find(id, "test transition");
        if (!s)
            return staleIdRc;
        if (!validState(from) || !validState(to)) {
            qCWarning(lcRes, "test transition: %s: unknown state %d -> %d", idText(id).constData(), from, to);
            return badValueRc;
        }
        if (State(to) == State::Released) { // only the frame end frees
            qCWarning(lcRes, "test transition: %s: Released is reached only by release + frame end", idText(id).constData());
            return badValueRc;
        }
        return transition(id, *s, State(from), State(to)) ? okRc : badValueRc;
    }

private:
    Kind* kindOf(int kind)
    {
        switch (kind) {
        case VLQT_RES_KIND_TEST:  return testKind(); // test hook: test/test_resource_kind.cpp
        case VLQT_RES_KIND_IMAGE: return image::kind();
        case VLQT_RES_KIND_SNAPSHOT: return snap::kind();
        case VLQT_RES_KIND_SHADER: return shader::kind();
        case VLQT_RES_KIND_AUDIO_TAP: return audio::kind();
        default:                  return nullptr;
        }
    }

    // Slot of a live id or nullptr; what = API name for the warning (nullptr = internal lookup, silent).
    Slot* find(uint64_t id, const char* what)
    {
        const uint32_t index = indexOf(id);
        if (index > 0 && index < m_slots.size()) {
            Slot& s = m_slots[index];
            if (s.used && s.generation == generationOf(id))
                return &s;
        }
        if (what)
            qCWarning(lcRes, "%s: unknown or stale resource id %s", what, idText(id).constData());
        return nullptr;
    }

    bool transition(uint64_t id, Slot& s, State from, State to, bool restart = false)
    {
        if (s.state != from || !allowed(from, to, externalKind(s.kind), restart)) {
            qCWarning(lcRes, "%s: invalid transition %s -> %s rejected (state is %s)", idText(id).constData(),
                stateName(from), stateName(to), stateName(s.state));
            return false;
        }
        s.state = to;
        s.sinceMs = steadyMs();
        if (to == State::Failed)
            s_failureEpoch++;
        qCDebug(lcRes, "%s: %s -> %s", idText(id).constData(), stateName(from), stateName(to));
        if (to != State::Released)
            pushState(id, s);
        return true;
    }

    void fail(uint64_t id, Slot& s, const std::string& error)
    {
        if (!transition(id, s, s.state, State::Failed))
            return;
        qCWarning(lcRes, "%s %s failed: %s", idText(id).constData(), kindOf(s.kind)->name(), error.c_str());
        if (s.hostRefs > 0) {
            const int len = std::min(int(error.size()), maxErrorText);
            pushEvent(VLQT_RES_EV_ERROR, id, error.data(), len);
        }
    }

    void handleResult(const Result& r)
    {
        Slot* s = find(r.id, nullptr);
        if (!s) {
            qCCritical(lcRes, "worker result for %s without a resource (freed while in a worker?)", idText(r.id).constData());
            return;
        }
        s->inWorker = false;
        if (s->freeQueued) { // released while decoding: drop the result, endFrame frees
            qCDebug(lcRes, "%s: decode result dropped (released)", idText(r.id).constData());
            return;
        }
        Kind* kind = kindOf(s->kind);
        // Decoded for a device that is gone meanwhile: decode again, also after an error (the old device).
        if (std::shared_ptr<Work>& decoded = s->next ? s->next : s->work; decoded && kind->stale(*decoded)
            && renew(kind, decoded)) {
            qCDebug(lcRes, "%s: decode for the old device dropped, decoding again", idText(r.id).constData());
            enqueue(r.id, *s, decoded);
            return;
        }
        if (!r.error.empty()) {
            if (s->next)
                failReplacement(r.id, *s, r.error);
            else
                fail(r.id, *s, r.error);
            startQueued(r.id, *s);
            return;
        }
        warn(r.id, *s, kind->takeWarning(s->next ? *s->next : *s->work));
        if (s->next) {
            s->nextUploading = true;
            m_uploads.push_back(r.id);
        } else if (transition(r.id, *s, State::Decoding, State::Uploading)) {
            m_uploads.push_back(r.id);
        }
    }

    // One upload call within budget (< 0 = no limit); returns the bytes used. Leaves Ready, Failed or Uploading
    // (or Decoding when queued content started).
    int64_t uploadStep(uint64_t id, Slot& s, int64_t budget)
    {
        Kind* kind = kindOf(s.kind);
        const bool replacing = s.next != nullptr;
        bool done = false;
        std::string error;
        const int64_t used = std::max<int64_t>(0, kind->upload(replacing ? *s.next : *s.work, budget, done, error));
        (replacing ? s.nextBytes : s.bytes) += used;
        if (!error.empty()) {
            if (replacing)
                failReplacement(id, s, error);
            else
                fail(id, s, error);
            startQueued(id, s);
        } else if (done) {
            if (replacing) { // swap: the old content is visible up to here
                kind->destroy(*s.work);
                s.work = std::move(s.next);
                s.bytes = s.nextBytes;
                s.nextBytes = 0;
                s.nextUploading = false;
                s.sinceMs = steadyMs(); // still Ready, but the age on the F5 page counts from the new content
                pushInfo(id, s);
                s_contentEpoch++;
                qCDebug(lcRes, "%s: content replaced", idText(id).constData());
            } else {
                pushInfo(id, s);
                transition(id, s, State::Uploading, State::Ready);
                s_contentEpoch++;
            }
            startQueued(id, s);
        }
        return used;
    }

    void pushInfo(uint64_t id, const Slot& s)
    {
        if (s.hostRefs == 0)
            return;
        const Info info = kindOf(s.kind)->info(*s.work);
        unsigned char payload[20];
        const uint64_t bytes = uint64_t(s.bytes);
        std::memcpy(payload, &info.width, 4);
        std::memcpy(payload + 4, &info.height, 4);
        std::memcpy(payload + 8, &info.format, 4);
        std::memcpy(payload + 12, &bytes, 8);
        pushEvent(VLQT_RES_EV_INFO, id, payload, 20);
    }

    // At most one WARNING record per resource; every warning is logged.
    void warn(uint64_t id, Slot& s, const std::string& text)
    {
        if (text.empty())
            return;
        qCWarning(lcRes, "%s %s: %s", idText(id).constData(), kindOf(s.kind)->name(), text.c_str());
        if (s.warned || s.hostRefs == 0)
            return;
        s.warned = true;
        pushEvent(VLQT_RES_EV_WARNING, id, text.data(), std::min(int(text.size()), maxErrorText));
    }

    // A decode or upload of the resource runs (first content or replacement).
    static bool busy(const Slot& s) { return s.state == State::Decoding || s.state == State::Uploading || s.next; }
    static bool decoding(const Slot& s) { return s.state == State::Decoding || (s.next && !s.nextUploading); }
    static bool uploading(const Slot& s) { return s.state == State::Uploading || (s.next && s.nextUploading); }

    // New content: Pending/Failed -> Decoding, or a replacement next to the current content of a Ready resource.
    bool begin(uint64_t id, Slot& s, std::shared_ptr<Work> work)
    {
        Kind* kind = kindOf(s.kind);
        if (s.state == State::Ready) {
            s.next = std::move(work);
            s.nextBytes = 0;
            s.nextUploading = false;
            qCDebug(lcRes, "%s: replacement started", idText(id).constData());
            enqueue(id, s, s.next);
            return true;
        }
        if (s.state != State::Pending && s.state != State::Failed) {
            qCWarning(lcRes, "start: %s is %s", idText(id).constData(), stateName(s.state));
            return false;
        }
        if (s.work) // Failed: the old content goes
            kind->destroy(*s.work);
        s.work = std::move(work);
        s.bytes = 0;
        if (!transition(id, s, s.state, State::Decoding))
            return false;
        enqueue(id, s, s.work);
        return true;
    }

    void enqueue(uint64_t id, Slot& s, const std::shared_ptr<Work>& work)
    {
        ensureWorkers();
        s.inWorker = true;
        m_jobs.push_back({id, kindOf(s.kind), work});
        m_jobCv.notify_one();
    }

    void startQueued(uint64_t id, Slot& s)
    {
        if (!s.queued || s.freeQueued || busy(s))
            return;
        std::shared_ptr<Work> work = std::move(s.queued);
        begin(id, s, std::move(work));
    }

    // The replacement failed: its Work becomes the current one (without texture), the resource Failed.
    void failReplacement(uint64_t id, Slot& s, const std::string& error)
    {
        kindOf(s.kind)->destroy(*s.work);
        s.work = std::move(s.next);
        s.bytes = s.nextBytes;
        s.nextBytes = 0;
        s.nextUploading = false;
        fail(id, s, error);
    }

    // Replaces w by a new Work for the current device (Kind::restore); false = nothing to restore, w stays.
    static bool renew(Kind* kind, std::shared_ptr<Work>& w)
    {
        std::shared_ptr<Work> fresh = kind->restore(*w);
        if (!fresh)
            return false;
        kind->destroy(*w);
        w = std::move(fresh);
        return true;
    }

    void maybeQueueFree(uint64_t id, Slot& s)
    {
        if (s.hostRefs > 0 || s.elementRefs > 0 || s.freeQueued)
            return;
        s.freeQueued = true;
        if (s.work)
            s.work->cancel = true;
        if (s.next)
            s.next->cancel = true;
        m_toFree.push_back(id);
    }

    // Main thread, never while a worker holds the Work.
    void free(uint64_t id, Slot& s)
    {
        Kind* kind = kindOf(s.kind);
        for (std::shared_ptr<Work>* w : {&s.work, &s.next, &s.queued})
            if (*w)
                kind->destroy(**w);
        transition(id, s, s.state, State::Released);
        s.work.reset();
        s.next.reset();
        s.queued.reset();
        s.nextUploading = false;
        s.nextBytes = 0;
        s.used = false;
        s.hostRefs = s.elementRefs = 0;
        s.freeQueued = false;
        s.bytes = 0;
        if (++s.generation == 0) // 0 never: an id is never 0
            s.generation = 1;
        m_free.push_back(indexOf(id));
    }

    void pushState(uint64_t id, const Slot& s)
    {
        if (s.hostRefs == 0)
            return;
        const uint8_t state = uint8_t(s.state);
        pushEvent(VLQT_RES_EV_STATE, id, &state, 1);
    }

    void pushEvent(uint16_t ev, uint64_t id, const void* payload, int payloadLen)
    {
        const uint16_t len = uint16_t(8 + payloadLen);
        const qsizetype at = m_events.size();
        m_events.resize(at + ((VLQT_HEADER_SIZE + len + 3) & ~3), '\0');
        char* d = m_events.data() + at;
        std::memcpy(d, &ev, 2);
        std::memcpy(d + 2, &len, 2);
        std::memcpy(d + 4, &id, 8);
        if (payloadLen)
            std::memcpy(d + 12, payload, size_t(payloadLen));
    }

    // Removes the queued records of id (host release).
    void dropEvents(uint64_t id)
    {
        qsizetype read = 0, write = 0;
        const qsizetype n = m_events.size();
        char* d = m_events.data();
        while (read + VLQT_HEADER_SIZE + 8 <= n) {
            uint16_t len;
            uint64_t rid;
            std::memcpy(&len, d + read + 2, 2);
            std::memcpy(&rid, d + read + 4, 8);
            const qsizetype size = (VLQT_HEADER_SIZE + len + 3) & ~3;
            if (rid != id) {
                if (write != read)
                    std::memmove(d + write, d + read, size_t(size));
                write += size;
            }
            read += size;
        }
        m_events.resize(write);
    }

    void ensureWorkers()
    {
        if (!m_threads.empty())
            return;
        int n = qEnvironmentVariableIntValue("VLQT_RES_WORKERS");
        if (n <= 0)
            n = std::clamp(int(std::thread::hardware_concurrency()) - 1, 1, 4);
        n = std::min(n, 16);
        for (int i = 0; i < n; i++)
            m_threads.emplace_back([this, i] { workerLoop(i); });
        qCInfo(lcRes, "%d resource worker(s) started", n);
    }

    // Worker thread: only m_jobs / m_results under the mutex, decode on pure data without it. Never logs.
    void workerLoop(int index)
    {
        char name[16];
        std::snprintf(name, sizeof name, "vlqt-res-%d", index);
        vlqt::setCurrentThreadName(name);
        std::unique_lock lock(m_mutex);
        for (;;) {
            m_jobCv.wait(lock, [this] { return m_stop || !m_jobs.empty(); });
            if (m_stop)
                return;
            Job job = std::move(m_jobs.front());
            m_jobs.pop_front();
            lock.unlock();
            std::string error = runDecode(job);
            job.work.reset(); // the main thread owns the Work again
            lock.lock();
            if (m_stop)
                return; // shutdown frees everything itself
            m_results.push_back({job.id, std::move(error)});
            m_resultCv.notify_all();
        }
    }

    std::mutex m_mutex;
    std::condition_variable m_jobCv, m_resultCv;
    std::vector<Slot> m_slots;
    std::vector<uint32_t> m_free;
    std::deque<Job> m_jobs;
    std::deque<Result> m_results;
    std::deque<uint64_t> m_uploads;
    std::vector<uint64_t> m_toFree;
    std::vector<std::thread> m_threads;
    QByteArray m_events;
    int64_t m_budget = defaultBudget;
    int64_t m_usedLastFrame = 0; // upload bytes of the last beginFrame
    bool m_stop = false;
};

// Created at the first use and never deleted (like g_app): workers may still run while the process ends.
std::atomic<Manager*> s_manager{nullptr};

Manager& manager()
{
    Manager* m = s_manager.load();
    if (!m) {
        static std::once_flag once;
        std::call_once(once, [] {
            s_manager.store(new Manager());
            device::addListener({[] { s_manager.load()->deviceLost(); }, [] { s_manager.load()->deviceRestored(); }});
        });
        m = s_manager.load();
    }
    return *m;
}

} // namespace

const char* stateName(State s)
{
    switch (s) {
    case State::Pending:   return "Pending";
    case State::Decoding:  return "Decoding";
    case State::Uploading: return "Uploading";
    case State::Ready:     return "Ready";
    case State::Failed:    return "Failed";
    case State::Released:  return "Released";
    }
    return "?";
}

int create(int kind, uint64_t* outId) { return manager().create(kind, outId); }
int release(uint64_t id) { return manager().release(id); }
int setBudget(int bytes) { return manager().setBudget(bytes); }
int poll(unsigned char* out, int cap, int* written, int* required) { return manager().poll(out, cap, written, required); }
int wait(uint64_t id, int timeoutMs) { return manager().wait(id, timeoutMs); }
int list(void* out, int cap) { return manager().list(static_cast<vlqt_res_info*>(out), std::max(0, cap)); }
int start(uint64_t id, std::shared_ptr<Work> work, int kind) { return manager().start(id, std::move(work), kind); }
std::shared_ptr<Work> readyWork(uint64_t id, int kind) { return manager().readyWork(id, kind); }
int kindOfId(uint64_t id, const char* what) { return manager().kindOfId(id, what); }
bool isFailed(uint64_t id) { return manager().isFailed(id); }
int beginExternal(uint64_t id, int kind) { return manager().beginExternal(id, kind); }
int finishExternal(uint64_t id, std::shared_ptr<Work> work, int64_t bytes, const std::string& error)
{
    return manager().finishExternal(id, std::move(work), bytes, error);
}
void snapshot(std::vector<Row>& rows, Overview& out) { manager().snapshot(rows, out); }
uint64_t contentEpoch() { return s_contentEpoch.load(std::memory_order_relaxed); }
uint64_t failureEpoch() { return s_failureEpoch.load(std::memory_order_relaxed); }

int acquire(uint64_t id) { return manager().acquire(id); }
int releaseElement(uint64_t id) { return manager().releaseElement(id); }

void shutdown()
{
    snap::shutdown(); // running grabs first: they report into the manager
    if (Manager* m = s_manager.load())
        m->shutdown();
}

void beginFrame()
{
    if (Manager* m = s_manager.load()) // nothing to do before the first resource
        m->beginFrame();
}

void endFrame()
{
    snap::endFrame(); // finished / hanging grabs -> Ready or Failed, before released resources are freed
    if (Manager* m = s_manager.load())
        m->endFrame();
}

int testTransition(uint64_t id, int from, int to) { return manager().testTransition(id, from, to); }

} // namespace vlqt::res