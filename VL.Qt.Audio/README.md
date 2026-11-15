# VL.Qt.Audio

Companion package of [VL.Qt](https://www.nuget.org/packages/VL.Qt/) for audio in [vvvv gamma](https://visualprogramming.net): the sound of Qt elements as [VL.Audio](https://www.nuget.org/packages/VL.Audio/) signals. Windows only (VL.Audio is NAudio-based), **work in progress**: the package has no nodes yet.

## Planned

The node `Video (VL.Audio)`: like `Video` of VL.Qt, but the sound comes out as `AudioSignal` (mixing, effects, device and channel choice, analysis) and Qt's own audio output stays off. Design and status: [issue #22](https://github.com/sebllll/VL.Qt/issues/22).

## Install

Add `VL.Qt.Audio` to the dependencies of your vvvv patch. It pulls in `VL.Qt` with the same version and `VL.Audio`. It is built and released together with `VL.Qt` from the same repository, so the two versions always match.

## Documentation

- [VL.Qt README and getting started](https://github.com/sebllll/VL.Qt#readme)
- [Documentation index](https://github.com/sebllll/VL.Qt/blob/main/docs/README.md)

## License

Licensed under the [MIT License](https://github.com/sebllll/VL.Qt/blob/main/LICENSE). The Qt runtime is part of the `VL.Qt` package, see its [LICENSES.md](https://github.com/sebllll/VL.Qt/blob/main/LICENSES.md).
