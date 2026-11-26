# Review of VL.Qt before 0.10.0 – index

Twenty-three read-only review sessions, one per area, on the state of `develop` at release 0.9.10 (647392e), run on 2026-11-19 after the brief [00_brief.md](00_brief.md). Every report has the same structure: summary, what must survive, findings, target structure, questions for Sebastian, notes for other areas. Nothing was built or run: every finding comes from reading the code, and the reports say where a suspicion is not confirmed.

340 findings: 31 `must`, 211 `should`, 98 `could`. They are the raw material of the plan; the issues of the milestone `0.10.0` are cut from them.

| Report | Area | Prefix | Model | must / should / could | The finding that matters most |
|---|---|---|---|---|---|
| [05](05_reconciler.md) | Reconciler | REC | Opus | 2 / 9 / 5 | A reconcile that throws part-way (a Text over about 64 KB) leaves the renderer leaking every frame. |
| [06](06_elements.md) | Elements and the way back from Qt | ELM | Opus | 2 / 10 / 2 | The summary of `Effect` describes an older design; the feedback types are public by accident. |
| [07](07_runtime.md) | C# runtime: app, pump, renderers | RUN | Opus | 1 / 9 / 4 | `NodeErrors` shows one message per node: warnings and errors overwrite each other. |
| [08](08_protocol.md) | Wire protocol | PROTO | Opus | 2 / 8 / 2 | Nothing checks that `vlqt.dll` and `VL.Qt.dll` belong together. |
| [09](09_c-api.md) | C API and P/Invoke | CAPI | Opus | 1 / 7 / 2 | 101 exports; `Native.cs` matches `vlqt.h`, one export has no declaration. |
| [10](10_nodes-elements.md) | Element nodes and their shared core | NODE | Opus | 1 / 5 / 2 | The node pattern is sound; the duplication of pins and their docs needs one decision. |
| [11](11_qml.md) | User QML: element and node factory | QML | Opus | 1 / 8 / 3 | `QmlDeclarations.cs` is a hand-written parser that should be cut into a `Parsing` folder. |
| [12](12_shaders.md) | Shaders and the shader node factory | SHD | Opus | 2 / 10 / 4 | `@padding` is documented as DIP but applied in renderer units (100 times too large). |
| [13](13_styles-images-paths.md) | Styles, images, paths | SIP | Opus | 0 / 10 / 6 | One likely defect that needs a run to confirm (SIP-03). |
| [14](14_nodes-widgets-resources.md) | Widget, image and file nodes | NODW | Opus | 0 / 6 / 8 | Slider and Input (Int) can disagree with their channel for good. |
| [15](15_nodes-effects-interaction-paths.md) | Effect, interaction and path nodes | NODX | Opus | 1 / 8 / 4 | Two blur nodes take degrees where vvvv uses cycles; the interaction pins lack one vocabulary. |
| [16](16_native-vlqt.md) | QtNative: `vlqt.cpp` | NAPP | Opus | 1 / 11 / 5 | The last-error string is shared between threads. |
| [17](17_native-commands-events.md) | QtNative: commands, events, input | NCMD | Opus | 3 / 8 / 6 | A rejected buffer reports the error of the previous apply. |
| [18](18_native-elements.md) | QtNative: elements | NELM | Opus | 1 / 11 / 3 | The frame of a Group that contains a Group is computed wrongly. |
| [19](19_native-device-offscreen.md) | QtNative: device, offscreen, render thread | NDEV | Opus | 1 / 9 / 4 | Use-after-free risk when offscreen scenes render on two or more threads. |
| [20](20_native-resources.md) | QtNative: image and snapshot resources | NRES | Opus | 1 / 8 / 6 | An Image in a threaded offscreen scene can free its texture on the wrong thread. |
| [21](21_native-shaders-effects.md) | QtNative: shaders, effect chain, user QML item, style | NFX | Opus | 0 / 13 / 10 | An animated effect chain allocates every frame. |
| [22](22_stride-audio.md) | VL.Qt.Stride and VL.Qt.Audio | COMP | Opus | 1 / 8 / 6 | Two suspicions that need a run to confirm (COMP-03, COMP-04). |
| [23](23_api.md) | API as vvvv shows it | API | Opus | 1 / 10 / 4 | Section 7 is the catalog of proposed renames with a decision column. |
| [24](24_docs-pages.md) | Documentation: user and contributor pages | DOC | Opus | 4 / 10 / 2 | `architecture.md` says there is no render thread; there is one. |
| [25](25_docs-design-notes.md) | Documentation: decisions, plans, research, topics | DOCD | Opus | 3 / 9 / 3 | Most decisions still hold; finished plans and handovers should go. |
| [26](26_native-diagnostics.md) | QtNative: diagnostics | NDIAG | Sonnet | 1 / 11 / 7 | The value "Pump" includes the VSync wait, so it does not say what it seems to. |
| [27](27_tests.md) | Tests and SmokeTest (survey) | TEST | Sonnet | 1 / 11 / 2 | No test checks the node API (names, pins, defaults, categories). |

Not reviewed as an area of its own: the architecture as a whole (projects, namespaces, layering, public surface). It is put together from sections 4 and 6 of the reports when the plan is written.

## What comes next

1. Collect section 5 of every report (questions for Sebastian) into one list, grouped by topic, and decide them. The catalog in [23_api.md](23_api.md), section 7, is the largest block.
2. Confirm the `must` findings that are marked as suspicions with a test or a run.
3. Cut the findings into issues: the `Touches` and `Depends on` lines of the findings say what can run in parallel.
