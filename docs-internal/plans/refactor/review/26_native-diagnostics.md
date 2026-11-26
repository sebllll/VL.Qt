# Review 26 – QtNative diagnostics (perf and debug overlays)

Reviewer model Sonnet 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/perf.cpp` (1763 lines, not 1642 as the prompt says), `perf.h`, `debug.cpp`, `debug.h`, `src/VL.Qt/PerfStats.cs`, `docs/diagnostics.md`. Skimmed (call sites only, by search and short excerpts): `vlqt.cpp` (window creation, pump, `vlqt_window_*perf*`/`overlay`/`debug`, test hooks, `replaceWindow`, `UpdateGate` perf-overlay check), `commands.cpp` (`Scene::updateDebug`), `WindowRenderer.cs` (`Perf` getter, pump read), `QtApp.cs` (slow-frame warning), `protocol.h` (`VLQT_TYPE_*`), `vlqt.h` (perf structs), the test file lists of the SmokeTest. Not read: `offscreen.cpp` (threading contract of the render thread, see NDIAG-06), the bodies of `PerfOverlayTest.cs`/`PerfStatsTest.cs`/`DebugOverlayTest.cs`, `resources.h`/`snapshot_resource.h`.

## 1 Summary

Two diagnostic systems live in this area. They differ a lot in how well they fit their purpose.

- **Debug overlay** (`debug.cpp`, 366 lines): proportionate, one responsibility, a clear legend in `debug.h`, one list of triangles for both backends. It is the better of the two. Its weak points are small (per-frame property lookups, refresh only on apply, NDIAG-12).
- **Perf monitor** (`perf.cpp`, 1763 lines): five jobs in one file: the sampling hooks, the pure statistics, five text pages, two custom scene-graph items (graph, timeline) and an embedded QML overlay. The measurement part is sound and cheap with the overlays off. The presentation part is about two thirds of the file and has grown by accretion (issue-driven comments everywhere, three copies of the software-backend painting, three copies of the peak search).
- **The numbers are not consistent with each other.** "Pump" includes the VSync wait and the present, but the F5 counter `over budget`, the traffic-light colours, the timeline's red highlight and the F2 label "cpu" treat it as Qt work. The C# slow-frame warning subtracts the wait; the native side does not (NDIAG-01). `Fps`/`FrameMs` average over intervals the percentiles exclude (NDIAG-02). Three different things are called "budget" (NDIAG-10).
- **Defects found:** Scene page folds half of the element types into "other" (NDIAG-03); cross-thread access of the monitor from a scene's render thread (NDIAG-06, to confirm); the `Consolas` font that the column alignment relies on does not exist on Linux (NDIAG-13).

The three changes that matter most: (1) make "pump" vs "Qt work" one defined quantity and fix the counters, colours and meter label (NDIAG-01, -02); (2) cut `perf.cpp` into a pure statistics unit, the monitor, the pages and the two items, and share the software-image painting (NDIAG-04, -05); (3) write the glossary of every measured value into `docs/diagnostics.md` and rename the overloaded words (NDIAG-10).

## 2 What works and must survive

- **No allocation per frame in the sampling path.** `PerfSample`, `PerfCounters`, the 240-entry ring, `m_pending/m_done` are fixed-size. Pages allocate (Qt strings) only at 4 Hz while visible or on demand. Keep this split: *sampling hooks and `endPump` must never allocate or format text*.
- **Hot path, always on, also with the overlays off** (needed for `Renderer.Perf` and the log warning): seven signal lambdas per window (`perf.cpp:303-366`), about nine `nowMs()` reads per frame, one uncontended `std::mutex` lock in `afterFrameEnd` and one in `endPump`, a ~120 byte `PerfSample{}` reset, `PerfCounters::add` (one `QScreen::refreshRate`, one `floor`) and one ring write per pump. Estimated at a few microseconds per frame; no allocation. This is proportionate. A refactoring must not add work here.
- **Two-stage hand-off `m_pending` → `m_done` under `m_doneLock`** (`perf.h:170-177`): a sample holds only whole frames, and it is the only thing that keeps the ring correct when a scene's frame signals arrive on a render thread. Keep it, and keep the comment (shortened) at the member.
- **Statistics as pure functions** (`perfStats`, `perfHostStats`, `perfEval`, `PerfCounters`): no Qt in the header, testable with `vlqt_perf_eval` / `vlqt_perf_host_eval` without a window (`PerfStatsTest.cs`). This is the right seam; the cut in section 4 follows it.
- **Percentiles computed on query, not per pump**, and C# limits it to every 250 ms (`WindowRenderer.cs:27-29`). Keep.
- **Debug overlay costs nothing until used:** created on first use, `updateDebug()` returns early (`commands.cpp:320-322`), no `update()` while inactive, `enabled=false`, `contains()` false, so hit-testing is unchanged. This is the contract of the Debug pin.
- **Both overlays are outside the user tree** (child of `contentItem`, z `1e9` / `1e9-1`), and the perf one is excluded from the scene-change check by `perfOverlayName`. Keep; see NDIAG-09 for the debug overlay, which is not excluded.
- **One triangle list drawn two ways** in the debug overlay (RHI geometry node and `paintSoftware`), so the two backends show the same overlay.
- **Pixel-grid snapping of 1 px lines** (`Painter::segment`, `debug.cpp:61-101`): the reason (MSAA resolve halves an unaligned line) is written next to the code. Keep, with the issue number replaced by that sentence.

## 3 Findings

### NDIAG-01 "Pump" includes the VSync wait and the present, but is judged as Qt work
- **Kind:** correctness
- **Severity:** must
- **Breaking:** yes: counter values, colours and the F2 text change; `PerfStats.OverBudget`/`RecentOverBudget` count fewer pumps
- **Where:** `perf.cpp:400-401` (`pump` = whole `processEvents`), `perf.cpp:494` (`over = s.pump > budgetMs`), `perf.cpp:228` (`pumpColor`), `perf.cpp:955`, `perf.cpp:1552` (red highlight `s.pump > budget`), `perf.cpp:850-853` (F2 label "cpu" is `p.pump_ms`), `perf.cpp:1367` (series "cpu"), `QtApp.cs:442-453`, `docs/diagnostics.md:22`
- **Finding:** `pump` is the wall time of `vlqt_app_process_events`, so with VSync on (the default) it contains the wait in `QRhi::beginFrame` (the "vsync wait" phase, `perf.cpp:309-315`) and the present. The docs and the C# warning say this wait "is not Qt work" and subtract it (`ExceedsBudget`, `QtApp.cs:495`). The native side does not: `over_budget` counts `pump > budget`, `pumpColor` colours the pump row green/yellow/red against the budget, the timeline paints red where `pump > budget`, and the F2 meter prints `pump` as "cpu". With VSync on and a scene that changes every frame, the wait fills most of the interval, so the pump sits near the budget and these indicators flicker yellow/red and the counter climbs for a healthy scene. Not verified at runtime (no builds allowed here): confirm with a Renderer at VSync on and a changing scene; F5 Summary `over budget` should then count roughly half of the pumps while `GPU`/`render` are small. `PerfStatsTest.cs:34-37` tests the counter on synthetic samples only, so it does not see this.
- **Proposal:** define one quantity, **Qt work = pump − vsync − present** (the events, sync and render phases), and use it for everything that says "work": `over_budget`/`recent_over_budget` (rename to `OverBudget` of Qt work, documented), `pumpColor` of the pump row, the timeline highlight and the F2 series, which is renamed from "cpu" to "qt" (or "work"). Keep the raw `pump` (with waits) in the table and the percentiles as "pump (incl. waits)", because it is what the frame costs on the host thread. Compute the quantity once in `PerfSample` as a method `workMs()` so that the C# warning, the pages and the counters read the same definition (the C# side keeps using the sample it reads for the warning). Add the quantity to `vlqt_perf_stats` only if the C# `PerfStats` needs it (`PumpMsP95` etc. stay on raw pump; add `WorkMs`).
- **Decided:** E13, 0.2 → (a): only Qt work counts, named `OverBudget` (and `RecentOverBudget`); QtNative and C# compute it from one definition (`workMs()` of the sample, which the C# slow-frame warning reads too) and there is no second counter for the wait (c not taken); wave 1 confirms or drops the finding first, with a VSync-on Renderer and a static scene where `over budget` must stay 0; C4 of [DECISION-ROUND.md](../DECISION-ROUND.md) → over budget means Qt work above vvvv's frame interval, one budget; C7 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the F2 series is labelled `work`, the word of `WorkMs`
- **Effort:** M, mechanical: no
- **Touches:** `perf.h`, `perf.cpp`, `vlqt.h`, `Native.cs`, `PerfStats.cs`, `docs/diagnostics.md`, `PerfStatsTest.cs`, `PerfOverlayTest.cs`
- **Depends on:** NDIAG-10 (names)
- **Verification:** `PerfStatsTest` with a synthetic sample that has a long vsync wait and a short render: counter does not increment. `PerfOverlayTest.VSyncWindowLabels` extended: VSync-on window, static scene, `over budget` stays 0.
- **Wave 1 (W1-15):** confirmed by `PerfOverBudget` (`vlqt_perf_eval` counts all 20 synthetic pumps of 18 ms with 14 ms VSync wait and 4 ms Qt work as over budget). The live symptom was not seen on this machine (RTX 5080, 75 Hz screen, VSync window, host pump every 16 ms and every 4 ms): VSync wait about 0.03 ms, no pump over budget, counter 0; the static-scene check of the decision passes (counter 0 over 60 pumps). The defect is in the definition; how often it shows depends on where the platform waits.

### NDIAG-02 `Fps`/`FrameMs` and the phase averages are defined differently from the percentiles and from the F5 page
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes: `Fps`, `FrameMs`, `FrameMsMax`, `SyncMs`, `RenderMs`, `PresentMs`, `VsyncMs` of `PerfStats` change for on-demand scenes
- **Where:** `perf.cpp:413-460` (`summary`), `perf.cpp:912-915`, `perf.cpp:926`, `perf.cpp:537-540`, `PerfStats.cs:6-11`
- **Finding:** The percentiles use only *consecutive* frame intervals (`consecutiveFrame`, because after idle pumps the interval spans the idle time and is not a frame time). `summary()` (feeding `PerfStats.Fps/FrameMs/FrameMsMax` and the F2 meter) averages every `s.frame > 0`, including the first interval after idle pumps. For an on-demand scene (static scene, animation that stops) the average and the max are the idle time, so the F2 meter shows e.g. "2 fps" and `FrameMsMax` shows seconds, while the percentiles are fine. The F5 Summary even shows both (`frameAvg` for `fps` and the colour, `rowAvg` for the row). The phase averages differ too: `summary()` divides by pumps *with a frame* (per swap), the F5 table divides by *all* pumps (`na`, idle included), so "render avg" on F5 and `RenderMs` in the patch disagree by the idle share.
- **Proposal:** one computation `PerfWindow { frameAvg, frameMax, phaseAvg[], pumpAvg, gpuAvg, ... }` built once from a range of samples with the rule "frame statistics from consecutive frames; phase averages per pump that rendered", used by `summary()`, the F2 text and the F5 page. Document the rule in the glossary (NDIAG-10). In `PerfStats.cs`, `Fps` is then `1000 / FrameMs` over consecutive frames, and 0 (not a misleading low number) when the scene is idle.
- **Effort:** M, mechanical: no
- **Touches:** `perf.h`, `perf.cpp` (→ `perf_stats.cpp`, `perf_pages.cpp`), `PerfStats.cs` docs, `PerfStatsTest.cs`
- **Depends on:** NDIAG-04
- **Verification:** `PerfStatsTest`: idle gap in the synthetic samples does not change `frame_ms`; `PerfOverlayTest` on-demand case (`PerfOverlayTest.cs:364`).

### NDIAG-03 Scene page counts element types ≥ 16 as "other"; shader, QML, shape and handlers are never named
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `perf.cpp:1084-1090`, `perf.cpp:1041-1054`, `protocol.h:299-322`
- **Finding:** `byType[16]` is indexed with `type < 16 ? type : 0`. `VLQT_TYPE_SHADER_EFFECT` (24), `QML` (25), `SHAPE` (26) and all handlers (32-38) are therefore added to index 0, which `typeName` prints as "other". The `case VLQT_TYPE_SHADER_EFFECT: return "shader"` in `typeName` can never be reached. Button, SpinBox, CheckBox, Switch, Slider and TextField (10-15) print as "other" too, because `typeName` has no case for them. The list is the F5 Scene page's per-type count, so on a scene with Shapes or ShaderEffects the line is wrong.
- **Proposal:** count in a `std::map<quint16,int>` (or a small sorted vector) built at 4 Hz, and take the name from one table shared with the rest of QtNative (a `vlqt::typeName(quint16)` next to the `VLQT_TYPE_*` enum in `protocol.h`'s C++ counterpart, also useful for error messages). No fixed-size array.
- **Decided:** TARGET 2.7, O5, A5 → no separate `vlqt::typeName`: the name is `TypeDef::name` of the one table of element types (NELM-04); the type names are `VLQT_ELEMENT_TYPE_SHADER` (was `VLQT_TYPE_SHADER_EFFECT`) and `GROUP` (was `ITEM`) after the break, so the Scene page prints "shader" for the shader element
- **Effort:** S, mechanical: no
- **Touches:** `perf.cpp` (`scenePage`), the file that gets `typeName` (e.g. `elements.h`)
- **Depends on:** none
- **Verification:** `vlqt_test_perf_text(page 2)` in `PerfOverlayTest` on a scene with a Shape and a ShaderEffect: the text contains `shape 1 shader 1`, not `other 2`.

### NDIAG-04 `perf.cpp` does five jobs; cut it into five files
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `perf.cpp` (whole), `perf.h`
- **Finding:** Parts by line range: sampling and ring (`:293-420`, `:654-780`, ~250 lines); pure statistics (`:486-652`, ~170); text pages and their colours (`:872-1351`, ~480, plus the helpers at `:133-285`); `PerfGraph` (`:1355-1481`, ~130); `PerfTimeline` (`:1485-1763`, ~280); plus the 65-line QML overlay (`:64-129`) and the DXGI tearing query (`:155-177`). They share only `PerfSample` and a few colour constants. A reader who wants to change a page has to find it between graph painting and counters; the file is unreadable as one unit and it is the one most likely to receive more pages. `perf.h` mixes the public sampling API with the item classes and the test hooks.
- **Proposal:** see section 4. The split is by seam, with no behaviour change; do it as its own step before NDIAG-01/02.
- **Decided:** S2, D8, TARGET 2.3, 3.3 → the files are in `src/VL.Qt.Native/diagnostics/`: `perf.h`, `perf_stats`, `perf_monitor`, `perf_pages`, `perf_items`, `perf_exports.cpp` (the perf exports leave `vlqt.cpp`), `overlay_software.h`, `overlay_geometry.h`, `debug_overlay` (was `debug.*`); the overlay QML is `qml/perf/perf_overlay.qml`; the cut runs in the second round of native cuts, after NAPP-01; B12 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the order of TARGET.md 3.3 binds: second round, after NAPP-01
- **Effort:** M, mechanical: yes (moving code; includes and `CMakeLists.txt`)
- **Touches:** `src/VL.Qt.Native/perf*.cpp/.h`, `CMakeLists.txt`, `vlqt.cpp` includes
- **Depends on:** none (do it first)
- **Verification:** `tools/build.ps1 -Test`: same SmokeTest text assertions (`vlqt_test_perf_text`) pass unchanged.

### NDIAG-05 Three copies of "paint into an image for the Qt Quick software backend"; one workaround as a property
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `perf.cpp:1447-1481` (graph), `perf.cpp:1726-1763` (timeline), `debug.cpp:337-364`, `perf.cpp:793-797` + `perf.cpp:64-129` (`layered`)
- **Finding:** `PerfGraph`, `PerfTimeline` and `DebugOverlay` each contain the same ~25 lines: create the image node, size an image by DPR, fill transparent, paint, `createTextureFromImage`, `setTexture`, `setRect`, `markDirty`. The software backend is a supported fallback (`vlqt.cpp:671-683` selects it when no GLX is found; `test-linux.sh --software`, `Program.cs:450`), so the paths must stay, but they should exist once. The comment at `perf.cpp:300` says "software rendering is not supported", which contradicts that.
- **Proposal:** one helper `SoftwareImageNode` (header `overlay_software.h`, no moc): `QSGNode* paintToImage(QQuickItem* item, QSGNode* old, QImage& cache, F paint)` that does everything except `paint(QPainter&)`. Each item keeps only its `QPainter` body. Add a helper `isSoftwareBackend(QQuickWindow*)` (the `rendererInterface()->graphicsApi() == Software` test appears 4× in the area: `perf.cpp:795-796`, `:1403`, `:1613`, `debug.cpp:312`). Replace the stale comment at `perf.cpp:300` with what is true (GPU rendering is the supported mode; the software backend is a fallback that gets a warning and this reduced drawing).
- **Effort:** S, mechanical: no
- **Touches:** `perf.cpp` (items), `debug.cpp`, new `overlay_software.h`
- **Depends on:** NDIAG-04
- **Verification:** `tools/test-linux.sh --software` grabs of the F2/F5 pages and the debug overlay look as before; on Windows the RHI path is unchanged.

### NDIAG-06 Monitor state is touched from a scene's render thread without a defined contract (to confirm)
- **Kind:** threading
- **Severity:** should
- **Breaking:** no
- **Where:** `perf.cpp:303-305`, `:369-380` (`detectRenderer`), `:813-818` (`updateRenderingBadge`), `:55-56`, `:131`, `:203`, `:1374-1395`, `:1490-1498`, `:1528-1608`; `perf.h:170-177`, `:179-180`
- **Finding:** For offscreen (QtTexture) scenes the frame signals run on the scene's render thread (`perf.h:170`). Most of that is handled (the `m_pending` → `m_done` hand-off under a mutex). Some is not:
  1. `detectRenderer` is connected `DirectConnection` to `sceneGraphInitialized` and a single-shot `frameSwapped`. For a scene it runs on the render thread, writes `m_software` (a `QString`) and `m_rendererKnown`, and calls `m_overlay->setProperty("rendering", …)`, a property write on a `QQuickItem` from a non-GUI thread, while `systemPage()` reads `m_software` on the GUI thread (`perf.cpp:1160-1165`). Unsynchronised `QString` assignment against a read is a data race.
  2. `s_softwareWarned` (`:56`) is a plain `bool` set from whichever thread detects first; with a pool of render threads (`VLQT_OFFSCREEN_THREAD=n`) two threads can warn twice.
  3. `PerfGraph/PerfTimeline::updatePaintNode` run on the render thread for a scene and read `m_ring`, `m_frozenRing`, `m_count`, `s_hostSegments` and `m_flags` that `endPump` writes on the GUI thread. This is safe only if `QQuickRenderControl::sync()` runs with the GUI thread blocked, as `debug.h:66` ("render thread, GUI blocked") also assumes for the debug overlay. I did not read `offscreen.cpp`, so I cannot confirm that the host thread is really blocked during sync and not already inside the next pump.
- **Proposal:** (a) in `detectRenderer`, compute the name where it runs but publish it with `QMetaObject::invokeMethod(this, …, Qt::QueuedConnection)` so that `m_software`, `m_rendererKnown`, the QML property and the warning all happen on the GUI thread; make `s_softwareWarned` a `std::atomic<bool>` (or move the whole detection to the device layer, NDIAG-08). (b) Write the threading contract once, as a block comment at the top of `perf_monitor.h`: which members belong to the signal thread (`m_pending`, `m_t*`), which to the GUI thread (everything else), which are shared (`m_done` under `m_doneLock`), and that item painting reads the ring only inside sync. (c) Confirm (3) in `offscreen.cpp` and say so in that comment.
- **Decided:** E6, E7, P7, TARGET 2.7, PLAN-CHECK wave 1 → `VLQT_OFFSCREEN_THREAD` is gone: the pool size is the pin `Render Threads` of QtTexture and QtRenderer, so the 100-frame verification sets that pin from C# (pool-2 run of E7); the detection moves with NDIAG-08 into `device/renderer_info.*`, the second way of (a); the finding is one of the suspicions that wave 1 confirms or drops
- **Effort:** S, mechanical: no
- **Touches:** `perf.cpp` (→ `perf_monitor.cpp`), `perf.h`, `offscreen.cpp` (read only)
- **Depends on:** NDIAG-04
- **Verification:** a QtTexture scene with the F5 overlay open under a stress loop (`QtTexturePerfTest.cs`) with the thread sanitizer on the Linux build; a 100-frame run with `VLQT_OFFSCREEN_THREAD=2` and two scenes shows one software warning at most.
- **Wave 1 (W1-15):** dropped, by code reading only (no run can show the interleaving from C#, and no TSan build was run; evidence: medium to strong). `offscreen.cpp` runs every render-thread job that touches the monitor beyond `m_pending`/`m_done` as a blocking call (`onRenderThread` -> `RenderThread::call`, `BlockingQueuedConnection`): `renderControl->initialize()`, which emits `sceneGraphInitialized` -> `detectRenderer`, at create, bind and device restore, and `beginAndSync` (sync = `updatePaintNode` of PerfGraph/PerfTimeline) in `renderThreaded`. The main thread waits in each, so (1) and (3) never overlap a GUI-thread read, and (2) cannot warn twice because the main thread starts the initializations of a pool one after the other. The posted job (render, endFrame) touches only `m_pending` and `m_done` under `m_doneLock`. Left: `detectRenderer` writes a QML property of a GUI-thread item from the render thread while the GUI thread waits, which is an affinity break, not a race; the contract comment of (b) is still useful with NDIAG-04.

### NDIAG-07 F2, F5, F6 and F7 are swallowed in every window and cannot be turned off
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: a new Renderer option; default behaviour unchanged
- **Where:** `perf.cpp:740-776`, `Native.cs:239` (`vlqt_window_set_overlay` has no C# caller)
- **Finding:** `PerfMonitor` installs an event filter on every window and returns `true` for F2 and F5 (and F6/F7 while the stats are visible). A patch that uses these keys (an installation with a keyboard, a QML TextField shortcut) never receives them, and there is no switch. The overlay state is reachable only by those keys; a patch cannot open the F5 page by a pin, even though `vlqt_window_set_overlay` exists.
- **Proposal:** two options on `Renderer [Qt]`: `Diagnostic Keys` (bool, default true; false removes the event filter) and, if wanted, an optional `Overlay` pin (enum None/Meter/Stats) that calls `vlqt_window_set_overlay`. In QtNative, `PerfMonitor::setKeysEnabled(bool)` installs/removes the filter. If the answer to question 1 below is "no pin", delete the unused C# import `vlqt_window_set_overlay` or keep it for tests only and say so.
- **Decided:** C18 → the pin is `Enable Keyboard Shortcuts` (bool, default true) on the Renderer, named as in VL.Stride's renderer; false removes the event filter, disables every diagnostic key (F2, F5, F6, F7) and passes the keys to the patch; no `Overlay` pin (b not taken); `Renderer.cs` is `Nodes/RendererNode.cs` (RUN-06); C5 of [DECISION-ROUND.md](../DECISION-ROUND.md) → `vlqt_window_set_overlay` moves to `vlqt_test.h` if a test uses it, otherwise it is deleted
- **Effort:** S, mechanical: no
- **Touches:** `perf.cpp`/`perf_monitor.cpp`, `vlqt.cpp`, `vlqt.h`, `Native.cs`, `Renderer.cs`, `help/Qt/HowTo Read Performance.vl`
- **Depends on:** NDIAG-04
- **Verification:** SmokeTest with `vlqt_test_send_key(F5)` after `set_keys(false)`: no overlay, key reaches the scene.

### NDIAG-08 Renderer detection and its warning do not belong to the perf monitor
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `perf.cpp:33-51`, `:155-177`, `:369-380`, `perf.h:27-29`
- **Finding:** `softwareRendererName`, the `vlqt.gpu` logging category, the one-time "SOFTWARE RENDERING" warning and `dxgiAllowTearingSupported` are about the GPU/backend, not about timing. They sit in `perf.cpp` because the F5 System page and the CPU/GPU badge need them. A person looking for "why do I get the software warning" has no reason to open `perf.cpp`. `softwareRendererName` is used only in `perf.cpp` (search over `src/VL.Qt.Native`).
- **Proposal:** move them into `device.cpp` (next to the shared D3D11 device) or a new `renderer_info.{h,cpp}`: `RendererInfo describeRenderer(QQuickWindow*)` returning backend name, adapter, device type, `software` name, `allowTearing`. The System page then only formats that struct. The warning is emitted by the code that creates the window.
- **Decided:** TARGET 2.7 → `device/renderer_info.h/.cpp` (not `device.cpp`)
- **Effort:** S, mechanical: no
- **Touches:** `perf.cpp`, `perf.h`, `device.cpp` or new file, `vlqt.cpp`
- **Depends on:** NDIAG-04, NDIAG-06
- **Verification:** `PerfOverlayTest` System page text unchanged; Linux `--software` still logs the warning once.

### NDIAG-09 Debug overlay is not excluded from the scene-change check, and does not follow layout changes without an apply
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `vlqt.cpp:455-470` (`isPerfOverlay` by object name), `debug.cpp:178-204`, `commands.cpp:309-327`, `vlqt.cpp:1807`
- **Finding:** (a) The perf overlay is named so that its changes are no scene change (UpdateGate). The debug overlay has no such name, but it repaints through `update()` on every `refresh()`; whether `contentChildrenChanged()` / the dirty check count that depends on code outside my area. Suspicion, to confirm: with the Debug pin on, a static scene keeps rendering (on-demand rendering lost) or not. Confirm by watching `idle` on the F5 Summary with Debug on. (b) `refresh()` is called after an apply and when the content item is resized; nothing calls it when a Row/Column re-lays out or a Text changes its implicit size after a font loaded, without a new command. The outlines would then show the old geometry until the next apply (suspicion: confirm with a Text whose font loads late or a Row containing an Image that finishes loading).
- **Proposal:** give the overlay an object name constant `debugOverlayName` next to `perfOverlayName` in one header, and treat both as "overlay items" in `UpdateGate`. For (b) connect `refresh()` to the polish of the items shown: simplest is `update()` once per pump while active (the overlay is for debugging, and the cost is documented), via the same path as `UpdateGate`, instead of "after apply".
- **Effort:** S, mechanical: no
- **Touches:** `debug.cpp`, `debug.h`, `vlqt.cpp` (`UpdateGate`), `commands.cpp`
- **Depends on:** none
- **Verification:** `DebugOverlayTest`: Row with a child whose size changes by a QML property binding; grab shows the new frame without a new apply.

### NDIAG-10 Overloaded and undefined words; no glossary of the measured values
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: renames in `vlqt.h`/`Native.cs`/`protocol`, and `PerfStats` members (see NDIAG-11)
- **Where:** `perf.h:31-48`, `vlqt.h:78-98`, `PerfStats.cs`, `docs/diagnostics.md:22-24`, `perf.cpp:462-468`, `QtApp.cs:179-182`, `perf.h:31` (comment says "NaN = no frame", the code uses 0)
- **Finding:** A contributor cannot tell what a number means without reading three files.
  - **"frame"** means three things: the Qt swap interval (`PerfSample.frame`), the vvvv frame (`vlqt_host_frame`, "since the previous host pump") and the host's `FrameMs`. `PerfSample.frame` is an *interval*, not a duration.
  - **"pump"** is this project's word for one call of `vlqt_app_process_events`; `PerfStats.cs:6-8` explains it, `perf.h` does not.
  - **"budget"** has three definitions: native `budgetMs()` = 1000 / the window's screen refresh (`perf.cpp:462`); C# `budgetMs` = the delta of the vvvv frame clock (`QtApp.cs:180`); the log warning fires above 0.5 × budget of *work* (`QtApp.cs:446`). The native counters use the first, the warning the second.
  - **"events"** is derived by subtraction (`pump − vsync − sync − render − present`, `perf.cpp:401`), so with several windows it contains the other windows' frames (said only in `perf.h:40-42`).
  - `perf.h:31` says NaN, the code uses 0. `hostReported` and "0 = unknown" are used for two different things in the host struct (`vvvv` 0 = unknown, `apply` 0 = real zero).
- **Proposal:** (1) a table in `docs/diagnostics.md` "What the numbers mean": one row per value (name in the overlay, in `PerfStats`, in the C struct), start signal, end signal, what is included/excluded, the budget it is compared with. (2) Renames: `PerfSample.frame` → `frameInterval`; `vlqt_host_frame` → `vlqt_host_pump` (and `vlqt_host_stats` → `vlqt_host_pump_stats`); one name for the budget: `refreshBudgetMs` (screen) and, in C#, `FrameClockMs`; the warning keeps its own constant `WarnAtWorkShare = 0.5`. (3) fix the NaN comment. (4) a unit sentence on every field in `vlqt_perf`, `vlqt_perf_stats`, `vlqt_host_frame` (most have one; `vlqt_perf` should be checked).
- **Decided:** A5, O6, TARGET 1.6, E13 → `vlqt_host_frame` is `vlqt_host_pump` (the struct), `vlqt_window_report_host_frame` and `_get_host_frame` are `vlqt_scene_report_host_pump` and `_get_host_pump`, the C# meter is `HostPumpMeter`; `PerfSample.frame` → `frameInterval` (O6); `OverBudget` counts Qt work only (E13); C4 of [DECISION-ROUND.md](../DECISION-ROUND.md) → one budget, vvvv's frame interval (QtNative renders in step with vvvv, E4); one field instead of `refreshBudgetMs` and `FrameClockMs`
- **Effort:** M, mechanical: partly (the renames)
- **Touches:** `perf.h`, `perf*.cpp`, `vlqt.h`, `vlqt.cpp`, `Native.cs`, `PerfStats.cs`, `QtApp.cs`, `WindowRenderer.cs`, `docs/diagnostics.md`, tests that name the struct
- **Depends on:** NDIAG-04
- **Verification:** `--drift` (protocol/struct mirror), `dotnet test`, `PerfStatsTest`.

### NDIAG-11 `PerfStats`: a 41-member record with hand-written `Split*` methods
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: `PerfStats` pins and Split nodes change; `help/Qt/HowTo Read Performance.vl` is updated
- **Where:** `PerfStats.cs:12-155`
- **Finding:** 11 positional members + 30 `init` properties; six `Split*` methods exist only because VL imports the primary constructor's parameters as Split pins but not the init properties (`PerfStats.cs:90-92`). `SplitHost` has nine outputs, `SplitCounters` ten. The members are in six unrelated groups (Qt timing, percentiles, counters, recent counters, host, GC), the comments carry issue numbers (`#245`, `#260`, `#270`), and the summary of the record is mostly a definition of "pump". The `.cs` mirror of each native field is a second place to update on every addition (`From` has 40 assignments).
- **Proposal:** split by meaning into small records, each with a primary constructor so that VL generates its Split pins and the `Split*` methods go away:
  `PerfStats(Fps, FrameMs, FrameMsMax, PumpMs, PumpMsMax, WorkMs, SyncMs, RenderMs, PresentMs, VsyncMs, GpuMs, GpuMsMax, SwapsPerPump)` with the properties `Percentiles` (`PerfPercentiles`: Budget, frame/pump p50/p95/p99), `Counters` (`PerfCounters` since start) and `Recent` (`PerfCounters` last 10 s), `Host` (`HostPerfStats`: vvvv, wait, apply, reconcile, ops) and `Gc` (`GcPerfStats`). One `PerfCounters` type replaces the 10-argument `SplitCounters`. Alternative: keep one flat record and generate the Split methods with a source generator; not recommended (two shapes to maintain). Order: Qt timing first, then the optional detail records.
