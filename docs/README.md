# VL.Qt documentation

User and contributor documentation. For an overview and installation see the [project README](../README.md).

| Page | Content |
|---|---|
| [help-patches.md](help-patches.md) | The help patches in `help/Qt/`, grouped by topic. |
| [export.md](export.md) | Exporting a standalone app with vvvvc and what the export bundles. |
| [linux.md](linux.md) | Exporting for Linux, running the result (WSL or native), fonts, building the Linux runtime from source. |
| [styles.md](styles.md) | The VLQt base style, the `Style` pin, style nodes, and self-made widgets with `IStylable`. |
| [images.md](images.md) | `QtImage` as GPU resource, the loader nodes, `ToQtImage`, `Snapshot`, mipmaps and frame budget. |
| [effects-and-shaders.md](effects-and-shaders.md) | The `Effect` pin and its nodes, ShaderEffect, the `Sampler` node, shader nodes generated from `.frag` files. |
| [tooltips.md](tooltips.md) | Preview images for `Element` and `QtImage` links in the vvvv editor. |
| [diagnostics.md](diagnostics.md) | Performance overlays (F2/F5), the `Debug` overlay, logging and errors, GPU device loss. |
| [profiling.md](profiling.md) | Repeatable CPU profiling of test scenarios with Ultra. |
| [architecture.md](architecture.md) | Element tree, reconciler, QtNative, timing, rendering and events. |
| [development.md](development.md) | Prerequisites, first build, Visual Studio solution, scripts, tests, project layout, typical workflows, encoding and git hook. |

For coding agents and contributors, the working conventions (build, tests, code style, branches) are in [AGENTS.md](../AGENTS.md).

## Design notes

Why things are built the way they are. Each note states the goals, the decision and the alternatives that were rejected.

| Note | Content |
|---|---|
| [decisions/device-sharing.md](decisions/device-sharing.md) | Qt renders on Stride's own D3D11 device instead of a device of its own, and what that means for latency and parallelism. |
| [decisions/element-catalog-v1.md](decisions/element-catalog-v1.md) | Conventions shared by all elements: geometry pins, coordinate space, colours, image sources and pin design. |
| [decisions/events.md](decisions/events.md) | How events and measured values travel from Qt back to the element nodes through sinks. |
| [decisions/qtnative-deployment.md](decisions/qtnative-deployment.md) | How QtNative is built, licensed (LGPL), packaged and located at runtime. |
| [decisions/reconciler-identity.md](decisions/reconciler-identity.md) | How the reconciler decides every frame between updating a Qt object and creating a new one. |
| [decisions/timing.md](decisions/timing.md) | One shared clock for vvvv and Qt animations through a custom animation driver on `IFrameClock`. |
| [topics/element-immutability.md](topics/element-immutability.md) | Why elements are immutable descriptions and where Qt-side state deliberately lives outside them. |
| [topics/style-nodeset.md](topics/style-nodeset.md) | Design of the nodes that build, merge and read styles for interactive widgets. |
