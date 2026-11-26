# VL.Qt.Stride

Companion package of [VL.Qt](https://www.nuget.org/packages/VL.Qt/) for Stride in [vvvv gamma](https://visualprogramming.net): Qt 6 (Qt Quick) scenes built with VL.Qt, rendered into Stride textures. Windows only, experimental.

## What it contains

The node `QtTexture` (category `Qt`): like `Renderer [Qt]`, but without a window; it outputs a Stride `Texture` (premultiplied alpha, GPU only, same frame). Pins, limits (no input yet, same GPU adapter) and the help patch `HowTo Render Qt into a Stride Texture` are described in the [VL.Qt README](https://github.com/sebllll/VL.Qt#stride-texture).

## Install

Add `VL.Qt.Stride` to the dependencies of your vvvv patch. It pulls in `VL.Qt` with the same version; Stride (`VL.Stride`) comes with vvvv. It is built and released together with `VL.Qt` from the same repository, so the two versions always match. The help patches are in the Help Browser under `Qt`.

## Documentation

- [VL.Qt README and getting started](https://github.com/sebllll/VL.Qt#readme)
- [Documentation index](https://github.com/sebllll/VL.Qt/blob/main/docs/README.md)
- [Design notes for this package](https://github.com/sebllll/VL.Qt/blob/main/docs/design/stride-output.md)

## License

Licensed under the [MIT License](https://github.com/sebllll/VL.Qt/blob/main/LICENSE). The Qt runtime is part of the `VL.Qt` package, see its [LICENSES.md](https://github.com/sebllll/VL.Qt/blob/main/LICENSES.md).