- **Decided:** C17, P22, TARGET 2.2, 2.7 → (a), grouped records, as proposed, and the `Perf` output keeps updating every frame; one issue with RUN-10; one file per record in `src/VL.Qt/Window/` (`PerfStats.cs`, `PerfPercentiles.cs`, `PerfCounters.cs`, `HostPerfStats.cs`, `GcPerfStats.cs`), not `PerfDetails.cs`, namespace `VL.Qt`
- **Effort:** M, mechanical: no
- **Touches:** `PerfStats.cs` (→ `PerfStats.cs`, `PerfDetails.cs`), `WindowRenderer.cs`, `Renderer.cs`, `help/Qt/HowTo Read Performance.vl`, `PerfStatsTest`/unit tests
- **Depends on:** NDIAG-10
- **Verification:** `help/Qt/HowTo Read Performance.vl` through `vl-patch-check.py` and `vvvvc.ps1`; unit test that `From` maps every native field (reflection over the struct).

### NDIAG-12 Duplicated logic inside the perf items and pages
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** peaks: `perf.cpp:1010-1021` and `:1549-1566`; timeline scale: `:1490-1499` and `:1536-1543`; "carry the last non-zero value" series: `:1386-1395`, `:1595-1607`; y mapping: `:1421`, `:1462`, `:1543`; `rgba()`/`setVertex`/`qPremultiply` repeated in `debug.cpp:55-71` and `perf.cpp:1518-1523`, `:1621-1648`; hard-coded colours: `kGpuColor` etc. at `:142`, `kGraphSeries` at `:1365` (same hex values twice)
- **Finding:** The three largest bars are found twice (once as text, once as markers) and the timeline's top scale is computed in `scaleFor` and again in `buildPrims` ("top as scaleFor()", `:1535`). Each copy has to be changed together; the comment at `:1535` says so, which is the sign. The colours of the graph series and the phase colours are written once as `QRgb` and once as an HTML string, both by hand.
- **Proposal:** in the timeline unit: `struct TimelineView { float top; std::array<int,3> peaks; float maxStack; }  TimelineView analyze(const PerfMonitor&)` used by the page footer, `scaleFor` and `buildPrims`. In the graph unit: `forEachCarried(series, f)` (already `graphSeries`) used by the timeline GPU line too. One `struct Color { QRgb rgb; const char* html; }` constructed with a constexpr function from three ints so the hex string is derived (or computed once at start-up with `QColor::name()`), replacing `Rgb` and the duplicate lists. One `OverlayVertex` helper (`setVertex`, premultiply) shared by perf items and `debug.cpp` (`overlay_geometry.h`).
- **Decided:** S2, TARGET 2.3 → graph and timeline are one file, `diagnostics/perf_items.cpp` (not `perf_graph.cpp` and `perf_timeline.cpp`); `overlay_geometry.h` and `perf_pages.cpp` are in `diagnostics/`
- **Effort:** M, mechanical: no
- **Touches:** `perf_timeline.cpp`, `perf_graph.cpp`, `perf_pages.cpp`, `debug.cpp`, new `overlay_geometry.h`
- **Depends on:** NDIAG-04
- **Verification:** `PerfOverlayTest` text and grab checks unchanged (peaks line, scale in the Timeline header).

