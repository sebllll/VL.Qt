// Test export of vlqt_test.h: the resource kind VLQT_RESOURCE_KIND_TEST (decode sleeps, upload consumes budget in stripes).
// No production kind has deterministic timings, so the SmokeTest cannot test the state machine, the budget and the
// cancellation without it.
#include "../vlqt_test.h"
#include "../app/exports.h"
#include "resources/resources.h"

#include <QtCore/QLoggingCategory>

#include <algorithm>
#include <chrono>
#include <memory>
#include <string>
#include <thread>

using vlqt::guarded;

namespace vlqt::res {

namespace {

struct TestContent final : Content
{
    int decodeMs = 0;
    int64_t remaining = 0; // bytes still to upload
    bool failDecode = false, failUpload = false;
};

class TestKind final : public Kind
{
public:
    const char* name() const override { return "test"; }

    std::string decode(Content& work) override
    {
        auto& w = static_cast<TestContent&>(work);
        const auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(w.decodeMs);
        while (std::chrono::steady_clock::now() < end) {
            if (w.cancel.load())
                return "cancelled";
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
        return w.failDecode ? "test: decode failed (requested)" : std::string();
    }

    int64_t upload(Content& work, int64_t budget, UploadStatus& status, std::string& error) override
    {
        auto& w = static_cast<TestContent&>(work);
        if (w.failUpload) {
            error = "test: upload failed (requested)";
            return 0;
        }
        const int64_t used = budget < 0 ? w.remaining : std::min(w.remaining, budget);
        w.remaining -= used;
        status = w.remaining == 0 ? UploadStatus::Done : UploadStatus::Progress;
        return used;
    }

    int64_t pendingBytes(const Content& work) const override { return static_cast<const TestContent&>(work).remaining; }
};

} // namespace

Kind* testKind()
{
    static TestKind s_test;
    return &s_test;
}

} // namespace vlqt::res

extern "C" {

VLQT_API int vlqt_test_res_start(unsigned long long id, int decodeMs, long long uploadBytes, int flags)
{
    return guarded([&] {
        if (decodeMs < 0 || uploadBytes < 0) {
            qCWarning(vlqt::res::lcRes, "test start: negative argument (%d ms, %lld bytes)", decodeMs, uploadBytes);
            return vlqt::res::badValueRc;
        }
        auto work = std::make_shared<vlqt::res::TestContent>();
        work->decodeMs = decodeMs;
        work->remaining = uploadBytes;
        work->failDecode = (flags & 1) != 0;
        work->failUpload = (flags & 2) != 0;
        return vlqt::res::start(id, std::move(work), 0);
    });
}

} // extern "C"
