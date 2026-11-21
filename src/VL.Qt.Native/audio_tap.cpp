#include "audio_tap.h"

#include "elements.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QLoggingCategory>
#include <QtCore/QPointer>
#include <QtMultimedia/QAudioBuffer>
#include <QtMultimedia/QAudioBufferOutput>
#include <QtMultimedia/QAudioFormat>
#include <QtMultimedia/QAudioOutput>
#include <QtMultimedia/QMediaPlayer>
#include <QtMultimedia/QVideoFrame>
#include <QtMultimedia/QVideoSink>
#include <QtQuick/QQuickItem>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <utility>

namespace vlqt::res::audio {

namespace {

static_assert(std::atomic<double>::is_always_lock_free && std::atomic<uint64_t>::is_always_lock_free,
    "the audio thread must not take a lock");

// Ring length in ms (protocol.h: about 500 ms).
constexpr int ringMs = 500;
// A buffer whose startTime is this far from the end of the previous one starts a new segment (source change, seek).
// Qt's buffers are contiguous to the microsecond, also across a loop wrap.
constexpr long long mediaJumpUs = 50'000;
// A buffer emitted this much later than the previous one ended (wall time) starts a new segment (stall, pause).
constexpr long long wallGapNs = 150'000'000;

// Registry id -> tap, readable from the audio thread without the manager's mutex (see audio_tap.h). The main thread
// holds the mutex only to insert (init) and erase (destroy), never while allocating ring memory, so a reader waits at
// most for a hash map insert, once per tap.
std::mutex s_mutex;
std::unordered_map<uint64_t, std::shared_ptr<TapContent>> s_taps;

std::shared_ptr<TapContent> findTap(uint64_t id)
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
    std::string decode(Content&) override { return "audio tap: no decode"; }
    int64_t upload(Content&, int64_t, bool& done, std::string& error) override
    {
        done = true;
        error = "audio tap: no upload";
        return 0;
    }
    void destroy(Content& work) override
    {
        auto& tap = static_cast<TapContent&>(work);
        // The element reference keeps the tap alive while a player is attached, so nothing writes any more here; a
        // QAudioBufferOutput still being deleted ignores the tap by its generation.
        tap.attachGen.fetch_add(1, std::memory_order_acq_rel);
        std::lock_guard lock(s_mutex);
        s_taps.erase(tap.id);
        qCDebug(lcRes, "audio tap %llx destroyed", static_cast<unsigned long long>(tap.id));
    }
    Info info(const Content& work) const override
    {
        const auto& tap = static_cast<const TapContent&>(work);
        return {static_cast<uint32_t>(tap.channels), static_cast<uint32_t>(tap.sampleRate), 0};
    }
};

// --- Producer (player worker thread: no QObjects, no locks, no allocation) ---

void copyIn(TapContent& t, uint64_t frame, const float* src, int frames)
{
    const int at = static_cast<int>(frame % static_cast<uint64_t>(t.capacity));
    const int first = std::min(frames, t.capacity - at);
    std::memcpy(t.samples.get() + static_cast<size_t>(at) * t.channels, src, sizeof(float) * first * t.channels);
    if (first < frames)
        std::memcpy(t.samples.get(), src + static_cast<size_t>(first) * t.channels, sizeof(float) * (frames - first) * t.channels);
}

void produce(TapContent& t, uint32_t gen, const QAudioBuffer& buffer)
{
    const long long emitNs = nowNs();
    const QAudioFormat fmt = buffer.format();
    const int frames = static_cast<int>(buffer.frameCount());
    if (frames <= 0 || fmt.sampleFormat() != QAudioFormat::Float || fmt.channelCount() != t.channels
        || fmt.sampleRate() != t.sampleRate)
        return;
    if (t.writing.exchange(true, std::memory_order_acquire))
        return; // another thread writes (an old output during a swap): never two producers
    if (t.attachGen.load(std::memory_order_acquire) != gen) {
        t.writing.store(false, std::memory_order_release);
        return;
    }
    const double rate = t.rate.load(std::memory_order_relaxed);
    const long long mediaUs = buffer.startTime();
    bool fresh = t.newSegment.exchange(false, std::memory_order_acq_rel) || !t.hasLast || t.lastDropped || rate != t.lastRate;
    if (!fresh) {
        const long long mediaGap = mediaUs - t.lastMediaEndUs;
        fresh = mediaGap > mediaJumpUs || mediaGap < -mediaJumpUs || emitNs - t.lastEmitEndNs > wallGapNs;
    }
    t.hasLast = true;
    t.lastRate = rate;
    t.lastMediaEndUs = mediaUs + static_cast<long long>(frames) * 1'000'000 / t.sampleRate;
    t.lastEmitEndNs = emitNs + static_cast<long long>(frames * 1e9 / (t.sampleRate * rate));

    const uint64_t records = t.recordWrite.load(std::memory_order_relaxed);
    const uint64_t used = t.writeFrame - t.readFrame.load(std::memory_order_acquire);
    if (used + frames > static_cast<uint64_t>(t.capacity)
        || records - t.recordRead.load(std::memory_order_acquire) >= TapContent::recordCapacity) {
        t.overrunFrames.fetch_add(static_cast<uint64_t>(frames), std::memory_order_relaxed);
        t.lastDropped = true; // the next buffer starts a new segment
        t.writing.store(false, std::memory_order_release);
        return;
    }
    t.lastDropped = false;
    if (fresh)
        t.segment = t.discontinuity.fetch_add(1, std::memory_order_acq_rel) + 1;
    copyIn(t, t.writeFrame, buffer.constData<float>(), frames);
    Segment& s = t.records[records % TapContent::recordCapacity];
    s = {t.writeFrame, frames, t.segment, emitNs, mediaUs, rate};
    t.writeFrame += static_cast<uint64_t>(frames);
    t.recordWrite.store(records + 1, std::memory_order_release);
    t.writing.store(false, std::memory_order_release);
}

void storeVideoFrame(TapContent& t, long long mediaUs)
{
    const uint32_t seq = t.videoSeq.load(std::memory_order_relaxed);
    t.videoSeq.store(seq + 1, std::memory_order_relaxed);
    std::atomic_thread_fence(std::memory_order_release);
    t.videoMediaUs.store(mediaUs, std::memory_order_relaxed);
    t.videoNs.store(nowNs(), std::memory_order_relaxed);
    t.videoSeq.store(seq + 2, std::memory_order_release);
}

// --- Reader (any thread, allocation free) ---

void fillState(const TapContent& tap, vlqt_audio_tap_state& s)
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
    for (int i = 0; i < 4; ++i) {
        const uint32_t seq = tap.videoSeq.load(std::memory_order_acquire);
        if (seq & 1)
            continue;
        const long long media = tap.videoMediaUs.load(std::memory_order_relaxed);
        const long long at = tap.videoNs.load(std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_acquire);
        if (tap.videoSeq.load(std::memory_order_relaxed) == seq) {
            s.video_media_us = media;
            s.video_ns = at;
            break;
        }
    }
}