### NDIAG-13 Page columns rely on the font `Consolas`
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `perf.cpp:86`, `:93`, `:109`, `:117`, `:119` (`font.family: "Consolas"`), `Html` (`:233-260`, spaces as `&nbsp;`)
- **Finding:** All column alignment of the pages is done with spaces and right-justified strings and works only with a monospaced font. `Consolas` is a Microsoft font: on Linux (the supported second platform) Qt falls back to a proportional default and the tables are ragged. Not verified on Linux (no run), but `docs/linux.md` is the place to check how fonts are provided.
- **Proposal:** use the system fixed font: set the QML `font` from `QFontDatabase::systemFont(QFontDatabase::FixedFont)` through the `kOverlayQml`'s `fontFamily` property set in `ensureOverlay`, or `Qt.font({family: ...})` with a list of families (`"Consolas, DejaVu Sans Mono, Menlo, monospace"` as `font.families`, Qt 6.2+).
- **Effort:** S, mechanical: no
- **Touches:** `perf.cpp` (overlay QML)
- **Depends on:** none
- **Verification:** `test-linux.sh` grab of the F5 Summary page: columns aligned.

### NDIAG-14 The test hooks live inside the production monitor and in the per-frame path
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `perf.cpp:308`, `:317`, `:321`, `:327`, `:332`, `:341`, `:722-738` (`injectSpike`/`spin`), `perf.h:105-108`, `:127-135`, `:190-191`; `vlqt.cpp:2085-2125`
- **Finding (K6 for this area):** `m_spikePhase`/`m_spikeMs` (atomics), `injectSpike`, `spin`, `Phase` as a test enum, `statsText()` and `pageText()` exist for the SmokeTest. The cost is not the code but the six `m_spikePhase.load()` calls on every frame of every window in production, and the `Phase` order constraint ("vsync last to keep the numbers of `vlqt_test_perf_spike`", `perf.h:105-107`). `vlqt_test_perf_stats_text` is redundant with `vlqt_test_perf_text(page 0)`.
- **Proposal:** `injectSpike(phase, ms)` connects a one-shot lambda to the signal that ends that phase and disconnects itself after the spin; with nothing injected the hot path has no test code. Then `m_spikePhase/m_spikeMs/spin` and the six checks disappear. The test enum `Phase` stays only as the *phase identity* the pages use (rename `Phase` → `QtPhase`, drop the "numbers of the spike test" constraint by renumbering the test). `statsText()` and `vlqt_test_perf_stats_text` are deleted; the test reads `vlqt_test_perf_text(0)`. `pageText()` stays (it is the way to test the pages without an overlay); put it in `perf_pages.cpp` as a free function `perfPageText(const PerfMonitor&, int page)`.
- **Decided:** F1, F5, P22, TARGET 2.3 → one issue with the other K6 findings under TEST-07; the test exports that stay are named `vlqt_test_perf_*` and declared in `vlqt_test.h`; `perfPageText` is in `diagnostics/perf_pages.cpp`; `PerfOverlayTest` is an NUnit test
- **Effort:** S, mechanical: no
- **Touches:** `perf.cpp`, `perf.h`, `vlqt.cpp`, `vlqt.h`, `Native.cs`, `PerfOverlayTest.cs`
- **Depends on:** NDIAG-04
- **Verification:** `PerfOverlayTest` spike cases (events/sync/render/present/vsync) still attribute the spike to the injected phase.

