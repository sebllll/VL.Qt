# Video sound through VL.Audio

`Video (VL.Audio)` plays a video file like `Video` of VL.Qt, but its sound comes out as [VL.Audio](https://www.nuget.org/packages/VL.Audio/) `AudioSignal`s: mixing, effects, device and channel choice (ASIO/WASAPI) and analysis work as with any other audio source. It lives in the companion package `VL.Qt.Audio` (category `Qt.Elements`, **Windows only**: VL.Audio is NAudio-based). Design and status: [issue #22](https://github.com/sebllll/VL.Qt/issues/22).

Help patch: `HowTo Play a Video with VL.Audio` (in the Help Browser under `Audio`, package `VL.Qt.Audio`; the one place where VL.Audio is sure to be installed). Run it with `tools/start-vvvv.ps1 -Patch "VL.Qt.Audio/help/HowTo Play a Video with VL.Audio.vl"`.

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

The `MediaPlayer` of the Video element gets a `QAudioBufferOutput` (Qt 6.8+, FFmpeg backend) in the format of the VL.Audio engine (float, stereo, its sample rate; Qt resamples) and its `audioOutput` is set to null. Without an audio output the player runs on the system clock; with one, even muted, Qt would sync the picture to the audio device with about 200 ms of start offset and seconds of slewing (spike in the issue).

The buffers go from the player's worker thread into a lock-free ring (about 500 ms) of an *audio tap*, a resource of QtNative that lives independently of the element. `VideoAudioSignal` reads it on VL.Audio's audio thread without allocating. The contract between QtNative and C# is documented in `src/VL.Qt.Native/protocol.h`, "Audio tap". A new sample rate of the engine means a new tap; the element switches to it without a pause in between.

If no tap can be created, the node reports the error and the element stays muted, so the sound never comes out of two places.

## Sync

The sound follows the picture (Qt's player has no external clock). Qt emits an audio buffer when its start is "now" on the player clock, in step with the matching video frame (within ±1.5 ms in the spike), so the emit time of a buffer is the reference of the picture. Each buffer carries it; sample *k* is due at `emit time + k / (sample rate × rate)`.

A control loop runs once per VL.Audio buffer:

- **Error:** `now + output latency − due time of the next input frame − target delay`. The target delay is `Audio Offset`, but at least what is reachable: output latency of VL.Audio (buffer plus driver latency; ASIO `PlaybackLatency`, WASAPI device period) + one buffer + 5 ms.
- **Large error** (start, after a discontinuity, underrun): removed once by dropping input frames or inserting silence, then the loop is locked.
- **Small error:** a PI controller steers the resampling ratio (proportional part within ±0.5 %, drift of the audio device against the player clock within ±1.2 %).
- **Underrun:** silence, never stretching; the loop re-locks.
- **Discontinuities** flush the ring and re-lock: source change, rate change, play after pause, ring overrun, a new tap. A **loop wrap is none**: Qt's audio time keeps counting across passes, so the sound continues without a gap.

**Accuracy.** Internally the error stays below 5 ms (the spike measured a standard deviation of about 1 ms between audio buffers and frames). End to end it is about one video frame after calibration with `Audio Offset`. Strict frame accuracy cannot be guaranteed with Qt's player.

`Sync Error` compares the sound with the frame that reaches Qt's video sink. It includes `Audio Offset` and the output latency of VL.Audio, but not the latency of the display (render thread, swap chain; with `QtTexture` plus vvvv also the following stages). It is NaN while the sound is not locked yet (start, after a jump, pause or underrun).

**Calibration.** Display and device latency can only be measured from outside, once per setup: play a test video with a flash and a click, film screen and speaker with a 240 fps phone and enter the remainder in `Audio Offset`. No stable common offset has been measured yet, so the default stays 0.

## Rate

`Rate` ≠ 1 plays like tape: the pitch follows the rate (the node resamples the media content by 1/rate); there is no pitch correction. A change of the rate flushes the ring and the sound re-locks, which may glitch once. At a stable rate the playback is stable; rate 1 has priority.

## Limits

- Windows only. The spike was measured on win-arm64; Windows x64 and Linux are not covered.
- Stereo only: Qt mixes mono or multichannel media to two channels.
- No reverse playback (like `Video`: `Rate` below 0.01 is clamped).
- Not frame-exact, see [Accuracy](#sync).

## Tests

- `src/VL.Qt.Audio.Tests`: the control loop with simulated clocks (drift, jitter), underrun, discontinuities, rate change and allocations per buffer, without VL.Audio or Qt.
- SmokeTest scenario `videoaudio` (`dotnet run --project src/VL.Qt.SmokeTest -c Release -- --only videoaudio`): QtNative with `help/Qt/assets/qt-help-video-audio.mp4`, samples, rate, loop wrap, pause and source change, audio/frame consistency.
- Help patch: `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File "VL.Qt.Audio/help/HowTo Play a Video with VL.Audio.vl"` (the script packs `VL.Qt` and `VL.Qt.Audio` into a local feed; VL.Audio is restored from NuGet).