// The first frame of a call: due time, media time, rate and segment of frame offset k of record s.
void describe(const TapContent& t, const Segment& s, uint64_t k, vlqt_audio_tap_state& state)
{
    state.due_ns = s.emitNs + static_cast<long long>(static_cast<double>(k) * 1e9 / (t.sampleRate * s.rate));
    state.media_us = s.mediaUs + static_cast<long long>(k * 1'000'000 / static_cast<uint64_t>(t.sampleRate));
    state.rate = s.rate;
    state.discontinuity = s.discontinuity;
}

int readRing(TapContent& t, float* dst, int maxFrames, vlqt_audio_tap_state& state)
{
    uint64_t rec = t.recordRead.load(std::memory_order_relaxed);
    uint64_t frame = t.readFrame.load(std::memory_order_relaxed);
    const uint64_t published = t.recordWrite.load(std::memory_order_acquire);
    const auto record = [&](uint64_t i) -> const Segment& { return t.records[i % TapContent::recordCapacity]; };

    // Flush: frames of segments older than the newest one go before new ones are delivered.
    if (rec < published) {
        const uint32_t newest = record(published - 1).discontinuity;
        while (rec < published && record(rec).discontinuity != newest) {
            frame = record(rec).startFrame + static_cast<uint64_t>(record(rec).frames);
            ++rec;
        }
    }

    int moved = 0;
    bool described = false;
    uint32_t segment = 0;
    while (moved < maxFrames && rec < published) {
        const Segment& s = record(rec);
        if (described && s.discontinuity != segment)
            break; // one segment per call
        const uint64_t offset = frame - s.startFrame;
        if (!described) {
            describe(t, s, offset, state);
            segment = s.discontinuity;
            described = true;
        }
        const int left = s.frames - static_cast<int>(offset);
        const int n = std::min(left, maxFrames - moved);
        if (dst) {
            const int at = static_cast<int>(frame % static_cast<uint64_t>(t.capacity));
            const int first = std::min(n, t.capacity - at);
            float* out = dst + static_cast<size_t>(moved) * t.channels;
            std::memcpy(out, t.samples.get() + static_cast<size_t>(at) * t.channels, sizeof(float) * first * t.channels);
            if (first < n)
                std::memcpy(out + static_cast<size_t>(first) * t.channels, t.samples.get(), sizeof(float) * (n - first) * t.channels);
        }
        moved += n;
        frame += static_cast<uint64_t>(n);
        if (n == left)
            ++rec;
    }
    t.readFrame.store(frame, std::memory_order_release);
    t.recordRead.store(rec, std::memory_order_release);

    // Nothing moved: describe the next frame in the ring.
    if (!described && rec < published) {
        describe(t, record(rec), frame - record(rec).startFrame, state);
        segment = record(rec).discontinuity;
        described = true;
    }
    int available = 0;
    if (described) {
        for (uint64_t i = rec; i < published && record(i).discontinuity == segment; ++i)
            available += record(i).frames - static_cast<int>(i == rec ? frame - record(i).startFrame : 0);
    }
    state.available = available;

    // Underrun: a copying read that the ring could not fill while a playing player feeds the tap.
    constexpr int feeding = VLQT_AUDIO_TAP_ATTACHED | VLQT_AUDIO_TAP_PLAYING;
    if (dst && moved < maxFrames && rec == published && (t.flags.load(std::memory_order_relaxed) & feeding) == feeding)
        t.underrunFrames.fetch_add(static_cast<uint64_t>(maxFrames - moved), std::memory_order_relaxed);
    state.underrun_frames = t.underrunFrames.load(std::memory_order_relaxed);
    return moved;
}

