#include "resources/resource_manager.h"

#include "app/exports.h"
#include "device.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QByteArray>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <thread>

namespace vlqt::res {

namespace {

// Counts content changes of Ready resources (first Ready, replacement swap); see contentEpoch.
std::atomic<uint64_t> s_contentEpoch{0};
// Counts transitions to Failed; see failureEpoch.
std::atomic<uint64_t> s_failureEpoch{0};

constexpr uint32_t maxSlots = 1u << 20;      // ~1M live resources; more is a leak in the host

bool validState(int s) { return s >= int(State::Pending) && s <= int(State::Released); }

// Kinds whose content is made on the main thread without worker and upload (beginExternal / finishExternal).
bool externalKind(int kind) { return kind == VLQT_RESOURCE_KIND_SNAPSHOT || kind == VLQT_RESOURCE_KIND_AUDIO_TAP; }

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

// Created at the first use and never deleted (like g_app): workers may still run while the process ends.
std::atomic<Manager*> s_manager{nullptr};

} // namespace

int Manager::create(int kind, uint64_t* outId)
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
    s.replacement.reset();
    s.waiting.reset();
    s.replacementUploading = s.warned = false;
    s.replacementBytes = 0;
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

int Manager::release(uint64_t id)
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
    m_events.drop(id); // no event for the id any more, also not one queued before the release
    qCDebug(lcRes, "%s: host release (%s, %d element refs)", idText(id).constData(), stateName(s->state),
        s->elementRefs);
    maybeQueueFree(id, *s);
    return okRc;
}

int Manager::setBudget(int bytes)
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

int Manager::poll(unsigned char* out, int cap, int* written, int* required)
{
    if (const int rc = beginBuffer(out, cap, written, required); rc != okRc)
        return rc;
    std::lock_guard lock(m_mutex);
    return m_events.poll(out, cap, written, required);
}

int Manager::start(uint64_t id, std::shared_ptr<Content> work, int kind)
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
        if (s->waiting)
            kindOf(s->kind)->destroy(*s->waiting);
        s->waiting = std::move(work);
        qCDebug(lcRes, "%s: new content queued (%s)", idText(id).constData(), stateName(s->state));
        return okRc;
    }
    return begin(id, *s, std::move(work)) ? okRc : badValueRc;
}

std::shared_ptr<Content> Manager::readyContent(uint64_t id, int kind)
{
    std::lock_guard lock(m_mutex);
    Slot* s = find(id, nullptr);
    return s && s->kind == kind && s->state == State::Ready ? s->work : nullptr;
}

bool Manager::isFailed(uint64_t id)
{
    std::lock_guard lock(m_mutex);
    Slot* s = find(id, nullptr);
    return s && s->state == State::Failed;
}

int Manager::kindOfId(uint64_t id, const char* what)
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

int Manager::beginExternal(uint64_t id, int kind)
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

int Manager::finishExternal(uint64_t id, std::shared_ptr<Content> work, int64_t bytes, const std::string& error)
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

int Manager::wait(uint64_t id, int timeoutMs)
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
        Job job{};
        if (m_pool.takeQueued(id, job)) {
            lock.unlock();
            Result r{id, runDecode(job)};
            lock.lock();
            m_pool.putResult(std::move(r));
        }
        Result r;
        if (!m_pool.waitFor(lock, id, deadline, r)) {
            s = find(id, nullptr);
            qCWarning(lcRes, "wait: %s timed out after %d ms (%s)", idText(id).constData(), timeoutMs,
                s ? stateName(s->state) : "gone");
            return s ? int(s->state) : staleIdRc;
        }
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

int Manager::list(vlqt_res_info* out, int cap)
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

void Manager::shutdown()
{
    std::vector<std::thread> threads;
    {
        std::lock_guard lock(m_mutex);
        threads = m_pool.stop();
        for (Slot& s : m_slots)
            for (Content* w : {s.work.get(), s.replacement.get()})
                if (s.used && w)
                    w->cancel = true;
    }
    m_pool.join(threads);

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
    m_pool.reset();
    m_uploads.clear();
    m_toFree.clear();
    m_events.clear();
}

int Manager::acquire(uint64_t id)
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

int Manager::releaseElement(uint64_t id)
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

