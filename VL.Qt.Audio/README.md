# VL.Qt.Audio

Companion package of [VL.Qt](https://www.nuget.org/packages/VL.Qt/) for audio in [vvvv gamma](https://visualprogramming.net): the sound of Qt elements as [VL.Audio](https://www.nuget.org/packages/VL.Audio/) signals. **Windows only** (VL.Audio is NAudio-based).

## Video (VL.Audio)

The node `Video (VL.Audio)` (category `Qt.Elements`) plays a video file like `Video` of VL.Qt, but the sound comes out at `Audio` as `AudioSignal`s (stereo, one per channel) instead of Qt's own audio output, which stays off. Connect `Audio` to `AudioOut` or any other VL.Audio node: mixing, effects, device and channel choice (ASIO/WASAPI) and analysis work as with any audio source. `Video` of VL.Qt is unchanged.

- **Same pins as `Video`**, plus `Audio` (output), `Audio Offset` (optional, ms, default 0) and `Sync Error` (optional output, ms). `Volume` (0..1) acts on the signal.
- **Synchronized to the picture:** the sound follows the frames Qt shows. Internally the error stays below 5 ms; end to end it is about one video frame after calibration with `Audio Offset`. Strict frame accuracy cannot be guaranteed with Qt's player.
- **`Rate` other than 1 plays like tape:** the pitch follows the rate. A change of the rate may glitch once while the sound re-locks.
- **Help patch:** `HowTo Play a Video with VL.Audio` (Help Browser, topic `Audio`).

Details (sync model, calibration, limits): [Video sound through VL.Audio](https://github.com/sebllll/VL.Qt/blob/main/docs/video-audio.md).

## Install

Add `VL.Qt.Audio` to the dependencies of your vvvv patch. It pulls in `VL.Qt` with the same version and `VL.Audio`. It is built and released together with `VL.Qt` from the same repository, so the two versions always match.

## Documentation

- [VL.Qt README and getting started](https://github.com/sebllll/VL.Qt#readme)
- [Documentation index](https://github.com/sebllll/VL.Qt/blob/main/docs/README.md)

## License

Licensed under the [MIT License](https://github.com/sebllll/VL.Qt/blob/main/LICENSE). The Qt runtime is part of the `VL.Qt` package, see its [LICENSES.md](https://github.com/sebllll/VL.Qt/blob/main/LICENSES.md).