// --- Attach (main thread) ---

constexpr const char* attachmentName = "vlqtAudioTap";

void setFlag(TapContent& t, int flag, bool on)
{
    if (on)
        t.flags.fetch_or(flag, std::memory_order_relaxed);
    else
        t.flags.fetch_and(~flag, std::memory_order_relaxed);
}

// QObject child of a Video item: the tap the item's player writes into. Destroyed with the item (DESTROY).
class TapAttachment final : public QObject
{
public:
    explicit TapAttachment(QQuickItem* item)
        : QObject(item)
    {
        setObjectName(QLatin1StringView(attachmentName));
    }
    ~TapAttachment() override
    {
        // Normally releaseVideo already unlinked the output while the player was alive. A QAudioBufferOutput keeps a
        // raw pointer to its player (QAudioBufferOutputPrivate::mediaPlayer) that ~QMediaPlayer does not clear, so
        // one still set on a destroyed player must never be deleted: leak it rather than crash.
        if (m_output && !m_player) {
            qCWarning(lcRes, "audio tap: player destroyed before the tap was detached, its buffer output is leaked");
            m_output->setParent(nullptr);
            m_output = nullptr;
        }
        detach(false);
    }

    // Attaches tap to the player (replacing the current tap without a moment of Qt sound). false + err on failure.
    bool attach(const std::shared_ptr<TapContent>& tap, QMediaPlayer* player, QVideoSink* sink, std::string& err)
    {
        if (acquire(tap->id) != okRc) {
            err = "audio tap has no references left";
            return false;
        }
        if (!m_tap) {
            m_player = player;
            m_qtOutput = player->audioOutput();
        }
        unhook();

        m_tap = tap;
        tap->owner = this;
        const uint32_t gen = tap->attachGen.fetch_add(1, std::memory_order_acq_rel) + 1;
        tap->rate.store(player->playbackRate(), std::memory_order_relaxed);
        tap->newSegment.store(true, std::memory_order_release);
        tap->flags.store(VLQT_AUDIO_TAP_ATTACHED
                | (player->playbackState() == QMediaPlayer::PlayingState ? VLQT_AUDIO_TAP_PLAYING : 0),
            std::memory_order_relaxed);

        QAudioFormat fmt;
        fmt.setSampleFormat(QAudioFormat::Float);
        fmt.setChannelCount(tap->channels);
        fmt.setSampleRate(tap->sampleRate);
        // Owned by this attachment, not by the player: its lifetime does not depend on the order in which the item's
        // children (player, attachment) are destroyed.
        auto* output = new QAudioBufferOutput(fmt, this);
        TapContent* raw = tap.get();
        // DirectConnection: runs on the player's audio thread. The lambda keeps the Content alive while it may run.
        connect(output, &QAudioBufferOutput::audioBufferReceived, output,
            [keep = tap, raw, gen](const QAudioBuffer& b) { produce(*raw, gen, b); }, Qt::DirectConnection);
        QAudioBufferOutput* old = std::exchange(m_output, output);
        // Buffer output first, then Qt's output off (no Qt sound from here, the player runs on the system clock). The
        // other order leaves a playing player without any audio output for a moment, and the FFmpeg backend then
        // drops the audio stream until the next source change (no buffers at all).
        player->setAudioBufferOutput(output);
        player->setAudioOutput(nullptr);
        if (old)
            old->deleteLater(); // still referenced by a buffer in flight: its slot ignores the tap by generation

        m_conns.push_back(connect(player, &QMediaPlayer::playbackStateChanged, this, [raw](QMediaPlayer::PlaybackState s) {
            const bool playing = s == QMediaPlayer::PlayingState;
            setFlag(*raw, VLQT_AUDIO_TAP_PLAYING, playing);
            if (playing)
                raw->newSegment.store(true, std::memory_order_release); // play after pause
        }));
        m_conns.push_back(connect(player, &QMediaPlayer::playbackRateChanged, this,
            [raw](qreal r) { raw->rate.store(r, std::memory_order_relaxed); }));
        m_conns.push_back(connect(player, &QMediaPlayer::sourceChanged, this,
            [raw] { raw->newSegment.store(true, std::memory_order_release); }));
        if (sink)
            m_conns.push_back(connect(sink, &QVideoSink::videoFrameChanged, this,
                [keep = tap, raw, gen](const QVideoFrame& f) {
                    if (f.isValid() && raw->attachGen.load(std::memory_order_acquire) == gen)
                        storeVideoFrame(*raw, f.startTime());
                }, Qt::DirectConnection));
        qCDebug(lcRes, "audio tap %llx attached (%d channels, %d Hz)", static_cast<unsigned long long>(tap->id),
            tap->channels, tap->sampleRate);
        return true;
    }