void Manager::beginFrame()
{
    std::lock_guard lock(m_mutex);
    Result r;
    while (m_pool.popResult(r))
        handleResult(r);
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

void Manager::overview(std::vector<OverviewRow>& rows, OverviewTotals& out)
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
        const Content* w = s->replacement ? s->replacement.get() : s->work.get(); // a replacement uploads its own content
        if (!w)
            continue;
        out.uploadJobs++;
        out.uploadBytes += kindOf(s->kind)->pendingBytes(*w);
    }
    out.decodeJobs = m_pool.queuedJobs();
}

void Manager::endFrame()
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

void Manager::deviceLost()
{
    std::lock_guard lock(m_mutex);
    for (Slot& s : m_slots) {
        if (!s.used)
            continue;
        Kind* kind = kindOf(s.kind);
        const Content* inWorker = s.inWorker ? (s.replacement ? s.replacement.get() : s.work.get()) : nullptr;
        for (Content* w : {s.work.get(), s.replacement.get(), s.waiting.get()})
            if (w && w != inWorker)
                kind->releaseDevice(*w);
    }
}

void Manager::deviceRestored()
{
    std::lock_guard lock(m_mutex);
    int restarted = 0;
    for (size_t i = 1; i < m_slots.size(); i++) {
        Slot& s = m_slots[i];
        if (!s.used || s.freeQueued)
            continue;
        const uint64_t id = makeId(uint32_t(i), s.generation);
        Kind* kind = kindOf(s.kind);
        if (s.waiting)
            renew(kind, s.waiting);
        if (s.inWorker) // handleResult restarts it (stale)
            continue;
        if (s.replacement) { // replacement in its upload: start it over
            if (!renew(kind, s.replacement))
                continue;
            m_uploads.erase(std::remove(m_uploads.begin(), m_uploads.end(), id), m_uploads.end());
            s.replacementUploading = false;
            s.replacementBytes = 0;
            enqueue(id, s, s.replacement);
        } else if (s.state == State::Ready) { // replacement, the state stays Ready
            std::shared_ptr<Content> fresh = kind->restore(*s.work);
            if (!fresh)
                continue;
            s.replacement = std::move(fresh);
            s.replacementBytes = 0;
            s.replacementUploading = false;
            enqueue(id, s, s.replacement);
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

int Manager::testTransition(uint64_t id, int from, int to)
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

Slot* Manager::find(uint64_t id, const char* what)
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

bool Manager::transition(uint64_t id, Slot& s, State from, State to, bool restart)
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

void Manager::fail(uint64_t id, Slot& s, const std::string& error)
{
    if (!transition(id, s, s.state, State::Failed))
        return;
    qCWarning(lcRes, "%s %s failed: %s", idText(id).constData(), kindOf(s.kind)->name(), error.c_str());
    if (s.hostRefs > 0) {
        m_events.push(VLQT_RESOURCE_EVENT_ERROR, id, error.data(), int(error.size()));
    }
}

void Manager::handleResult(const Result& r)
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
    if (std::shared_ptr<Content>& decoded = s->replacement ? s->replacement : s->work; decoded && kind->stale(*decoded)
        && renew(kind, decoded)) {
        qCDebug(lcRes, "%s: decode for the old device dropped, decoding again", idText(r.id).constData());
        enqueue(r.id, *s, decoded);
        return;
    }
    if (!r.error.empty()) {
        if (s->replacement)
            failReplacement(r.id, *s, r.error);
        else
            fail(r.id, *s, r.error);
        startQueued(r.id, *s);
        return;
    }
    warn(r.id, *s, kind->takeWarning(s->replacement ? *s->replacement : *s->work));
    if (s->replacement) {
        s->replacementUploading = true;
        m_uploads.push_back(r.id);
    } else if (transition(r.id, *s, State::Decoding, State::Uploading)) {
        m_uploads.push_back(r.id);
    }
}

int64_t Manager::uploadStep(uint64_t id, Slot& s, int64_t budget)
{
    Kind* kind = kindOf(s.kind);
    const bool replacing = s.replacement != nullptr;
    bool done = false;
    std::string error;
    const int64_t used = std::max<int64_t>(0, kind->upload(replacing ? *s.replacement : *s.work, budget, done, error));
    (replacing ? s.replacementBytes : s.bytes) += used;
    if (!error.empty()) {
        if (replacing)
            failReplacement(id, s, error);
        else
            fail(id, s, error);
        startQueued(id, s);
    } else if (done) {
        if (replacing) { // swap: the old content is visible up to here
            kind->destroy(*s.work);
            s.work = std::move(s.replacement);
            s.bytes = s.replacementBytes;
            s.replacementBytes = 0;
            s.replacementUploading = false;
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

void Manager::pushInfo(uint64_t id, const Slot& s)
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
    m_events.push(VLQT_RESOURCE_EVENT_INFO, id, payload, 20);
}

void Manager::warn(uint64_t id, Slot& s, const std::string& text)
{
    if (text.empty())
        return;
    qCWarning(lcRes, "%s %s: %s", idText(id).constData(), kindOf(s.kind)->name(), text.c_str());
    if (s.warned || s.hostRefs == 0)
        return;
    s.warned = true;
    m_events.push(VLQT_RESOURCE_EVENT_WARNING, id, text.data(), int(text.size()));
}

bool Manager::begin(uint64_t id, Slot& s, std::shared_ptr<Content> work)
{
    Kind* kind = kindOf(s.kind);
    if (s.state == State::Ready) {
        s.replacement = std::move(work);
        s.replacementBytes = 0;
        s.replacementUploading = false;
        qCDebug(lcRes, "%s: replacement started", idText(id).constData());
        enqueue(id, s, s.replacement);
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

void Manager::enqueue(uint64_t id, Slot& s, const std::shared_ptr<Content>& work)
{
    s.inWorker = true;
    m_pool.submit(id, kindOf(s.kind), work);
}

void Manager::startQueued(uint64_t id, Slot& s)
{
    if (!s.waiting || s.freeQueued || busy(s))
        return;
    std::shared_ptr<Content> work = std::move(s.waiting);
    begin(id, s, std::move(work));
}

void Manager::failReplacement(uint64_t id, Slot& s, const std::string& error)
{
    kindOf(s.kind)->destroy(*s.work);
    s.work = std::move(s.replacement);
    s.bytes = s.replacementBytes;
    s.replacementBytes = 0;
    s.replacementUploading = false;
    fail(id, s, error);
}

bool Manager::renew(Kind* kind, std::shared_ptr<Content>& w)
{
    std::shared_ptr<Content> fresh = kind->restore(*w);
    if (!fresh)
        return false;
    kind->destroy(*w);
    w = std::move(fresh);
    return true;
}

void Manager::maybeQueueFree(uint64_t id, Slot& s)
{
    if (s.hostRefs > 0 || s.elementRefs > 0 || s.freeQueued)
        return;
    s.freeQueued = true;
    if (s.work)
        s.work->cancel = true;
    if (s.replacement)
        s.replacement->cancel = true;
    m_toFree.push_back(id);
}

void Manager::free(uint64_t id, Slot& s)
{
    Kind* kind = kindOf(s.kind);
    for (std::shared_ptr<Content>* w : {&s.work, &s.replacement, &s.waiting})
        if (*w)
            kind->destroy(**w);
    transition(id, s, s.state, State::Released);
    s.work.reset();
    s.replacement.reset();
    s.waiting.reset();
    s.replacementUploading = false;
    s.replacementBytes = 0;
    s.used = false;
    s.hostRefs = s.elementRefs = 0;
    s.freeQueued = false;
    s.bytes = 0;
    if (++s.generation == 0) // 0 never: an id is never 0
        s.generation = 1;
    m_free.push_back(indexOf(id));
}

void Manager::pushState(uint64_t id, const Slot& s)
{
    if (s.hostRefs == 0)
        return;
    const uint8_t state = uint8_t(s.state);
    m_events.push(VLQT_RESOURCE_EVENT_STATE, id, &state, 1);
}

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

Manager* existingManager() { return s_manager.load(); }

uint64_t contentEpoch() { return s_contentEpoch.load(std::memory_order_relaxed); }
uint64_t failureEpoch() { return s_failureEpoch.load(std::memory_order_relaxed); }

} // namespace vlqt::res
