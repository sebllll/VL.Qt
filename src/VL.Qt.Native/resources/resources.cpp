#include "resources/resources.h"

#include "resources/resource_manager.h"
#include "snapshot_resource.h"
#include "vlqt.h"

#include <algorithm>

namespace vlqt::res {

Q_LOGGING_CATEGORY(lcRes, "VL.Qt.Resources")

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
int start(uint64_t id, std::shared_ptr<Content> work, int kind) { return manager().start(id, std::move(work), kind); }
std::shared_ptr<Content> readyContent(uint64_t id, int kind) { return manager().readyContent(id, kind); }
int kindOfId(uint64_t id, const char* what) { return manager().kindOfId(id, what); }
bool isFailed(uint64_t id) { return manager().isFailed(id); }
int beginExternal(uint64_t id, int kind) { return manager().beginExternal(id, kind); }
int finishExternal(uint64_t id, std::shared_ptr<Content> work, int64_t bytes, const std::string& error)
{
    return manager().finishExternal(id, std::move(work), bytes, error);
}
void overview(std::vector<OverviewRow>& rows, OverviewTotals& out) { manager().overview(rows, out); }

int acquire(uint64_t id) { return manager().acquire(id); }
int releaseElement(uint64_t id) { return manager().releaseElement(id); }

void shutdown()
{
    snapshot::shutdown(); // running grabs first: they report into the manager
    if (Manager* m = existingManager())
        m->shutdown();
}

void beginFrame()
{
    if (Manager* m = existingManager()) // nothing to do before the first resource
        m->beginFrame();
}

void endFrame()
{
    snapshot::endFrame(); // finished / hanging grabs -> Ready or Failed, before released resources are freed
    if (Manager* m = existingManager())
        m->endFrame();
}

int testTransition(uint64_t id, int from, int to) { return manager().testTransition(id, from, to); }

} // namespace vlqt::res