### NDIAG-15 With no `Perf` consumer, C# still reads the native summary every pump
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `WindowRenderer.cs:178-190` (read every pump), `WindowRenderer.cs:108-132` (the `Perf` getter reads stats lazily already), `perf.cpp:413-460`
- **Finding:** `AfterEvents` calls `vlqt_window_get_perf` on every pump to detect changes by a byte compare, although the `Perf` output is optional and the getter is already lazy for the percentiles. `summary()` loops the last 60 samples twice (`:416-447`) and goes through `guarded()` (try/catch) and P/Invoke. It is a microsecond, but it runs for every window of every patch that never connects `Perf`, and it is the only always-on diagnostic cost that is not needed.
- **Proposal:** make `Perf` read all of it on demand (`_perfChanged = true` on every pump, no native call), cache by `_perfPump` counter so that repeated reads in a frame cost nothing. Drop the byte-compare. If `Perf` is not read in a frame, no native call happens. Keep one `vlqt_window_get_perf_sample` for the warning (already on demand).
- **Effort:** S, mechanical: no
- **Touches:** `WindowRenderer.cs`
- **Depends on:** none
- **Verification:** unit test that a frame without a `Perf` read makes no native call (a counter is not needed: use a fake `INativeWindow` if one exists; otherwise the SmokeTest `PumpTest` plus a profiler run, `docs/profiling.md`).

