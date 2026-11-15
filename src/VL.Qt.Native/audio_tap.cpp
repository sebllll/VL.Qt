#include "audio_tap.h"

#include "elements.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QLoggingCategory>

#include <chrono>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>

namespace vlqt::res::audio {

namespace {

Q_LOGGING_CATEGORY(lcTap, "VL.Qt.Resources")

static_assert(sizeof(vlqt_audio_tap_state) == 96, "vlqt_audio_tap_state is mirrored in Native.cs (96 bytes)");

// Ring length in ms (protocol.h: about 500 ms).
constexpr int ringMs = 500;

// Registry id -> tap, readable from the audio thread without the manager's mutex (see audio_tap.h). The main thread
// holds the mutex only to insert (init) and erase (destroy).
// TODO(T4): a reader may wait here while the main thread inserts (allocation under the lock). Keep it if the lock is
//   never contended in practice (one insert per tap), else replace it by a fixed table of atomic shared_ptr slots.
std::mutex s_mutex;
std::unordered_map<uint64_t, std::shared_ptr<TapWork>> s_taps;

std::shared_ptr<TapWork> findTap(uint64_t id)
{
    std::lock_guard lock(s_mutex);
    const auto it = s_taps.find(id);
    return it == s_taps.end() ? nullptr : it->second;
}

class AudioTapKind final : public Kind
{
public:
    const char* name() const override { return "audio tap"; }
    // External content (init on the main thread): never decoded or uploaded.
    std::string decode(Work&) override { return "audio tap: no decode"; }
    int64_t upload(Work&, int64_t, bool& done, std::string& error) override
    {
        done = true;
        error = "audio tap: no upload";
        return 0;
    }
    void destroy(Work& work) override
    {
        auto& tap = static_cast<TapWork&>(work);
        // TODO(T4): detach from a player that still writes (normally the element reference keeps the tap alive until
        //   the element detached or was destroyed, so nothing writes any more here).
        std::lock_guard lock(s_mutex);
        s_taps.erase(tap.id);
        qCDebug(lcTap, "audio tap %llx destroyed", static_cast<unsigned long long>(tap.id));
    }
    Info info(const Work& work) const override
    {
        const auto& tap = static_cast<const TapWork&>(work);
        return {static_cast<uint32_t>(tap.channels), static_cast<uint32_t>(tap.sampleRate), 0};
    }
};

void fillState(const TapWork& tap, vlqt_audio_tap_state& s)
{
    std::memset(&s, 0, sizeof s);
    s.now_ns = nowNs();
    s.channels = tap.channels;
    s.sample_rate = tap.sampleRate;
    s.capacity = tap.capacity;
    s.discontinuity = tap.discontinuity.load(std::memory_order_acquire);
    s.overrun_frames = tap.overrunFrames.load(std::memory_order_relaxed);
    s.underrun_frames = tap.underrunFrames.load(std::memory_order_relaxed);
    s.flags = tap.flags.load(std::memory_order_relaxed);
    // TODO(T4): due_ns, media_us, rate of the first frame, available, video_media_us / video_ns from the ring.
}

} // namespace

Kind* kind()
{
    static AudioTapKind s_kind;
    return &s_kind;
}

int init(uint64_t id, int channels, int sampleRate)
{
    if (channels < 1 || channels > VLQT_AUDIO_TAP_MAX_CHANNELS || sampleRate < VLQT_AUDIO_TAP_MIN_SAMPLE_RATE
        || sampleRate > VLQT_AUDIO_TAP_MAX_SAMPLE_RATE) {
        qCWarning(lcTap, "audio tap %llx: bad format (%d channels, %d Hz)", static_cast<unsigned long long>(id), channels,
            sampleRate);
        return badValueRc;
    }
    // Once per tap: the format never changes (a new sample rate = a new tap).
    if (findTap(id)) {
        qCWarning(lcTap, "audio tap %llx: already initialised", static_cast<unsigned long long>(id));
        return badValueRc;
    }
    const int rc = beginExternal(id, VLQT_RES_KIND_AUDIO_TAP);
    if (rc != okRc)
        return rc;
    auto tap = std::make_shared<TapWork>();
    tap->id = id;
    tap->channels = channels;
    tap->sampleRate = sampleRate;
    tap->capacity = static_cast<int>(static_cast<int64_t>(sampleRate) * ringMs / 1000);
    // TODO(T4): allocate the ring (capacity * channels floats + segment records) here, never on the producer thread.
    {
        std::lock_guard lock(s_mutex);
        s_taps[id] = tap;
    }
    const int64_t bytes = static_cast<int64_t>(tap->capacity) * channels * static_cast<int64_t>(sizeof(float));
    const int finished = finishExternal(id, tap, bytes, {});
    if (finished != okRc) { // the manager dropped it (cannot happen on the main thread between begin and finish)
        std::lock_guard lock(s_mutex);
        s_taps.erase(id);
        return finished;
    }
    qCDebug(lcTap, "audio tap %llx: %d channels, %d Hz, %d frames", static_cast<unsigned long long>(id), channels,
        sampleRate, tap->capacity);
    return okRc;
}

int read(uint64_t id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state)
{
    // Silent on unknown ids: the reader polls a tap the main thread may have released (protocol.h).
    const auto tap = findTap(id);
    if (!tap)
        return staleIdRc;
    if (state)
        fillState(*tap, *state);
    if (maxFrames < 0 || channels != tap->channels)
        return badValueRc;
    (void)dst;
    // TODO(T4): deliver frames of one segment (protocol.h "Reader"):
    //   - drop frames of segments older than the newest discontinuity first;
    //   - dst != NULL: copy min(maxFrames, available in the segment) frames, advance the read index; a shortfall
    //     against maxFrames adds to underrunFrames (only when the tap is ATTACHED and PLAYING);
    //   - dst == NULL: drop up to maxFrames frames of the segment, no underrun;
    //   - fill state for the first frame moved (due_ns = emitTime + k * 1e9 / (sampleRate * rate), media_us =
    //     startTime + k * 1e6 / sampleRate), available after the call.
    return 0;
}

long long nowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

bool setVideoTap(Elements&, Node& node, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.u != 0) {
        const auto work = std::static_pointer_cast<TapWork>(readyWork(v.u, VLQT_RES_KIND_AUDIO_TAP));
        if (!work) {
            err = "not a Ready audio tap";
            return false;
        }
    }
    (void)node;
    // TODO(T4): attach / detach on the item's player (property alias vlqtPlayer in kVideoQml -> QMediaPlayer*,
    //   vlqtSink or VideoOutput.videoSink -> QVideoSink*), never on the QQuickWindow (#118 swaps the window):
    //   - id != 0: element reference on the new tap (acquire), QAudioBufferOutput(QAudioFormat Float, channels,
    //     sampleRate) parented to the player, audioBufferReceived -> ring with Qt::DirectConnection, player.audioOutput
    //     = null (Qt silent), discontinuity on attach, flags ATTACHED (+ PLAYING from playbackStateChanged);
    //   - the same tap already attached to another player: warning, ignore (rc true, protocol.h);
    //   - a different tap: switch without a moment of Qt sound (detach old, attach new in one call), releaseElement
    //     on the old one;
    //   - 0: detach, restore audioOutput (the AudioOutput of the wrapper), releaseElement;
    //   - DESTROY of the element: detach and releaseElement (e.g. a QObject child of the item whose destructor does it);
    //   - videoFrameChanged of the sink (DirectConnection) -> diagnostics video_media_us / video_ns.
    return true;
}

} // namespace vlqt::res::audio