    // Detaches the current tap; restore = Qt's own output plays again (prop 0), false when the item goes.
    void detach(bool restore)
    {
        if (!m_tap)
            return;
        unhook();
        if (m_player) {
            // Qt's output back before the buffer output goes: never a playing player without audio output (see attach).
            if (restore)
                m_player->setAudioOutput(m_qtOutput);
            if (m_player->audioBufferOutput() == m_output)
                m_player->setAudioBufferOutput(nullptr);
        }
        // A buffer in flight may still use it (its slot ignores the tap by generation). Its destructor still reaches
        // the player even after the unlink above, so it must go while the player exists: the next event loop during
        // playback, at once in releaseVideo before the item is deleted.
        if (m_output)
            m_output->deleteLater();
        m_output = nullptr;
        m_player = nullptr;
        m_qtOutput = nullptr;
    }

    const std::shared_ptr<TapContent>& tap() const { return m_tap; }

private:
    // Lets go of the current tap (flags, generation, element reference); keeps the player and its outputs.
    void unhook()
    {
        for (const auto& c : m_conns)
            disconnect(c);
        m_conns.clear();
        if (!m_tap)
            return;
        m_tap->attachGen.fetch_add(1, std::memory_order_acq_rel);
        m_tap->flags.store(0, std::memory_order_relaxed);
        m_tap->owner = nullptr;
        const uint64_t id = m_tap->id;
        m_tap.reset();
        releaseElement(id);
        qCDebug(lcRes, "audio tap %llx detached", static_cast<unsigned long long>(id));
    }

