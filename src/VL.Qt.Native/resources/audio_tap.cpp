#include "resources/audio_tap.h"

#include "app/last_error.h"
#include "elements/elements.h"
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
#include <array>
#include <chrono>
#include <cstring>
#include <memory>
#include <thread>
#include <utility>

namespace vlqt::res::audio {

namespace {

static_assert(std::atomic<double>::is_always_lock_free && std::atomic<uint64_t>::is_always_lock_free
        && std::atomic<int>::is_always_lock_free,
    "the audio thread must not take a lock");

// Ring length in ms (protocol.h: about 500 ms).
constexpr int ringMs = 500;
// A buffer whose startTime is this far from the end of the previous one starts a new segment (source change, seek).
// Qt's buffers are contiguous to the microsecond, also across a loop wrap.
constexpr long long mediaJumpUs = 50'000;
// A buffer emitted this much later than the previous one ended (wall time) starts a new segment (stall, pause).
constexpr long long wallGapNs = 150'000'000;

// --- Slot table (see audio_tap.h): the reader finds a tap without a lock and never owns a reference ---
//
// Main thread: writes tap while id == 0 and readers == 0, then publishes id; clears id, waits for readers == 0, then
// resets tap. Reader (any thread): readers + 1, re-check id, use tap.get(), readers - 1. The id store of destroy and the
// readers increment of the reader are seq_cst with the loads that follow them, so either the reader sees id 0 and
// leaves the tap alone, or destroy sees its count and waits for it.
constexpr int maxTaps = 64;
static_assert(maxTaps == 64, "the last-error text of init names the limit");

struct Slot
{
    std::atomic<uint64_t> id{0};
    std::atomic<int> readers{0};
    std::shared_ptr<TapContent> tap; // main thread only, see above
};

std::array<Slot, maxTaps> s_slots;

// Main thread: the slot of id, else nullptr.
Slot* slotOf(uint64_t id)
{
    for (Slot& s : s_slots)
        if (id != 0 && s.id.load(std::memory_order_relaxed) == id)
            return &s;
    return nullptr;
}

// Main thread: a free slot, else nullptr (all maxTaps used).
Slot* freeSlot()
{
    for (Slot& s : s_slots)
        if (s.id.load(std::memory_order_relaxed) == 0 && !s.tap)
            return &s;
    return nullptr;
}

// Main thread: the tap goes from the table; returns once no reader uses it. The reference drops here, not on the
// audio thread.
void unpublish(Slot& s)
{
    s.id.store(0, std::memory_order_seq_cst);
    while (s.readers.load(std::memory_order_seq_cst) != 0)
        std::this_thread::yield();
    s.tap.reset();
}

// Reader (any thread): announces itself on the slot of id for the time it uses the tap.
class ReadGuard
{
public:
    explicit ReadGuard(uint64_t id)
    {
        if (id == 0)
            return;
        for (Slot& s : s_slots) {
            if (s.id.load(std::memory_order_acquire) != id)
                continue;
            s.readers.fetch_add(1, std::memory_order_seq_cst);
            if (s.id.load(std::memory_order_seq_cst) == id) {
                m_slot = &s;
                m_tap = s.tap.get();
                return;
            }
            s.readers.fetch_sub(1, std::memory_order_release);
        }
    }
    ~ReadGuard()
    {
        if (m_slot)
            m_slot->readers.fetch_sub(1, std::memory_order_release);
    }
    ReadGuard(const ReadGuard&) = delete;
    ReadGuard& operator=(const ReadGuard&) = delete;

