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
 * Threads: init, the Video prop and destroy on the main thread; the producer (audioBufferReceived, T4) on a worker
 * thread of the player; read on any thread (VL.Audio's audio thread).
 *
 * Status: STUB (T3 of #22). Create/init/release work, read returns 0 frames with a valid state, the Video prop checks
 * the id and attaches nothing. TODO(T4) marks what the native tap still needs.
 */
#pragma once

#include "resources.h"

#include <atomic>
#include <cstdint>
#include <string>

struct vlqt_audio_tap_state;

namespace vlqt {
class Elements;
struct Node;
struct ResolvedProp;
struct Value;
}

namespace vlqt::res::audio {

// The Work of a tap: format (fixed at init) and the ring. Shared by the main thread (attach/detach), the producer
// (player worker thread) and the reader (audio thread); everything the producer and the reader touch is atomic or
// owned by one side.
struct TapWork final : Work
{
    uint64_t id = 0;     // resource id (registry key)
    int channels = 0;    // interleaved float frames of channels samples
    int sampleRate = 0;
    int capacity = 0;    // ring size in frames (about 500 ms)

    // Counters of vlqt_audio_tap_state, written by the producer (overrun, discontinuity) or the reader (underrun).
    std::atomic<uint32_t> discontinuity{0};
    std::atomic<uint64_t> overrunFrames{0};
    std::atomic<uint64_t> underrunFrames{0};
    std::atomic<int> flags{0}; // VLQT_AUDIO_TAP_* (protocol.h), set by the main thread (attach) and the player state

    // TODO(T4): the lock-free SPSC ring, written on the player's worker thread (Qt::DirectConnection, no QObjects, no
    //   locks, no allocation; the thread may change after a source change, still one producer):
    //   - sample storage: capacity * channels floats, allocated once in init;
    //   - per Qt buffer a segment record: emit time (vlqt_now_ns at the signal), media startTime (us), rate, frame
    //     count, discontinuity number; a small ring of records next to the samples;
    //   - write index (producer) / read index (reader) as atomics, release/acquire;
    //   - discontinuity (protocol.h): new segment, counter + 1; the reader drops older frames before it delivers new
    //     ones; overrun = drop the incoming buffer, overrunFrames += frames, next buffer starts a new segment;
    //   - diagnostics: startTime and vlqt_now_ns of the last frame at the video sink (videoFrameChanged, also a worker
    //     thread), e.g. two atomics written as a seqlock pair.
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

} // namespace vlqt::res::audio
