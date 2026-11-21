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

For coding agents and contributors, the working conventions (build, tests, code style, branches) are in [AGENTS.md](../AGENTS.md).

## Design notes

Why things are built the way they are. Notes live in [design/](design/README.md): what a design note is, the rules for every note and the template ([design/_template.md](design/_template.md)). The pages below still sit in `decisions/` and `topics/` and move to `design/`; dated measurements stay in `research/`.

| Note | Content |
|---|---|
| [design/resources.md](design/resources.md) | The resource manager of QtNative: ids, states, threads, reference counts, replacement, external content and device loss. |
| [design/timing.md](design/timing.md) | How the perf monitor measures a pump: the signals that bound each phase, the vsync wait, the hand-off from render threads and the timeline geometry. |
| [decisions/device-sharing.md](decisions/device-sharing.md) | One QtNative D3D11 device per process shared by all windows and images, device loss, and why Qt does not render on Stride's device. |
| [decisions/render-texture.md](decisions/render-texture.md) | `QtTexture` and the layer `QtRenderer` in the companion package `VL.Qt.Stride`: a Qt scene rendered offscreen into a Stride texture or drawn into a render target, with shared fences, ownership, format and alpha, frame timing and package layout. |
| [decisions/element-catalog-v1.md](decisions/element-catalog-v1.md) | Conventions shared by all elements: geometry pins, coordinate space, colours, image sources and pin design. |
| [decisions/events.md](decisions/events.md) | How events and measured values travel from Qt back to the element nodes through feedback objects. |
| [decisions/qtnative-deployment.md](decisions/qtnative-deployment.md) | How QtNative is built, licensed (LGPL), packaged and located at runtime. |
| [decisions/qt-bridges.md](decisions/qt-bridges.md) | Qt Bridge for C# compared with VL.Qt: no switch, no redundant features (research in [research/01_qt-bridge-csharp.md](research/01_qt-bridge-csharp.md)). |
| [decisions/user-qml.md](decisions/user-qml.md) | Loading a user `.qml` file as an element (`QmlElement`, issue #104) and the QML node factory (#107); background in [topics/user-qml.md](topics/user-qml.md). |
| [decisions/reconciler-identity.md](decisions/reconciler-identity.md) | How the reconciler decides every frame between updating a Qt object and creating a new one. |
| [decisions/timing.md](decisions/timing.md) | One shared clock for vvvv and Qt animations through a custom animation driver on `IFrameClock`. |
| [decisions/paths.md](decisions/paths.md) | Lines, paths and splines on Qt Quick Shapes: element and node set, `QtPath`, Skia parity, limits; plan in [plans/paths/00_overview.md](plans/paths/00_overview.md) (issues #154, #155). |
| [research/02_shapes-spike.md](research/02_shapes-spike.md) | Spike for lines and paths (#154): Qt Quick Shapes with a custom path element, `CurveRenderer` quality and benchmark, recommended limits. |
| [topics/element-immutability.md](topics/element-immutability.md) | Why elements are immutable descriptions and where Qt-side state deliberately lives outside them. |
| [topics/style-nodeset.md](topics/style-nodeset.md) | Design of the nodes that build, merge and read styles for interactive widgets. |
| [topics/gradients.md](topics/gradients.md) | Open topic: gradient fills for elements (handover for a brainstorm). |