### NDIAG-16 Dead or test-only members
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `perf.cpp:547`/`:587` (`first`), `perf.h:55`/`:71` (hard-coded `-1` list), `perf.h:137`/`:123-125`, `vlqt.h:78-98`, `perf.cpp:621-626`, `perf.h:24`, `:100`
- **Finding:**
  1. `perfStats`: `first = max(0, n - kHistory)` is always 0: every caller passes `n ≤ kHistory` (`stats()` passes `m_count`, `summaryPage` ≤ 240, `perfEval` already cuts). The same cut exists in `perfEval:587`. One of the two is dead.
  2. `m_bucketSec{-1,…}` lists ten values by hand, so a change of `kRecentSeconds` silently initialises the rest to 0 (bucket "second 0").
  3. `vlqt_host_stats.host_ms`, `host_ms_max`, `alloc_bytes_max` and `vlqt_host_frame.host` have no reader outside `PerfStatsTest.cs:88` (C# `PerfStats` does not read them; the F5 pages do not show them). `ops_max` is read by the Scene page only.
  4. `PerfMonitor` has `Q_OBJECT` but no signals, slots or properties (it uses lambdas and a function-pointer `connect`); `PerfGraph`/`PerfTimeline` need it for `qmlRegisterType`.
  5. The F2 meter reads the live ring, ignoring the freeze (`PerfGraph` uses `m->sample(i)`, `:1390`), while the timeline uses `viewSample`. Probably unintended: F6 "freeze" does not freeze the meter.
- **Proposal:** delete (1) the second cut; (2) fill with a loop in the `PerfCounters` constructor; (3) remove the unread fields from the C structs and `Native.cs` (or show them in the Summary table if they are wanted: `host` and `alloc` per pump are useful in a spike report); (4) drop `Q_OBJECT` from `PerfMonitor`; (5) decide in question 3.
- **Decided:** E14, A5 → item 5 needs no change: F6 freezes only the F5 pages and the F2 meter keeps running on the live ring (decision no); `vlqt_host_frame` is `vlqt_host_pump`
- **Effort:** S, mechanical: yes
- **Touches:** `perf.cpp`, `perf.h`, `vlqt.h`, `Native.cs`, `PerfStatsTest.cs`
- **Depends on:** NDIAG-04
- **Verification:** `--drift`; `PerfStatsTest`.

### NDIAG-17 Private Qt API for one row of the Scene page
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `perf.cpp:6`, `:1105-1108`
- **Finding:** `QUnifiedTimer::instance(false)->runningAnimationCount()` (`qabstractanimation_p.h`) feeds the row "animations … running (process)". It is a private header with a non-obvious argument (`false` = do not create), and the value is process-wide, not per window, although it is on a per-window page. Every Qt minor update can break the build for one informational row. `CMakeLists.txt:63` already keeps a list of private dependencies "check on Qt updates"; this is not in it.
- **Proposal:** drop the row, or keep it and add the include to that list with the reason. Recommended: drop it; "QML items" and "effects" already show the scene's cost, and a running animation shows up in the Timeline as frames.
- **Decided:** D9 → if the row stays, `qabstractanimation_p.h` goes into the list of private Qt classes in the head comment of `elements.h`, not into the comment at `CMakeLists.txt:63`; C6 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the "animations" row is dropped
- **Effort:** S, mechanical: yes
- **Touches:** `perf.cpp` (`scenePage`), `PerfOverlayTest.cs` (if it asserts the row)
- **Depends on:** NDIAG-04
- **Verification:** `PerfOverlayTest` Scene page text.

### NDIAG-18 Debug overlay: per-frame property lookups and font metrics for every node
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `debug.cpp:157-162` (`prop()` by name, four per Row/Column/Text per build), `debug.cpp:273` (`QFontMetricsF` + `property("font").value<QFont>()` per Text per build), `debug.cpp:221-228` and `:246-248` (`m_scene.ids.count(c)` per child, twice per container)
- **Finding:** With the Debug pin on, every `update()` rebuilds everything and each `item->property("leftPadding")` is a meta-object lookup by string. For "all elements" on a scene of thousands of nodes the overlay itself becomes the slow part. The overlay is a debugging aid so the cost only matters when it is on; the docs say nothing about this cost, and the F5 "Overhead" paragraph covers only the perf overlay.
- **Proposal:** (a) cache the padding values per `Node` (the element code sets them; read them at apply, not by name at paint) or at least resolve the four property indices once with `QMetaObject::indexOfProperty` per item type; (b) build the `QFontMetricsF` once per Text per font change (the apply knows the font); (c) one pass over `childItems()` for both children extent and spacing gaps. Add one sentence to `docs/diagnostics.md` ("with Debug on for all elements, painting is O(elements) per frame").
- **Decided:** O3, S2 → the per-element cache sits on the `ElementEntry` (was `Node`) of the scene's table, `scene/commands.h`
- **Effort:** M, mechanical: no
- **Touches:** `debug.cpp`, `commands.h` (Node cache), `docs/diagnostics.md`
- **Depends on:** none
- **Verification:** `DebugOverlayTest` grabs identical; measure a 3000-element scene with Debug all on.

### NDIAG-19 Comments: issue numbers and one 65-line QML string in C++
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `perf.cpp:64-129` (QML), many `(#…)` comments (K1, not listed); long comment blocks at `perf.cpp:309-315`, `:1680-1685`, `:1445-1446`; header comment of `debug.h:1-24`
- **Finding:** The QML text is an item with logic (flags, pages, bar). It is in the middle of `perf.cpp` in a raw string, so no editor highlights it. The comments at `:309-315` (the vsync-wait explanation with qtbase line numbers) and `:1680-1685` (what was measured and dropped, with a hardware name) are design notes; the reader needs the conclusion, not the history. K1 applies to the numbers; the text says what to explain instead:
  - `:309-315` → keep two sentences ("the wait for the refresh happens in `QRhi::beginFrame`, between `beforeFrameBegin` and `beforeSynchronizing`; Qt has no signal at its end") and move the rest to `docs/decisions/timing.md`.
  - `:1680-1685` → keep what the four nodes are and why highlights/max-hold are marked dirty only on change; move "measured and dropped" to `docs/decisions/` or `docs/profiling.md`.
  - `debug.h:1-24` is a good place for the legend; keep, and keep it identical to the `Debug` pin tooltip and `diagnostics.md` (three places say the same legend; make `debug.h` the source and generate the tooltip, or accept the copy and say where it is).
- **Proposal:** the QML into `perf_overlay.qml` as a Qt resource if the project already embeds QML resources (otherwise `perf_overlay_qml.h` with the raw string and nothing else).
- **Decided:** D8, G5, P14, TARGET 2.3 → the overlay QML is `src/VL.Qt.Native/qml/perf/perf_overlay.qml`, a Qt resource, no `perf_overlay_qml.h`; the design note is `docs/design/timing.md` (not `docs/decisions/`); the comment rewrites belong to the issue that cuts `perf.cpp` (NDIAG-04)
- **Effort:** S, mechanical: no
- **Touches:** `perf*.cpp`, `docs/decisions/timing.md`, CMake resources
- **Depends on:** NDIAG-04
- **Verification:** none beyond the build.

## 4 Target structure

QtNative, folder `src/VL.Qt.Native/` (no new folders; a prefix `perf_` groups the files):

| File | Content | Change |
|---|---|---|
| `perf_stats.h/.cpp` | `PerfSample`, `PerfCounters`, `PerfWindow` (NDIAG-02), `perfStats`, `perfHostStats`, `perfEval`, `consecutiveFrame`, `percentile`. No Qt headers except none. Pure, unit-testable. | split from `perf.cpp:486-652` |
| `perf_monitor.h/.cpp` | `PerfMonitor`: signal hooks, `m_pending`/`m_done` hand-off, ring, host-frame attachment, overlay flags/page/freeze, key filter, threading contract comment at the top of the header. | split from `perf.cpp:293-420`, `:654-780`; `Q_OBJECT` removed; test spike moved to a one-shot connection |
| `perf_pages.cpp` (+ small `perf_pages.h`) | `Html`, colours, `summaryPage`, `timelinePage`, `scenePage`, `systemPage`, `resourcesPage`, `perfPageText`. Free functions taking `const PerfMonitor&`. | split from `perf.cpp:133-285`, `:872-1351` |
| `perf_items.cpp` + `perf.h` | `PerfGraph`, `PerfTimeline`, `TimelineView analyze(...)`, `registerPerfTypes`. | split from `perf.cpp:1355-1763` |
| `overlay_software.h` | `paintToImage(...)`, `isSoftwareBackend(window)`. | new, from three copies |
| `overlay_geometry.h` | `setVertex`, premultiply helper, `Color` type. | new |
| `perf_overlay.qml` (or `perf_overlay_qml.h`) | the overlay QML | moved from `perf.cpp:64-129` |
| `renderer_info.h/.cpp` or `device.cpp` | `describeRenderer(window)`, the software warning, `dxgiAllowTearingSupported` | moved from `perf.cpp:33-51`, `:155-177`, `:369-380` |
| `debug.h/.cpp` | `DebugOverlay`, unchanged in shape; uses the two overlay headers; `Painter` stays in `debug.cpp` | small changes (NDIAG-05, -09, -12, -18) |
| `elements.h` (or protocol companion) | `vlqt::typeName(quint16)` | new function, NDIAG-03 |

Renames: `PerfSample.frame` → `frameInterval`; `vlqt_host_frame` → `vlqt_host_pump` (and `_stats`); `PerfMonitor::Phase` → `QtPhase`; one name for the screen budget (`refreshBudgetMs`). Deleted: `statsText()`, `vlqt_test_perf_stats_text`, `m_spikePhase`/`m_spikeMs`/`spin`, the second cut in `perfEval`, the "animations" row (recommended), `host_ms`, `host_ms_max`, `alloc_bytes_max` (unless used).

C#, `src/VL.Qt/`: `PerfStats.cs` (Qt timing record) and `PerfDetails.cs` (`PerfPercentiles`, `PerfCounters`, `HostPerfStats`, `GcPerfStats`); all `public sealed record`, primary constructors, no `Split*` methods, no issue numbers. `WindowRenderer.Perf` reads native data only when read. Namespace stays `VL.Qt`; public stays public because the types are pins of `Renderer`.

Public C ABI: unchanged in shape except the renames above, `vlqt_window_set_keys` (NDIAG-07) and the removed test export.

## 5 Questions for Sebastian

1. **Should a patch control the overlays?** Today only the keys F2/F5/F6/F7 do, and they are swallowed in every window (NDIAG-07). *Alternatives:* (a) add `Diagnostic Keys` (bool) to the Renderer, nothing else; (b) also an `Overlay` pin (None/Meter/Stats) so installations can show the stats from a patch; (c) leave as is. *Recommendation:* (a), and (b) only if you want the stats on a machine without a keyboard. Risk of (c): a patch with its own F5 shortcut cannot work.
2. **What does "over budget" mean for the user?** (NDIAG-01.) *Alternatives:* (a) Qt work (events+sync+render) above the refresh budget; (b) the whole pump including the VSync wait (today); (c) both, as two counters. *Recommendation:* (a) as `OverBudget`; (c) only if you want to see "Qt waited for the display" as a number, which the vsync-wait row already shows.
3. **Should the F6 freeze also freeze the F2 meter?** Today it does not (NDIAG-16 item 5). *Recommendation:* yes, the meter should use the same view as the pages, so that a screenshot of both is consistent.
4. **Is it acceptable to rename the host-side C struct** (`vlqt_host_frame` → `vlqt_host_pump`) before 1.0? The ABI is private (C# and QtNative are one package), the change is mechanical. *Recommendation:* yes, together with the glossary.
5. **Do you still want the per-page contents** "draw calls: n/a, not exposed by Qt Quick" and "pipelines: ms total creation time" (`perf.cpp:1130-1135`)? One row says "n/a" always. *Recommendation:* remove the draw-calls row; keep pipelines (it shows shader-build hitches).

## 6 For other areas

- **UpdateGate** (`vlqt.cpp:455-470`): the perf overlay exclusion is done by object name, a coupling from `vlqt.cpp` to a constant in `perf.h`; a shared `overlayNames` header (or an item property) would let the debug overlay join (NDIAG-09). Review of `vlqt.cpp`.
- **`replaceWindow`** (`vlqt.cpp:1717`): deletes the `PerfMonitor` and creates a new one, so ring, counters (`Pumps since start`) and host reports are lost whenever the swap-chain format changes; `Pumps` in `PerfStats` is documented as "since the window was created". Review of window/pump code.
- **Offscreen** (`offscreen.cpp:506`): the TODO says a scene has no GPU time ("perf.cpp reads it only from a swap chain"), so `GPU` is always 0 for `QtTexture` scenes, which is the case where the host cares most; also confirm the render-thread sync contract named in NDIAG-06. Review of `offscreen.cpp`.
- **`QtApp.cs:442-453`**: the slow-frame warning mixes native and host phases in one message with 14 placeholders; if NDIAG-10 renames values, the message needs the same names. C# runtime review.
- **`WindowRenderer.cs:201-207`**: `ReportHostFrame` is called after `AfterEvents` for each window; make sure a window created in the same pump has a first sample before the report (it returns `-2` "no pump yet" and the error is only set, never read). C# runtime review.
- **`snapshot_resource.cpp:310`**: another place that special-cases the Qt Quick software backend; shares `isSoftwareBackend` (NDIAG-05) once it exists.
