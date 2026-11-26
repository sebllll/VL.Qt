# VL.Qt.Stride

Companion package of [VL.Qt](https://www.nuget.org/packages/VL.Qt/) for Stride in [vvvv gamma](https://visualprogramming.net): Qt 6 (Qt Quick) scenes built with VL.Qt, shown in a Stride scene. **Windows only (Direct3D 11) and experimental.**

## What it contains

- **`QtTexture`** (category `Stride.Textures.Source`): renders a scene into a Stride `Texture` without a window. GPU only, premultiplied alpha, ready in the same frame. Takes mouse, touch and keyboard input at `Input Notifications`.
- **`QtRenderer`** (category `Stride.Rendering`): a layer for after the 3D scene that draws a scene into the render target it is drawn into. Half-transparent content blends like in a Qt window. Takes the input of the window.
- **`ToTexturePixels`** (category `Stride.Input`): maps the input of a window to the pixels of a `QtTexture` shown on a quad.

Which node to use, pins and defaults, formats and alpha, input, render threads and limits (same GPU adapter as the Qt windows, device loss): [Qt in Stride](https://github.com/sebllll/VL.Qt/blob/main/docs/stride.md).

## Install

Add `VL.Qt.Stride` to the dependencies of your vvvv patch. It pulls in `VL.Qt` with the same version; Stride (`VL.Stride`) comes with vvvv. It is built and released together with `VL.Qt` from the same repository, so the two versions always match.

The help patches are in the Help Browser under the topic `Stride`.

## Documentation

- [Qt in Stride](https://github.com/sebllll/VL.Qt/blob/main/docs/stride.md)
- [VL.Qt README and getting started](https://github.com/sebllll/VL.Qt#readme)
- [Documentation index](https://github.com/sebllll/VL.Qt/blob/main/docs/README.md)

## License

Licensed under the [MIT License](https://github.com/sebllll/VL.Qt/blob/main/LICENSE). The Qt runtime is part of the `VL.Qt` package, see its [LICENSES.md](https://github.com/sebllll/VL.Qt/blob/main/LICENSES.md).
