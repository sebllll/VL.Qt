/* audio_tap.h - resource kind VLQT_RES_KIND_AUDIO_TAP (#22): the sound of a Video element as float samples for
 * VL.Audio. The contract (lifetime, clock, discontinuities, reader rules) is in protocol.h "Audio tap"; the C API in
 * vlqt.h (vlqt_res_audio_tap_init, vlqt_res_read_audio, vlqt_now_ns).
 *
 * A tap is external content of the resource manager (resources.h beginExternal / finishExternal): init makes it Ready
 * at once, there is no worker and no upload. Besides the manager's table the taps live in a registry of this file
 * (id -> shared_ptr<TapWork>) that the audio thread can read without the manager's mutex; Kind::destroy (main thread,
 * after the host release and the last element reference) removes it there. A reader that holds a TapWork keeps it alive
 * past destroy, so a read never touches freed memory.
 *
 * Threads: init, the Video prop and destroy on the main thread; the producer (audioBufferReceived) on a worker thread
 * of the player; read on any thread (VL.Audio's audio thread).
 *
 * Attach: a QObject child of the Video item (TapAttachment in audio_tap.cpp) owns the QAudioBufferOutput on the item's
 * QMediaPlayer and the element reference; its destructor (DESTROY of the element) detaches. The window is never
 * involved, so a window swap (MSAA, #118) does not touch the tap.
 */
#pragma once

#include "resources.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <string>

struct vlqt_audio_tap_state;

class QQuickItem;

namespace vlqt {
class Elements;
struct Node;
struct ResolvedProp;
struct Value;
}

namespace vlqt::res::audio {

// One Qt buffer in the ring: where its frames are and when they are due (protocol.h "Producer").
struct Segment
{
    uint64_t startFrame = 0; // absolute frame index of the first frame (ring position = startFrame % capacity)
    int frames = 0;
    uint32_t discontinuity = 0; // segment number; consecutive records with the same number form one segment
    long long emitNs = 0;       // vlqt_now_ns at audioBufferReceived
    long long mediaUs = 0;      // QAudioBuffer::startTime
    double rate = 1;            // playback rate of the player at the signal
};

// The Work of a tap: format (fixed at init) and the ring. Shared by the main thread (attach/detach), the producer
// (player worker thread) and the reader (audio thread); everything the producer and the reader touch is atomic or
// owned by one side.
//
// Ring (single producer, single reader, lock free): samples and segment records are allocated in init. The producer
// writes samples, then the record, then publishes recordWrite (release); the reader consumes and publishes readFrame
// and recordRead (release), which free the space for the producer.
struct TapWork final : Work
{
    static constexpr int recordCapacity = 512; // Qt buffers are 10-40 ms: far more than 500 ms of them

    uint64_t id = 0;     // resource id (registry key)
    int channels = 0;    // interleaved float frames of channels samples
    int sampleRate = 0;
    int capacity = 0;    // ring size in frames (about 500 ms)

    std::unique_ptr<float[]> samples;      // capacity * channels
    std::unique_ptr<Segment[]> records;    // recordCapacity
    std::atomic<uint64_t> recordWrite{0};  // records published (producer)
    std::atomic<uint64_t> recordRead{0};   // records consumed (reader)
    std::atomic<uint64_t> readFrame{0};    // frames consumed (reader)

    // Producer side: a try-lock (the player's thread may change, at most one writes), then plain fields it owns.
    std::atomic<bool> writing{false};
    std::atomic<uint32_t> attachGen{0};    // + 1 on every attach/detach: buffers of an older QAudioBufferOutput are ignored
    std::atomic<bool> newSegment{false};   // main thread: attach, source change, play after pause
    std::atomic<double> rate{1};           // main thread: playbackRate of the attached player
    uint64_t writeFrame = 0;               // frames written
    bool hasLast = false, lastDropped = false;
    long long lastMediaEndUs = 0, lastEmitEndNs = 0;
    double lastRate = 1;
    uint32_t segment = 0;                  // discontinuity number of the current segment

    // Diagnostics: the last video frame at the sink (seqlock, written on the sink's thread).
    std::atomic<uint32_t> videoSeq{0};
    std::atomic<long long> videoMediaUs{0}, videoNs{0};

    // Main thread only: the TapAttachment that writes into this tap (one player per tap).
    void* owner = nullptr;

    // Counters of vlqt_audio_tap_state, written by the producer (overrun, discontinuity) or the reader (underrun).
    std::atomic<uint32_t> discontinuity{0};
    std::atomic<uint64_t> overrunFrames{0};
    std::atomic<uint64_t> underrunFrames{0};
    std::atomic<int> flags{0}; // VLQT_AUDIO_TAP_* (protocol.h), set by the main thread (attach) and the player state
};

// The kind for the manager (resources.cpp kindOf).
Kind* kind();

// --- C API backends (vlqt.h) ---
// Main thread: format of a Pending tap, Pending -> Decoding -> Ready. 0 / -7 / -9.
int init(uint64_t id, int channels, int sampleRate);
// Any thread, allocation free. Returns frames copied/dropped, -7 or -9 (vlqt.h vlqt_res_read_audio).
int read(uint64_t id, float* dst, int maxFrames, int channels, vlqt_audio_tap_state* state);
// std::chrono::steady_clock in ns.
long long nowNs();

// --- Video element (elements.cpp videoProps), main thread ---
// Setter of VLQT_PROP_AUDIO_TAP (u64): attaches the tap to the element's player and turns Qt's own output off, 0
// detaches and restores it (protocol.h "Audio tap"). false + err: not a Ready tap (ApplyBadValue).
bool setVideoTap(Elements& els, Node& node, const ResolvedProp& rp, const Value& v, std::string& err);
// Before a Video item is deleted (DESTROY, scene teardown), with its player still alive: detaches the tap and
// unlinks the QAudioBufferOutput from the player (~QMediaPlayer does not, deleting the output afterwards would call
// into the dead player).
void releaseVideo(QQuickItem* item);

} // namespace vlqt::res::audio
