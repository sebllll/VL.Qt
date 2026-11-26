# Video sound through VL.Audio

`Video (VL.Audio)` plays a video file like `Video` of VL.Qt, but its sound comes out as [VL.Audio](https://www.nuget.org/packages/VL.Audio/) `AudioSignal`s: mixing, effects, device and channel choice (ASIO/WASAPI) and analysis work as with any other audio source. It lives in the companion package `VL.Qt.Audio` (category `Qt.Elements`, **Windows only**: VL.Audio is NAudio-based).

`Video (VL.Audio)` is **experimental** (`SymbolSmell.Experimental`, like the other companion nodes) until its end-to-end latency has been checked in vvvv on Windows x64 (see [Output latency](#output-latency)).

Help patch: `HowTo Play a Video with VL.Audio` (in the Help Browser under `Audio`, package `VL.Qt.Audio`; the one place where VL.Audio is sure to be installed).

## Two nodes

| | `Video` (VL.Qt) | `Video (VL.Audio)` (VL.Qt.Audio) |
|---|---|---|
| Sound | Qt's own audio output (default device) | `Audio` output only, Qt's output is always off (no switch pin) |
| Platforms | Windows, Linux | Windows |
| Package | `VL.Qt` | `VL.Qt.Audio` (depends on `VL.Qt` of the same version and on `VL.Audio`, so installing it installs VL.Audio too) |

`Video` of VL.Qt is unchanged: it does not depend on VL.Audio and still plays through Qt.

`Video (VL.Audio)` has the pins of `Video` and three more:

- **`Audio`** (output): one `AudioSignal` per channel (stereo), at the sample rate of the VL.Audio engine. Silent without a file, without an audio track and while paused. Connect it to `AudioOut` or any other VL.Audio node.
- **`Audio Offset`** (optional input, ms, default 0): delay of the sound against the picture, positive = the sound comes later. Calibrates the latency of display and audio device that VL.Qt cannot know.
- **`Sync Error`** (optional output, ms): measured offset of the sound against the frame at Qt's video sink, positive = the sound is late. Diagnostics, see [Sync](#sync).

`Volume` (0..1) acts on the signal at `Audio`: Qt delivers the samples before its own volume, so the node applies it, ramped over one buffer on a change.

## How it works

The `MediaPlayer` of the Video element gets a `QAudioBufferOutput` (Qt 6.8+, FFmpeg backend) in the format of the VL.Audio engine (float, stereo, its sample rate; Qt resamples) and its `audioOutput` is set to null. Without an audio output the player runs on the system clock; with one, even muted, Qt would sync the picture to the audio device with about 200 ms of start offset and seconds of slewing.

The buffers go from the player's worker thread into a lock-free ring (about 500 ms) of an *audio tap*, a resource of QtNative that lives independently of the element. `VideoAudioSignal` reads it on VL.Audio's audio thread without allocating. The contract between QtNative and C# is documented in `src/VL.Qt.Native/protocol.h`, "Audio tap". A new sample rate of the engine means a new tap; the element switches to it without a pause in between.

If no tap can be created, the node reports the error and the element stays muted, so the sound never comes out of two places.

## Sync

The sound follows the picture (Qt's player has no external clock). Qt emits an audio buffer when its start is "now" on the player clock, in step with the matching video frame (within ±1.5 ms in the spike), so the emit time of a buffer is the reference of the picture. Each buffer carries it; sample *k* is due at `emit time + k / (sample rate × rate)`.

A control loop runs once per VL.Audio buffer:

- **Error:** `now + output latency − due time of the next input frame − target delay`. The target delay is `Audio Offset`, but at least what is reachable: output latency of VL.Audio (buffer plus driver latency; ASIO `PlaybackLatency`, WASAPI the buffer of the shared stream, see [Output latency](#output-latency)) + one buffer + 5 ms.
- **Large error** (start, after a discontinuity, underrun): removed once by dropping input frames or inserting silence, then the loop is locked.
- **Small error:** a PI controller steers the resampling ratio (proportional part within ±0.5 %, drift of the audio device against the player clock within ±1.2 %).
- **Underrun:** silence, never stretching; the loop re-locks.
- **Discontinuities** flush the ring and re-lock: source change, rate change, play after pause, ring overrun, a new tap. A **loop wrap is none**: Qt's audio time keeps counting across passes, so the sound continues without a gap.

**Accuracy.** Internally the error stays below 5 ms (the spike measured a standard deviation of about 1 ms between audio buffers and frames). End to end it is about one video frame after calibration with `Audio Offset`. Strict frame accuracy cannot be guaranteed with Qt's player.

`Sync Error` compares the sound with the frame that reaches Qt's video sink. It includes `Audio Offset` and the output latency of VL.Audio, but not the latency of the display (render thread, swap chain; with `QtTexture` plus vvvv also the following stages). It is NaN while the sound is not locked yet (start, after a jump, pause or underrun).

### Output latency

The control loop needs the delay between VL.Audio handing a buffer over and the sound leaving the audio engine. For ASIO it is the driver's `PlaybackLatency`. For WASAPI VL.Audio 1.9.21 opens `WasapiOut(device, Shared, eventSync, ceil(MinimumDevicePeriod in ms))`, that is 3 ms asked for, but Windows runs a shared stream with its own period. `VideoAudioSignal` therefore uses **the buffer length of the stream** (`GetBufferSize` / mix rate of a shared event-driven client initialised like VL.Audio's, read once per driver and sample rate by `WasapiOutputLatency`); it falls back to 10 ms (the default period of shared mode) if the device refuses the client. `StreamLatency` is 0 in shared mode and says nothing.

Measured on Windows x64, Realtek default device, 48 kHz mix, VL.Audio 1.9.21 setup (`WasapiLatencyTests` and a loopback test, 2026-11):

| Value | Result |
|---|---|
| `MinimumDevicePeriod` (used before) | 3.00 ms |
| `DefaultDevicePeriod` | 10.00 ms |
| Latency argument VL.Audio passes | 3 ms |
| Stream buffer (**used**) | 1056 frames = 22.00 ms |
| `AudioClient.StreamLatency` | 0.00 ms |
| Queue ahead of the first frame handed over (`GetCurrentPadding`, 43 hand-overs × 3 runs) | 12.00 ms every time; the last frame of a 10 ms chunk waits 22 ms |
| Click handed over → click in a loopback capture (QPC stamps of the packets, 43 clicks × 3 runs) | 90.3 ms median, 89.4 to 90.7 ms |

The buffer is an upper bound for what the sound waits before the mixer (12 ms for the first frame of a chunk, 22 ms for the last). The loopback value is **not used**: its packet stamps run about 13 ms ahead of the time the packets are read, so they are not a delay from the render call to the speaker, and what the driver stack of the sound card adds behind the shared mixer (effects, hardware buffer) could not be separated from it. The end-to-end latency is therefore not established: whether `Sync Error` and the sound agree in vvvv (with the help patch `HowTo Play a Video with VL.Audio`, `Audio Offset` 0, a test video with a flash and a click) is a **manual check** and the reason for the experimental mark. The assumed latency grows by 19 ms against the former 3 ms, so the sound shifts by up to that amount against the picture at the same settings: an `Audio Offset` calibrated before this change has to be calibrated again.

**Calibration.** Display and device latency can only be measured from outside, once per setup: play a test video with a flash and a click, film screen and speaker with a 240 fps phone and enter the remainder in `Audio Offset`. No stable common offset has been measured yet, so the default stays 0.

## Rate

`Rate` ≠ 1 plays like tape: the pitch follows the rate (the node resamples the media content by 1/rate); there is no pitch correction. A change of the rate flushes the ring and the sound re-locks, which may glitch once. At a stable rate the playback is stable; rate 1 has priority.

## Limits

- Windows only. The spike was measured on win-arm64; on Windows x64 only the WASAPI output latency was measured (see [Output latency](#output-latency)), not the end-to-end sync; Linux is not covered. Experimental until then.
- Stereo only: Qt mixes mono or multichannel media to two channels.
- No reverse playback (like `Video`: `Rate` below 0.01 is clamped).
- Not frame-exact, see [Accuracy](#sync).

For contributors: how to run the help patch from the repository and the tests are in [Development](contributing/development.md#video-vlaudio-tests).