    TapContent* tap() const { return m_tap; }

private:
    Slot* m_slot = nullptr;
    TapContent* m_tap = nullptr;
};

class AudioTapKind final : public Kind
{
public:
    const char* name() const override { return "audio tap"; }
    // External content (init on the main thread): never decoded or uploaded.
    std::string decode(Content&) override { return "audio tap: no decode"; }
    int64_t upload(Content&, int64_t, UploadStatus& status, std::string& error) override
    {
        status = UploadStatus::Done;
        error = "audio tap: no upload";
        return 0;
    }
    void destroy(Content& work) override
    {
        auto& tap = static_cast<TapContent&>(work);
        // The element reference keeps the tap alive while a player is attached, so nothing writes any more here; a
        // QAudioBufferOutput still being deleted ignores the tap by its generation.
        tap.attachGen.fetch_add(1, std::memory_order_acq_rel);
        // Also called for content the table never got (finishExternal dropped it): only its own slot goes.
        if (Slot* s = slotOf(tap.id); s && s->tap.get() == &tap)
            unpublish(*s);
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

// --- Reader (any thread, one reader per tap at a time: readRing publishes its positions with plain stores; no lock,
// no allocation) ---

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
        takeOver(player);
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

    // Mutes the item without a tap (the tap it asks for is owned by another Video): its current tap goes, Qt's output
    // stays off. No buffer output either, so the backend may drop the audio stream: nothing plays it. Prop 0 restores
    // Qt's output (detach).
    void mute(QMediaPlayer* player)
    {
        takeOver(player);
        unhook();
        player->setAudioOutput(nullptr);
        dropOutput();
    }

    // Gives the player back (current tap and buffer output go); restore = Qt's own output plays again (prop 0), false
    // when the item goes. Nothing to do when this attachment never took the player over.
    void detach(bool restore)
    {
        unhook();
        // Qt's output back before the buffer output goes: never a playing player without audio output (see attach).
        if (restore && m_player)
            m_player->setAudioOutput(m_qtOutput);
        dropOutput();
        m_player = nullptr;
        m_qtOutput = nullptr;
    }

    const std::shared_ptr<TapContent>& tap() const { return m_tap; }

private:
    // The first time this attachment changes the player's outputs: remembers Qt's output for detach(true).
    void takeOver(QMediaPlayer* player)
    {
        if (m_player)
            return;
        m_player = player;
        m_qtOutput = player->audioOutput();
    }

    // Unlinks the buffer output from the player and deletes it; keeps the player and Qt's output.
    void dropOutput()
    {
        if (!m_output)
            return;
        if (m_player && m_player->audioBufferOutput() == m_output)
            m_player->setAudioBufferOutput(nullptr);
        // A buffer in flight may still use it (its slot ignores the tap by generation). Its destructor still reaches
        // the player even after the unlink above, so it must go while the player exists: the next event loop during
        // playback, at once in releaseVideo before the item is deleted.
        m_output->deleteLater();
        m_output = nullptr;
    }

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
    if (slotOf(id)) {
        qCWarning(lcRes, "audio tap %llx: already initialised", static_cast<unsigned long long>(id));
        return badValueRc;
    }
    // Checked before the resource changes state, so a full table leaves it Pending. Stays free until the publish below:
    // only the main thread writes the table, and beginExternal refuses other threads.
    Slot* slot = freeSlot();
    if (!slot) {
        qCWarning(lcRes, "audio tap %llx: too many audio taps (%d)", static_cast<unsigned long long>(id), maxTaps);
        setLastErrorStatic("too many audio taps (64): release unused ones");
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
    const int64_t bytes = static_cast<int64_t>(tap->capacity) * channels * static_cast<int64_t>(sizeof(float));
    const int finished = finishExternal(id, tap, bytes, {});
    if (finished != okRc) // the manager dropped it (cannot happen on the main thread between begin and finish)
        return finished;
    // Publish: the content first, the id last (a reader that sees the id sees the tap).
    slot->tap = std::move(tap);
    slot->id.store(id, std::memory_order_seq_cst);
    qCDebug(lcRes, "audio tap %llx: %d channels, %d Hz, %d frames", static_cast<unsigned long long>(id), channels,
        sampleRate, slot->tap->capacity);
    return okRc;
}

int read(uint64_t id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state)
{
    // Silent on unknown ids: the reader polls a tap the main thread may have released (protocol.h).
    const ReadGuard guard(id);
    TapContent* tap = guard.tap();
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

bool setVideoTap(Elements&, ElementEntry& node, const ResolvedProp&, const Value& v, std::string& err)
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
    auto* player = qobject_cast<QMediaPlayer*>(node.item->property("vlqtPlayer").value<QObject*>());
    if (!player) {
        err = "Video without a media player";
        return false;
    }
    // One player per tap (COMP-03): a second item on the tap stays muted, else it plays Qt's sound next to the tap.
    // Nothing re-runs this setter when the owner detaches, so the refusal is final for this value.
    if (work->owner && work->owner != a) {
        a->mute(player);
        err = "audio tap already used by another Video item: use one Video (VL.Audio) per item";
        return false;
    }
    QObject* out = node.item->property("vlqtOut").value<QObject*>();
    QVideoSink* sink = out ? out->property("videoSink").value<QVideoSink*>() : nullptr;
    return a->attach(work, player, sink, err);
}

} // namespace vlqt::res::audio
