# VL.Qt documentation

User and contributor documentation. For an overview and installation see the [project README](../README.md).

| Page | Content |
|---|---|
| [help-patches.md](help-patches.md) | The help patches in `help/Qt/`, grouped by topic. |
| [export.md](export.md) | Exporting a standalone app with vvvvc and what the export bundles. |
| [linux.md](linux.md) | Exporting for Linux, running the result (WSL or native), fonts, building the Linux runtime from source. |
| [styles.md](styles.md) | The VLQt base style, the `Style` pin, style nodes, and self-made widgets with `IStylable`. |
| [video-audio.md](video-audio.md) | `Video (VL.Audio)` in the companion package `VL.Qt.Audio`: video sound as VL.Audio signals, sync model and accuracy, `Audio Offset`, rate (tape), `Volume`, limits. |
| [shapes.md](shapes.md) | `Line`, `Polyline` and `Polygon`: pins, geometry space, stroke and fill defaults (like Skia), dash and trim, limits (cost of changing points). |
| [images.md](images.md) | `QtImage` as GPU resource, the loader nodes, `ToQtImage`, `Snapshot`, mipmaps and frame budget. |
| [effects-and-shaders.md](effects-and-shaders.md) | The `Effect` pin and its nodes, ShaderEffect, the `Sampler` node, shader nodes generated from `.frag` files. |
| [user-qml.md](user-qml.md) | `QmlElement`: load a `.qml` file of your own as an element, properties in, changes and signals out, hot reload, limits; nodes with named pins generated from `.qml` files in `qml/` folders. |
| [tooltips.md](tooltips.md) | Preview images for `Element` and `QtImage` links in the vvvv editor. |
| [diagnostics.md](diagnostics.md) | Performance overlays (F2/F5), the `Debug` overlay, logging and errors, GPU device loss. |
| [profiling.md](profiling.md) | Repeatable CPU profiling of test scenarios with Ultra. |
| [versioning.md](versioning.md) | Version numbers, what counts as the API, obsolete and experimental nodes. |
| [glossary.md](glossary.md) | The words of VL.Qt and of the Qt behind it (element, scene, renderer, layer, pump, QRhi ...), one sentence each. |
| [architecture.md](architecture.md) | Element tree, reconciler, QtNative, timing, rendering and events. |
| [development.md](development.md) | Prerequisites, first build, Visual Studio solution, the three packages (VL.Qt, VL.Qt.Stride, VL.Qt.Audio), scripts, tests, release, project layout, typical workflows, encoding and git hook. |
| [protocol.md](protocol.md) | The command protocol: what commands, events and values mean (geometry, handlers, effect chain, shaders, resources, audio tap). The wire format is in `protocol.h`. |

For coding agents and contributors, the working conventions (build, tests, code style, branches) are in [AGENTS.md](../AGENTS.md).

## Design notes

Why things are built the way they are. Notes live in [design/](design/README.md): what a design note is, the rules for every note and the template ([design/_template.md](design/_template.md)). The pages below still sit in `decisions/` and `topics/` and move to `design/`; dated measurements stay in `research/`.

| Note | Content |
|---|---|
| [design/resources.md](design/resources.md) | The resource manager of QtNative: ids, states (diagram), threads, content ownership, reference counts, status records, replacement, images (upload paths, texture handles), snapshots, and device loss with what is restored. |
| [design/effects.md](design/effects.md) | The Effect chain in QtNative: item tree and stages, shader passes, the blur pyramid, the Shader item (uniforms, samplers, threads), alpha rules and the runtime bake of shader code. |
| [design/render-loop.md](design/render-loop.md) | Which Qt Quick render loop QtNative runs, the rules for item code that follow from it and the places that rely on the basic loop. |
| [design/render-thread.md](design/render-thread.md) | Lifetime of an offscreen scene, the pool of render threads (pin `Render Threads`), the frame of a batch, back pressure, which thread runs what and the lock on the shared device's context. |
| [design/timing.md](design/timing.md) | One shared clock for vvvv and Qt animations through a custom animation driver on `IFrameClock`, the pump per app host, render threads, VSync, and how the perf monitor measures a pump. |
| [design/gpu-device.md](design/gpu-device.md) | One QtNative D3D11 device per process shared by all windows, images and offscreen scenes: attach, adapter match and move, device loss, no device available, and why Qt does not render on Stride's device. |
| [design/stride-output.md](design/stride-output.md) | `QtTexture` and the layer `QtRenderer` in the companion package `VL.Qt.Stride`: shared texture and fences, ownership, formats, colour space and alpha, frame timing, composition of the layer and package layout. |
| [design/offscreen-input.md](design/offscreen-input.md) | Input and keyboard focus of offscreen scenes: the pin `Input Notifications`, `ToTexturePixels`, translation, native injection and focus. |
| [design/clear-and-msaa.md](design/clear-and-msaa.md) | `Clear` off through the `PreviousFrameLayer`, and MSAA: the window swap, the clamp and the offscreen resolve. |
| [research/render-texture-measurements.md](research/render-texture-measurements.md) | Measurements of the render threads, the MSAA counts and the cost of `Clear` off (one arm64 notebook, x64 not measured). |
| [design/element-model.md](design/element-model.md) | What an element is: immutable descriptions and their exceptions, the pins every element node shares, geometry and the coordinate space. |
| [design/events-and-interaction.md](design/events-and-interaction.md) | How input and values that only Qt knows come back to the patch: feedback, the Interaction nodes and their channels, latency and threads. |
| [design/input.md](design/input.md) | The input handlers of QtNative: events per handler and when they are sent, grabs and occlusion, drag offset, Touch Points and Pinch, what a change of the feedback does, and the Qt behaviour behind the rules. |
| [design/reconciler.md](design/reconciler.md) | How the reconciler turns the element tree into commands: one frame, the skip test, the order of the commands, occurrences and placeholders, input handlers, effect chains, styles, self-made widgets, and the resync after an exception. |
| [design/reconciler-identity.md](design/reconciler-identity.md) | How the reconciler decides every frame between updating a Qt object and creating a new one, and what counts as one element (its feedback slot) when it is shown at several places. |
| [design/paths.md](design/paths.md) | Lines, paths and splines on Qt Quick Shapes: the shape elements, `QtPath`, the `Qt.Paths` nodes and their Skia-like defaults, geometry space, wire format and costs. |
| [design/user-qml.md](design/user-qml.md) | Loading a user `.qml` file as an element (`QmlElement`) and the QML node factory: wire path, pins from a C# parser, hot reload, errors, time and licensing. |
| [design/qt-bridge.md](design/qt-bridge.md) | Why VL.Qt does not use Qt Bridge for C#: the comparison, the redundancy check and the sources. |
| [design/native-boundary.md](design/native-boundary.md) | The C ABI between C# and QtNative: one command buffer per frame, events by polling, return codes and last error, the version rule and logging. |
| [design/deployment.md](design/deployment.md) | How QtNative is licensed (LGPL), packaged and located at runtime: Qt version, private classes, platforms, the `qt/<rid>/` tree and export. |
| [research/02_shapes-spike.md](research/02_shapes-spike.md) | Spike for lines and paths (#154): Qt Quick Shapes with a custom path element, `CurveRenderer` quality and benchmark, recommended limits. |
| [design/styles.md](design/styles.md) | Design of the nodes that build, merge and read styles for interactive widgets. |