    std::shared_ptr<TapContent> m_tap;
    QPointer<QMediaPlayer> m_player;
    QPointer<QAudioOutput> m_qtOutput;          // the wrapper's AudioOutput, restored on detach
    QAudioBufferOutput* m_output = nullptr;    // child of this attachment
    std::vector<QMetaObject::Connection> m_conns;
};

TapAttachment* attachmentOf(QQuickItem* item, bool create)
{
    if (auto* a = item->findChild<QObject*>(QLatin1StringView(attachmentName), Qt::FindDirectChildrenOnly))
        return static_cast<TapAttachment*>(a);
    return create ? new TapAttachment(item) : nullptr;
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
        qCWarning(lcRes, "audio tap %llx: bad format (%d channels, %d Hz)", static_cast<unsigned long long>(id), channels,
            sampleRate);
        return badValueRc;
    }
    // Once per tap: the format never changes (a new sample rate = a new tap).
    if (findTap(id)) {
        qCWarning(lcRes, "audio tap %llx: already initialised", static_cast<unsigned long long>(id));
        return badValueRc;
    }
    const int rc = beginExternal(id, VLQT_RESOURCE_KIND_AUDIO_TAP);
    if (rc != okRc)
        return rc;
    auto tap = std::make_shared<TapContent>();
    tap->id = id;
    tap->channels = channels;
    tap->sampleRate = sampleRate;
    tap->capacity = static_cast<int>(static_cast<int64_t>(sampleRate) * ringMs / 1000);
    // The whole ring here, never on the producer thread.
    tap->samples = std::make_unique<float[]>(static_cast<size_t>(tap->capacity) * channels);
    tap->records = std::make_unique<Segment[]>(TapContent::recordCapacity);
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
    qCDebug(lcRes, "audio tap %llx: %d channels, %d Hz, %d frames", static_cast<unsigned long long>(id), channels,
        sampleRate, tap->capacity);
    return okRc;
}

int read(uint64_t id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state)
{
    // Silent on unknown ids: the reader polls a tap the main thread may have released (protocol.h).
    const auto tap = findTap(id);
    if (!tap)
        return staleIdRc;
    vlqt_audio_tap_state local;
    vlqt_audio_tap_state& s = state ? *state : local;
    fillState(*tap, s);
    if (maxFrames < 0 || channels != tap->channels)
        return badValueRc;
    return readRing(*tap, dst, maxFrames, s);
}

long long nowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

void releaseVideo(QQuickItem* item)
{
    TapAttachment* a = attachmentOf(item, false);
    if (!a)
        return;
    a->detach(false);
    // Now, while the player exists (and is stopped, vlqtRelease): also outputs of earlier switches still waiting for
    // deleteLater, which would otherwise go with the attachment after the player.
    qDeleteAll(a->findChildren<QAudioBufferOutput*>(Qt::FindDirectChildrenOnly));
}

bool setVideoTap(Elements&, Node& node, const ResolvedProp&, const Value& v, std::string& err)
{
    if (v.u == 0) {
        if (TapAttachment* a = attachmentOf(node.item, false))
            a->detach(true);
        return true;
    }
    const auto work = std::static_pointer_cast<TapContent>(readyContent(v.u, VLQT_RESOURCE_KIND_AUDIO_TAP));
    if (!work) {
        err = "not a Ready audio tap";
        return false;
    }
    TapAttachment* a = attachmentOf(node.item, true);
    if (a->tap() == work)
        return true;
    if (work->owner && work->owner != a) {
        qCWarning(lcRes, "audio tap %llx: already attached to another Video, ignored until it detaches",
            static_cast<unsigned long long>(v.u));
        return true;
    }
    auto* player = qobject_cast<QMediaPlayer*>(node.item->property("vlqtPlayer").value<QObject*>());
    if (!player) {
        err = "Video without a media player";
        return false;
    }
    QObject* out = node.item->property("vlqtOut").value<QObject*>();
    QVideoSink* sink = out ? out->property("videoSink").value<QVideoSink*>() : nullptr;
    return a->attach(work, player, sink, err);
}

} // namespace vlqt::res::audio
