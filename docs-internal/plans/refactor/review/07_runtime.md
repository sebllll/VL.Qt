# Review 07 – C# runtime: app, pump, renderers

Claude Opus 5.5, 2026-11-19. State: `develop` at 0.9.10.

Read in full: `src/VL.Qt/` QtApp.cs, Renderer.cs, WindowRenderer.cs, OffscreenRenderer.cs, SceneCore.cs, PerfStats.cs, QtLog.cs, WindowPlacement.cs, WindowSettings.cs, InputMapping.cs, ExportedApp.cs, SpaceMapping.cs, Space.cs, NodeErrors.cs, Geometry.cs, Initialization.cs, Categories.cs, Multisample.cs, MultisampleCount.cs, Nodes/WindowSettingsNode.cs; docs/architecture.md, docs/decisions/timing.md.
Skimmed (targeted excerpts): Native.cs (static constructor, declarations), Images/ResourceClient.cs (`ForApp`, `SetPumping`, `TryShutdown`), VL.Qt.Stride/QtTextureRenderer.cs (`DriverSeconds`), VL.Qt.Native/vlqt.cpp (`vlqt_driver_advance`, driver `setTime`), offscreen.cpp (`renderBatch`), perf.cpp (`vlqt_window_get_perf`), src/VL.Qt.Tests/QtPumpTests.cs (fakes), test file list.
Not read: Protocol.cs, CommandWriter.cs, EventReader.cs, the earlier review reports.

## 1 Summary

The runtime works and is well tested at the pump level. One Qt application per process, one `QtPump` per vvvv app (a service of its `AppHost`), copy-on-write participants and an animation driver that only moves forward. Together they make F5/F8 and several apps in one process work. The hot paths are almost allocation-free. The weak points are the error and logging paths, the split of the monotonic-time guarantee, and names that do not say what the types are:

1. **Node messages get lost** (RUN-01): `NodeErrors` holds one message. A second phase overwrites the first, and a recovering phase clears a warning that still applies. 23 nodes use it.
2. **The animation driver can run backwards** with two apps when one of them uses QtTexture (RUN-02). The "never backwards" rule lives in two places, and QtNative does not enforce it.
3. **Qt log messages go to a stale logger factory** after an app stops (RUN-03). The class comment says they are queued; the code never queues them.
4. **Every Renderer allocates every frame** (RUN-04): two `PerfStats` records, plus a `Path.GetFullPath` call when an icon is set.
5. **Names and file cut** (RUN-05, RUN-06): `QtApp.cs` holds five types. `Renderer`, `WindowRenderer`, `OffscreenRenderer` and `SceneCore` mean node, window scene, offscreen scene and shared scene core, but their names do not tell a reader that.

## 2 What works and must survive

- **One `QGuiApplication` per process, never released** (`QtApp.cs:250-307`). Qt cannot create its application twice in one process. Everything that belongs to a vvvv app lives elsewhere.
- **One `QtPump` per vvvv app, a service of its `AppHost`** (`QtApp.cs:116-123`, `allowToAskParent: false`). F5/F8 replace the app host and its frame clock. A process-wide subscription would stay on the dead clock (black window).
- **Frame order.** At the tick: deliver input (`vlqt_app_deliver_input`), then every participant polls. After the patch update: apply all → resources `BeforeEvents` → `vlqt_driver_advance` → `vlqt_app_process_events_for(windows of this app)` → resources `AfterEvents` → participants `AfterEvents` → host-frame report (`QtApp.cs:353-440`, `503-514`). Qt animations see the new values in the same frame, and input reaches the patch in the frame it arrived.
- **Several apps:** each pump renders only its own windows (`CollectWindows`). The driver time only grows: `_offset` and `ResyncSeconds` in `QtPump.Frame` (`QtApp.cs:185-197`), the guard in `PumpFrame` (`QtApp.cs:387`). Tests: `QtPumpTests`, SmokeTest `PumpTest` (restart loop).
- **Copy-on-write participants** (`QtApp.cs:139-160`). A frame reads one array, so a participant may unregister during a callback, and nothing is allocated per frame.
- **Per-phase exception isolation.** Each participant phase is caught in `QtApp` and again at the node (`Renderer.cs:223-291`). One broken window does not stop the others.
- **Scene core shared by window and offscreen scene** (`SceneCore`). It holds the reconciler, the command buffer, resync, apply errors at the element node, and the poll buffer. Both handles go through the same `vlqt_window_*` functions.
- **Change detection** of title, mode, icon, samples, debug, clear color, Clear and Space before anything goes native (`Renderer.cs:149-188`, `SceneCore.cs:82-101`).
- **The QtLog callback never throws into native code** (`QtLog.cs:79-90`). The delegate stays rooted in a static field (`QtLog.cs:30`).
- **Device loss** has no C# path for windows; QtNative handles it. Offscreen scenes report it through `IsBound == false`, and the caller (VL.Qt.Stride) binds again. Keep it this way: C# must not cache device state.
- **Hot paths** (no allocation per frame, keep it so): `QtPump.Frame`/`Tick`, `QtApp.PumpFrame`/`DeliverInput`/`CollectWindows`, `Renderer.Update`, `WindowRenderer.Update`/`ApplyCommands`/`AfterEvents`/`PollInput`/`ReportHostFrame`, `SceneCore.Update`/`Apply`/`ReadEvents`/`Dispatch`, `OffscreenRenderer.Notify`/`Send*`/`RenderBatch`. Broken today: RUN-04.

**Process-wide and static state of the area** (all checked):

| State | Where | Lifetime | Verdict |
|---|---|---|---|
| `QtApp.s_instance` | `QtApp.cs:258` | process, created on first `Get` | correct; a failed init is retried on every `Get` (RUN-11) |
| `QtLog.s_target`, `s_pending`, `s_dropped`, `Fn` | `QtLog.cs:30-35` | process; target replaced by the last app that creates a pump | stale after an app stops (RUN-03) |
| `ExportedApp.s_consoleHost`, `s_mainContext`, `s_logger`, signal registrations | `ExportedApp.cs:28-34` | process (one app host per exported process) | correct for exports; never touched in the editor |
| `OffscreenRenderer.s_scenes` | `OffscreenRenderer.cs:20` | process scratch array, main thread | works; belongs to the caller (RUN-06) |
| `QtApp._driverSeconds`, `_prevPumpEnd`, counters | `QtApp.cs:264-278` | process (inside the singleton) | `_driverSeconds` is not the whole truth (RUN-02) |
| `PerfStats.Empty`, `WindowPlacement.DefaultBounds`, `BoundsSaver.Delay` | | immutable | fine |

## 3 Findings

### RUN-01 Node messages: one slot per phase, not one slot per node
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt/NodeErrors.cs:36-97`, `src/VL.Qt/Renderer.cs:158-159`, `202-219`, `196-198`
- **Finding:** `NodeErrors` keeps one shown text and one `_shownPhase`. `Report` and `Warn` replace whatever is shown. `Clear(phase)` removes the message only if it came from that phase, and then nothing is shown, even if another phase still has a problem. Concrete cases in `Renderer`:
  - At creation `ReportSamples` warns (device lowered MSAA), then `ReportIcon` warns (icon not loadable). Only the icon warning is visible; the samples warning is gone for good, because `ReportSamples` runs again only when the pin changes.
  - The icon warning is shown, `AfterEvents` throws once and replaces it, and the next frame `Clear(AfterEvents)` removes the message. The icon is still the default icon, and the node shows nothing.
  - `error = _errors.Current ?? qtNativeError` (`Renderer.cs:198`) puts a warning text on the `Error` output and hides a QtNative error behind it.

  23 node classes create a `NodeErrors`, so the same loss can happen wherever a node uses more than one phase.
- **Proposal:** `NodeErrors` keeps a small list of `(string Phase, MessageSeverity Severity, string Text)`, at most one entry per phase. `Report`/`Warn` set the entry of their phase. `Clear(phase)` removes only that entry. The node shows one persistent message that joins all entries (errors first, one line each), rebuilt only when the list changes. `Current` becomes `CurrentError` (errors only), and the `Error` pin reads that. The "log once per distinct text" memory moves into the entry.
- **Decided:** DECISIONS 'No `Error` output pins' → the `Error` pin of the Renderer goes, so `CurrentError` feeds no pin: the message list is shown at the node (warning or error, message as tooltip) and logged; P22 and TARGET 3.2 → RUN-01, NODE-05 and NODW-04 are one mechanism for messages at a node, one issue; A1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → `NodeErrors` is the one store, one entry per phase, and `NodeMessage` is the handle a message source holds on it; `ResourceNodeReport` and the shader area use it
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/NodeErrors.cs`, `src/VL.Qt/Renderer.cs`, new `src/VL.Qt.Tests/NodeErrorsTests.cs` (needs a `NodeContext`; if that is not constructible in unit tests, extract the list logic into `internal sealed class NodeMessageList` and test that)
- **Depends on:** none
- **Verification:** new unit test: warn A, warn B, clear A → B shown; report C, clear C → A and B back. Help patch `HowTo Set the Window Icon` (or whichever covers WindowSettings) with a missing icon plus Multisample X8 on a device without X8.

### RUN-02 One owner for the animation driver time
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/QtApp.cs:386-392`, `src/VL.Qt/OffscreenRenderer.cs:277-285`, `src/VL.Qt.Stride/QtTextureRenderer.cs:124-131`, `src/VL.Qt.Native/offscreen.cpp:645`, `src/VL.Qt.Native/vlqt.cpp:135`, `1168-1182`
- **Finding:** `PumpFrame` advances the driver only when the new time is greater than `QtApp._driverSeconds`. `RenderBatch` advances the native driver itself (`offscreen.cpp:645`) to a time chosen by `QtTextureRenderer.DriverSeconds()`, but `_driverSeconds` never learns about it. The native `setTime` accepts any value. With app B (QtTexture, its clock ahead) and app A (Renderer) in one process, B's batch sets the native driver to `t_B`. A's pump then compares its time only with the managed value of the last pump and can set the native driver to a time below `t_B`, so Qt animations jump back by up to a frame. In a single app the two times coincide, so the tests do not see it. The rule "the driver never runs backwards" lives in three places: `QtPump._offset`, the `PumpFrame` guard and `QtTextureRenderer.DriverSeconds`.
- **Proposal:** `QtApp` owns the driver: `internal double AdvanceDriver(double seconds)` applies the max rule, calls `vlqt_driver_advance` only when the time grows, and returns the time in effect. `PumpFrame` calls it. `OffscreenRenderer.RenderBatch` takes the time already resolved by `QtApp.AdvanceDriver`, and `QtTextureRenderer.DriverSeconds` is deleted. Native side (for the native review): `renderBatch` stops calling `vlqt_driver_advance`, or `Driver::setTime` ignores times below the current one, so the guarantee holds in one place.
- **Decided:** A5 and CAPI-03 → `vlqt_driver_advance` is `vlqt_app_advance_animations` (glossary 'animation clock'); the exports that take scenes are `vlqt_scene_*`; B6 of [DECISION-ROUND.md](../DECISION-ROUND.md) → native side: `Driver::setTime` ignores a time below the current one; `vlqt_offscreen_render_batch` keeps its signature
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/QtApp.cs`, `src/VL.Qt/OffscreenRenderer.cs`, `src/VL.Qt.Stride/QtTextureRenderer.cs`, optionally `src/VL.Qt.Native/offscreen.cpp` and `vlqt.h`/`vlqt.cpp` (signature of `vlqt_offscreen_render_batch`), `src/VL.Qt/Native.cs`, SmokeTest callers of `OffscreenRenderBatch`
- **Depends on:** none
- **Verification:** new `QtPumpTests` case with the fake app: offscreen advance to t+0.05, then a pump at t+0.03 → driver stays at t+0.05. SmokeTest `PumpTest` and `MultiRendererTest`.

### RUN-03 QtLog: attach and detach the logger factory with the app host
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/QtLog.cs:18-19`, `41-63`, `134`; `src/VL.Qt/QtApp.cs:302-307`; `src/VL.Qt/QtApp.cs:116-123`
- **Finding:** The class comment says that messages "between an app restart's teardown and the next renderer are kept and flushed". Nothing in the library ever sets the target back to null: `SetLogger(null)` is called only by `CreateForTest` and the unit tests. The target is set only when the first pump of an app is created (`QtPump.ForApp` → `QtApp.Get(factory)`), plus `LoadFontNode` and `QtTextureRenderer`. Effects:
  - After F5/F8, every Qt message until the next pump goes to the old app host's factory. If vvvv disposes that factory, `LoggerFactory.CreateLogger` throws `ObjectDisposedException` for every new category, `OnLog` swallows it, and the message is lost. Not verified here: whether vvvv disposes `AppHost.LoggerFactory` on app stop. Check with a log message right after F8 and a breakpoint in `OnLog`'s catch.
  - With two apps, the app that most recently created a pump receives every Qt message, including the warnings of the other app's windows. It keeps receiving them after it stops while the other app runs on.
  - `QtApp.Logger` is a second name for `QtLog.Logger`.
- **Proposal:** `QtLog.Attach(AppHost appHost)` returns an `IDisposable` and registers it with the app host's disposables (`appHost.Services` or `OnExit`). QtLog keeps the attached factories in a list. The target is the newest live one. On detach it falls back to the previous one, or to the pending queue when none is left, which makes the comment true. Call it from `Initialization.Configure` (runs per app host, before any node), not from `QtApp.Get`. `QtApp.Get` loses its parameter, and `QtApp.Logger` is deleted (callers use `QtLog.Logger`).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/QtLog.cs`, `src/VL.Qt/QtApp.cs`, `src/VL.Qt/Initialization.cs`, `src/VL.Qt/Images/ResourceClient.cs`, `src/VL.Qt/Nodes/LoadFontNode.cs`, `src/VL.Qt.Stride/QtTextureRenderer.cs`, `src/VL.Qt.Tests/QtLogTests.cs`, SmokeTest callers of `QtApp.CreateForTest`
- **Depends on:** none (see question Q2)
- **Verification:** `QtLogTests`: attach A, attach B, detach B → A receives; detach A → queued, attach C → flushed. Manual: F8 then F5 in vvvv, Qt warnings of the restarted patch appear in the Log window.

### RUN-04 No allocation per frame in Renderer.Update
- **Kind:** performance
- **Severity:** should
- **Breaking:** no (Perf values may refresh at 4 Hz instead of every frame, see proposal)
- **Where:** `src/VL.Qt/Renderer.cs:144`, `194`; `src/VL.Qt/WindowRenderer.cs:114-133`, `177-186`; `src/VL.Qt/PerfStats.cs:138-155`; `src/VL.Qt/WindowSettings.cs:44-49`, `56-68`
- **Finding:**
  - `Renderer.Update` always reads `_view.Perf`, whether or not the pin is connected. `vlqt_window_get_perf` computes rolling averages over the last ~1 s (perf.cpp, `out->fps = 1000.0 / out->frame_ms`), so the bytes differ after nearly every pump, `_perfChanged` is true, and `PerfStats.From(p, s, h)` allocates two records: `From(p)` and the `with` clone. That is about 2 × 200 B per frame per window while the window renders.
  - `WindowSettings.Resolve` runs every frame and calls `IconFullPath` → `Path.GetFullPath` when an icon is set. That is a `GetFullPathNameW` call and usually a new string each frame, for a value that cannot change, because `WindowSettings` is immutable.
- **Proposal:**
  - Build `PerfStats` in one constructor call, without `with`.
  - Rebuild it only every `kPercentileIntervalMs` (250 ms), the same cadence as the percentiles, so the record changes at most four times per second.
  - Resolve the icon path once, in the `WindowSettings` constructor. Store `IconPath` (`string?`, full path or null) next to the given `Icon`.
- **Decided:** C17 → the Perf output keeps updating every frame, so the 250 ms rebuild and the 4 Hz cadence are not taken; this finding only removes the per-frame allocation (record structs, one constructor call, no `with`) and resolves the icon path once; C1 → `WindowSettings` keeps only `Icon` and `Bounds` (on create), so `Resolve` no longer merges Title, Borderless and Fullscreen
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/WindowRenderer.cs`, `src/VL.Qt/PerfStats.cs`, `src/VL.Qt/WindowSettings.cs`, `src/VL.Qt/Nodes/WindowSettingsNode.cs`, `src/VL.Qt.Tests/WindowSettingsTests.cs`, `src/VL.Qt.Tests/PerfStatsTests.cs`
- **Depends on:** RUN-10 if PerfStats is reshaped at the same time
- **Verification:** `src/VL.Qt.Tests/Allocations.cs`-style test for `WindowRenderer.Perf` and `WindowSettings.Resolve` (0 bytes over 100 calls); help patch `HowTo Draw a Rectangle` with the Perf pin.

### RUN-05 Cut QtApp.cs into one type per file and pull the diagnostics out of PumpFrame
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/QtApp.cs:14-46` (`IQtFrameParticipant`), `49-70` (`IQtPumpTarget`), `94-238` (`QtPump`), `243-248` (`HostFrameHistory`), `256-591` (`QtApp`); inside `QtApp`: `366-469` (host frame, slow-frame warning, stats), `476-590`
- **Finding:** A reader looking for "how a frame runs" opens `QtApp.cs` and finds five types. `PumpFrame` is 120 lines, and two thirds of them are measurement: GC counters, host-frame fields, the slow-frame warning with 17 template arguments, and interval stats. The four steps that matter (apply, advance, process events, poll) are hard to see. `HostFrameHistory` is a bag of public mutable fields that `QtApp` writes on behalf of `QtPump`.
- **Proposal:**
  - `IQtFrameParticipant.cs`: the interface, unchanged. Its default members stay, because test fakes implement subsets.
  - `IQtPumpTarget.cs`: stays. It is the seam for `QtPumpTests.FakeApp`.
  - `QtPump.cs`: `QtPump`. It owns a `HostFrameMeter` (one per app, replacing `HostFrameHistory`).
  - `HostFrameMeter.cs` (`internal sealed class`): `Begin()` reads the GC and alloc counters and the start timestamp. `End(long vvvvStart, long prevPumpEnd, long end)` returns the `Native.vlqt_host_frame`. It holds what `HostFrameHistory` held.
  - `PumpDiagnostics.cs` (`internal sealed class`, owned by `QtApp`): the cumulative counters and `[vlqt.stats]`, the slow-frame warning (`WarnIfSlow(...)` with `SumWaitMs`, `ExceedsBudget`, `FormatPhases`), and the rate-limited `LogParticipantError`.
  - `QtApp.cs`: `QtApp`. `PumpFrame` reads as the steps of section 2, about 30 lines.
- **Decided:** A5 and glossary 1.6 → `HostFrameMeter` is `HostPumpMeter` (`HostPumpMeter.cs`) and `Native.vlqt_host_frame` is `vlqt_host_pump`; P14 → K3 (one type per file) is done by this issue in wave 4
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/QtApp.cs`, new `IQtFrameParticipant.cs`, `IQtPumpTarget.cs`, `QtPump.cs`, `HostFrameMeter.cs`, `PumpDiagnostics.cs`; `src/VL.Qt.Tests/PerfStatsTests.cs` (calls `QtApp.FormatPhases`), `src/VL.Qt.Tests/QtPumpTests.cs`, SmokeTest `PumpTest.cs`, `HandlerTest.Latency.cs`
- **Depends on:** RUN-02 (`AdvanceDriver`) if done together
- **Verification:** `QtPumpTests`, `PerfStatsTests`, SmokeTest `--all` (PumpTest, Latency); the F5 overlay still shows the vvvv/wait/apply segments.

### RUN-06 Name the four scene types for what they are; give the shared core the handle
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: the C# type `VL.Qt.Renderer` becomes `VL.Qt.RendererNode` (node name and category unchanged; C# users of the library notice). Internal types are not visible to users.
- **Where:** `src/VL.Qt/Renderer.cs:23-24`, `src/VL.Qt/WindowRenderer.cs:15`, `47-68`, `274-284`, `src/VL.Qt/OffscreenRenderer.cs:17-47`, `303-313`, `src/VL.Qt/SceneCore.cs:16`, `47-56`, `253-267`
- **Finding:** The four types have distinct, sound responsibilities, but the names hide them:
  - `Renderer` is the vvvv node. AGENTS.md asks for `…Node`.
  - `WindowRenderer` renders nothing itself. It is the managed side of one native window scene: window state, title, mode, perf, geometry events.
  - `OffscreenRenderer` is the same thing for an offscreen scene. It also translates vvvv notifications into Qt input, a second responsibility of about 140 lines (`OffscreenRenderer.cs:129-270`) with its own state (`_mouseButtons`, `_keysDown`, `_highSurrogate`, which uses `' '` as "none").
  - `SceneCore` is the shared part, but the handle is set through a property "once after creation" (`SceneCore.cs:47-56`). Both wrappers repeat the same `_disposed || _handle == IntPtr.Zero` guard in every method, and the same `Dispose` (`Close()` + `vlqt_window_destroy`).
  - `OffscreenRenderer.RenderBatch` copies handles into a static array (`OffscreenRenderer.cs:20`, `277-285`), although its only caller (`QtTextureRenderer`) already keeps a batch array.
  - QtNative calls the same thing `vlqt::Scene` (architecture.md, "one per window or offscreen scene").
- **Proposal:**
  - `Renderer` → `RendererNode` in `src/VL.Qt/Nodes/RendererNode.cs`, with `[ProcessNode(Name = "Renderer")]` unchanged.
  - `SceneCore` → `Scene` (managed side of one `vlqt::Scene`). The constructor takes the created handle (`Scene(SceneRegistry registry, IntPtr handle)`). `Dispose()` does clear, apply and `vlqt_window_destroy`. `IsOpen` replaces the guards.
  - `WindowRenderer` → `WindowScene`: it creates the window, then `new Scene(registry, window)`, and keeps only the window-specific members.
  - `OffscreenRenderer` → `OffscreenScene`, same pattern. Its input translation moves into `OffscreenInput` (`internal sealed class OffscreenInput`, owns the input state, `Notify(INotification)` calls `OffscreenScene.Send*`). `'\0'` becomes the "no pending high surrogate" value.
  - `RenderBatch(IntPtr context, ReadOnlySpan<IntPtr> scenes, double driverSeconds)` takes handles. The caller keeps its own `IntPtr[]`, and `s_scenes` goes.
  - Note for the reconciler area: `SceneRegistry` (element occurrences per app) would then sit next to `Scene` (one native scene). Review 05 should say whether it becomes `OccurrenceRegistry` or similar.
- **Decided:** O2 → `Scene`, `WindowScene`, `OffscreenScene` as proposed, and the constructor takes a `SceneHandle` (what the C handle `vlqt_scene` points to, CAPI-03), not a raw `IntPtr`; REC-06 and O2 → `SceneRegistry` is `OccurrenceRegistry`; A5 → `vlqt_window_destroy` and the other shared functions are `vlqt_scene_*`; S1 → `RendererNode` is in namespace `VL.Qt.Nodes` (node classes), `Scene/` stays `VL.Qt`
- **Effort:** M, mechanical: partly (renames yes, handle-in-constructor no)
- **Touches:** `src/VL.Qt/Renderer.cs` → `Nodes/RendererNode.cs`, `WindowRenderer.cs` → `WindowScene.cs`, `OffscreenRenderer.cs` → `OffscreenScene.cs` + `OffscreenInput.cs`, `SceneCore.cs` → `Scene.cs`, `src/VL.Qt/Reconciler/Reconciler.cs` (doc refs), `src/VL.Qt.Stride/SharedTextureScene.cs`, `QtTextureRenderer.cs`, unit tests `SceneCoreClearTests.cs`, `SceneCoreSpaceTests.cs`, `ResyncTests.cs`, `ReconcilerSpaceTests.cs`, `PerfStatsTests.cs`, `OffscreenRendererTests.cs`, `InputMappingTests.cs`; SmokeTest `BorderlessTest`, `FullscreenTest`, `MultiRendererTest`, `Program`, `PumpTest`, `RendererTest`, `ResyncTest`, `SpaceTest`, `QtTextureTest*`; docs mentioning the names
- **Depends on:** RUN-02 (RenderBatch signature)
- **Verification:** `dotnet test src/VL.Qt.Tests`, SmokeTest `--all`, `tools/vvvvc.ps1` on every help patch with a Renderer (node identity in `.vl` files: check that the patches still bind after the C# type rename; if `.vl` stores the C# type name, update the help patches in the same change).

### RUN-07 Save Bounds marks the document changed on open when WindowSettings sets Bounds
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Renderer.cs:81-85`, `154`, `161-162`; `src/VL.Qt/WindowPlacement.cs:49-63`
- **Finding:** The `BoundsSaver` is created in the constructor with the Renderer's own `Bounds` pin. The window is created in the first `Update` with `WindowSettings.ResolveBounds(window, _bounds)`, so when `WindowSettings.Bounds` is set it opens elsewhere. The first geometry report differs from `_saved`, and 0.5 s later `SetPinValue("Bounds", …)` writes the settings value into the Renderer pin. Every patch that sets Bounds through WindowSettings is marked changed right after opening, without the user moving the window.
- **Proposal:** Create the `BoundsSaver` together with the window, in the first `Update`, initialised with the resolved create bounds.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Renderer.cs`
- **Depends on:** none
- **Verification:** Help patch that uses WindowSettings with Bounds: open it in vvvv, wait 1 s, the document is not dirty. A unit test is not possible without `NodeContext`; `WindowPlacementTests` covers `BoundsSaver` itself.

### RUN-08 Exported-app lifetime in one place
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Renderer.cs:72-80`, `89-104`, `339-343`; `src/VL.Qt/ExportedApp.cs:40-93`
- **Finding:**
  - The knowledge about vvvv's export hosts is split. `Renderer` holds the WinForms host detection by type name (`IsWindowsFormsHost`, used by `ExportedApp`) and the `KeepAlive` decision, with a 6-line comment about vvvv IL. `ExportedApp` holds the console signals and `Exit`, which sets the private `AppHostBase.refCount` by reflection and falls back to `Environment.Exit(0)`.
  - `ExportedApp.Attach` is called only from `Renderer`. A console export that uses only QtTexture or FileWriter gets no SIGTERM handling and no Linux `_exit`, the second being the protection against the WSL arm64 driver hang (the comment at `ExportedApp.cs:20-21`).
- **Proposal:**
  - `ExportedApp` becomes the one place for vvvv host knowledge: `IsWindowsFormsHost(AppHost)` moves there, and so does `IDisposable? HoldWhileWindowOpen(AppHost)`, which returns the KeepAlive in the WinForms export and null elsewhere. `Renderer` calls these two functions and keeps no host comment.
  - `Attach` is called from `Initialization.Configure` when `appHost.IsExported`, so every exported app gets it, Renderer or not.
  - The reflection stays behind `ReleaseKeepAlive` (see Q1).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/ExportedApp.cs`, `src/VL.Qt/Renderer.cs`, `src/VL.Qt/Initialization.cs`
- **Depends on:** none
- **Verification:** Linux console export (docs/linux.md) of a patch with a FileWriter and no Renderer: SIGTERM ends with exit code 0; Windows WinForms export: X ends the app.

### RUN-09 Pin help and docs that state something the code does not do
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Renderer.cs:51`, `53`, `339-343`; `docs/decisions/timing.md` ("Global pump on `IFrameClock.GetFrameFinished()`", "One global pump on the `QtApp` provider"); `docs/architecture.md` ("per vvvv frame" list, "Multiple windows")
- **Finding:**
  - `Bound To Document` says "False: X only closes the window, the app keeps running". In the WinForms export, X releases this Renderer's `KeepAlive` for both values (`Renderer.cs:339-341`), so the app ends when nothing else holds it.
  - The `Bounds` pin help carries the hotswap history ("A node added live or restored by Undo/Redo gets no pin value…"). That is a design note, not help.
  - timing.md describes one global pump. The code has one pump per vvvv app, and several apps pump in one frame, each rendering only its windows, with a monotonic driver.
  - The architecture list misses the apply step and the resource queues, and names `vlqt_app_process_events` instead of `vlqt_app_process_events_for`.
- **Proposal:**
  - `Bound To Document` false: "X hides the window. The editor keeps the app running; an exported app ends when nothing else keeps it alive (KeepAppAlive)."
  - `Bounds`: keep the first three sentences. The hotswap note goes to `docs/topics/` (or the WindowPlacement code comment, shortened).
  - timing.md gets a section "Several vvvv apps" (pump per app host, driver offset and resync, windows per pump).
  - architecture.md gets the frame order of section 2 of this report.
- **Decided:** G5 → `docs/decisions/timing.md` is `docs/design/timing.md`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Renderer.cs`, `docs/decisions/timing.md`, `docs/architecture.md`
- **Depends on:** RUN-08 (wording follows the final behaviour)
- **Verification:** read-through; `HowTo` help patch of the Renderer shows the new pin help.

### RUN-10 PerfStats: one shape instead of positional + init-only + four Split helpers
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: `Split [PerfStats]` and the four `Split…` operations change pins; patches that read percentiles, counters, host or GC values are rewired.
- **Where:** `src/VL.Qt/PerfStats.cs:12-18`, `30-31`, `78`, `90-136`
- **Finding:**
  - The record has 11 positional values with no `<param>` docs, so the `Split` pins have no help, and about 30 init-only properties added later "additively".
  - Because VL's `Split` shows only positional values, four hand-written `Split…` methods exist (`#270` comment).
  - A reader cannot tell which values are the main ones, and every new value needs a new `Split…` method or a place in one.
- **Proposal:** `PerfStats` stays a `public sealed record` with the main values as positional parameters, each with `<param>`, plus four value groups as positional parameters of their own type:
  - `PerfPercentiles(BudgetMs, FrameMsP50…, PumpMsP50…)`
  - `PerfCounters(Pumps…, RecentMultiSwaps)`
  - `HostTimes(VvvvMs…, Ops, VsyncMs)`
  - `GcActivity(Gen0PerSecond…, AllocatedBytesPerFrame)`

  Each group is a `public readonly record struct` with documented parameters, so a nested `Split` works in VL without an extra allocation. The four `Split…` methods go. Alternative: keep the flat record and only add `<param>` docs (no break, but the shape stays). Recommendation: the grouped shape, together with RUN-04.
- **Decided:** C17 → (a), grouped record structs with nested Split; the output updates every frame (RUN-04 only removes the allocation); P22 → RUN-10 = NDIAG-11, one issue; TARGET 2.2 and 2.7 → one file per record in `Window/` (`PerfStats.cs`, `PerfPercentiles.cs`, `PerfCounters.cs`, `HostPerfStats.cs`, `GcPerfStats.cs`), so `HostTimes` and `GcActivity` are `HostPerfStats` and `GcPerfStats`; NDIAG-10 → `PerfSample.frame` is `frameInterval`; E13 → `OverBudget` counts only Qt work (events, sync, render), not the vsync wait
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/PerfStats.cs`, `src/VL.Qt/WindowRenderer.cs`, `src/VL.Qt.Stride` (if QtTexture outputs PerfStats; check), `src/VL.Qt.Tests/PerfStatsTests.cs`, help patches using `PerfStats` (search `help/` for `Split Percentiles`, `SplitHost`), `help/Help.xml` if a topic changes
- **Depends on:** RUN-04; question Q3
- **Verification:** `PerfStatsTests`; `tools/vl-patch-check.py` and `tools/vvvvc.ps1` on the help patches that show Perf.

### RUN-11 Thread affinity and a failed init are silent in Release
- **Kind:** threading
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Renderer.cs:31`, `70-71`, `141`; `src/VL.Qt/QtApp.cs:283`, `292-293`, `306`, `355-364`
- **Finding:**
  - `QtApp` binds to the thread of the first `Get`. `Renderer` checks the thread only with `Debug.Assert`, a no-op in the shipped Release build. `PumpFrame` logs a wrong thread once and then never pumps.
  - A Renderer used from another thread (for example inside an async region) calls QtNative from that thread in Release, which is undefined behaviour in Qt.
  - `_app` in `Renderer` exists only for the asserts.
  - If `vlqt_app_init` fails, the constructor throws and `s_instance` stays null, so every later `Get` calls `vlqt_app_init` again. Whether a second call after a half-done first one is safe was not checked: read `vlqt_app_init` in vlqt.cpp for a `QGuiApplication` created before the failing step.
- **Proposal:** `QtApp.Get` checks the thread when the instance exists and throws `InvalidOperationException("VL.Qt must be used on the thread that created Qt (thread {id})")`. That is a programming error per AGENTS.md, and vvvv shows it at the node. Remove `_app` and the asserts from `Renderer`. Remember an init failure (`s_initError`) and throw it again instead of retrying, unless the native check shows a retry is safe and useful.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/QtApp.cs`, `src/VL.Qt/Renderer.cs`
- **Depends on:** none
- **Verification:** `QtPumpTests` unaffected; SmokeTest `PumpTest`.

### RUN-12 Renderer: one guarded call per pump phase
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Renderer.cs:223-291`
- **Finding:** Four explicit interface members repeat the same block: if disposed return, try, call `_view`, `Clear(phase)`, catch, `Report(e, phase)`. `QtApp` wraps every call again. A fifth phase would be copied a fifth time.
- **Proposal:** `NodeErrors.Run<TState>(string phase, TState state, Action<TState> action)`: calls the action, clears the phase on success and reports on failure; with static lambdas there is no allocation. Each participant member becomes one line, e.g. `_errors.Run(nameof(ApplyCommands), _view, static v => v?.ApplyCommands())`.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/NodeErrors.cs`, `src/VL.Qt/Renderer.cs`
- **Depends on:** RUN-01
- **Verification:** SmokeTest `PumpTest` (Throwing participant).

### RUN-13 Hide `Geometry` from the node browser
- **Kind:** api
- **Severity:** could
- **Breaking:** yes: the nodes `ToQtAlignment` and `AnchorOffset` disappear from the node browser (no help patch uses them; checked with a search in `*.vl`).
- **Where:** `src/VL.Qt/Geometry.cs:11`, `src/VL.Qt/Renderer.cs:14`
- **Finding:** `Geometry` is a `public static class`, and the assembly is imported with `ImportAsIs`, so its two methods show up as nodes. They are protocol helpers (Qt alignment bits for the `ANCHOR` command). The tests reach internals through `InternalsVisibleTo`.
- **Proposal:** Make `Geometry` `internal`. Move `[assembly: ImportAsIs]` from `Renderer.cs` into `Initialization.cs`, next to `[assembly: AssemblyInitializer]`, where a reader looks for assembly attributes.
- **Decided:** catalog → `Geometry` is internal (row 'ElementSink, SinkState, SinkSlot, ElementType, Geometry'); TARGET 2.2 → the assembly attributes live in `Properties/AssemblyInfo.cs`, not in `Initialization.cs`, and `Geometry.cs` is in `Scene/`
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Geometry.cs`, `src/VL.Qt/Renderer.cs`, `src/VL.Qt/Initialization.cs`
- **Depends on:** none
- **Verification:** `dotnet test src/VL.Qt.Tests` (GeometryTests); node browser shows no `ToQtAlignment`.

### RUN-14 Dead weight and test hooks of the area (K6)
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/WindowPlacement.cs:126-130`; `src/VL.Qt/SpaceMapping.cs:77-92`; `src/VL.Qt/QtApp.cs:137`, `309-314`, `325-327`, `516-517`; `src/VL.Qt/WindowRenderer.cs:105-111`; `src/VL.Qt/SceneCore.cs:60-64`
- **Finding:**
  - `TryReadGeometry` accepts records shorter than 28 bytes, for a QtNative "older than #465". QtNative and the C# library always ship together, so the branch is dead.
  - K6 occurrences in the area:
    - `SpaceMapping.ToQtTransform`: a reference formula used only by `GeometryTests` and `SpaceMappingTests`.
    - `QtPump.DeliverInputAtFrameStart`: lets the latency test switch back to the old behaviour.
    - `QtApp.CreateForTest`, `QtApp.LastDeliveredInput`, and the counters `Pumps`/`Advances`/`ProcessEventsCount`: SmokeTest only.
    - `WindowRenderer.Reconciler`/`Commands`/`Resyncs` and `SceneCore.Commands`: SmokeTest seams.
- **Proposal:**
  - Delete the < 28 branch and require 28 bytes.
  - Move `ToQtTransform` into the test project as a helper.
  - Drop `DeliverInputAtFrameStart`: the latency test then asserts the current latency only, and the old "one frame later" number goes into its comment.
  - Keep `CreateForTest` and the counters, because PumpTest needs a real `QtApp` on a simulated clock and they are cheap, but group them under a `// Diagnostics read by the SmokeTest` comment.
  - The `Commands` seams stay until review 05 decides how ResyncTest injects a bad command.
- **Decided:** P22 → RUN-14 is one of the K6 findings worked under TEST-07 (one issue), not an issue of its own
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/WindowPlacement.cs`, `src/VL.Qt/SpaceMapping.cs`, `src/VL.Qt/QtApp.cs`, `src/VL.Qt.Tests/GeometryTests.cs`, `SpaceMappingTests.cs`, `WindowPlacementTests.cs`, SmokeTest `HandlerTest.Latency.cs`
- **Depends on:** RUN-05
- **Verification:** unit tests, SmokeTest `--all`.

## 4 Target structure

Folders are for finding things. The namespace stays `VL.Qt` for every type here, because ImportAsIs derives the vvvv category of the public types (`Space`, `MultisampleCount`, `PerfStats`, `WindowSettings`) from the namespace, and splitting the internal ones into other namespaces would gain nothing.

```
src/VL.Qt/
  Initialization.cs          AssemblyInitializer; [assembly: ImportAsIs] moves here (RUN-13); attaches QtLog and ExportedApp per app host (RUN-03, RUN-08)
  Categories.cs              unchanged
  Runtime/
    QtApp.cs                 the process-wide Qt application: init, PumpFrame, DeliverInput, AdvanceDriver (RUN-02), Resources   (split from QtApp.cs)
    QtPump.cs                pump of one vvvv app: clock subscriptions, participants, driver offset                              (split)
    IQtFrameParticipant.cs   what a window does in a pump                                                                        (split)
    IQtPumpTarget.cs         seam for QtPumpTests                                                                                (split)
    HostFrameMeter.cs        host-frame values per app (replaces HostFrameHistory)                                               (new)
    PumpDiagnostics.cs       counters, [vlqt.stats], slow-frame warning, participant error throttle                              (new, out of QtApp)
    QtLog.cs                 Qt and VL.Qt log target, attach/detach per app host                                                 (moved, RUN-03)
    ExportedApp.cs           everything about vvvv export hosts: WinForms detection, keep-alive, exit, signals                  (moved, RUN-08)
    NodeErrors.cs            messages at a node, one entry per phase                                                             (moved, RUN-01)
  Scene/
    Scene.cs                 managed side of one vlqt::Scene: reconciler, commands, resync, apply errors, poll                   (SceneCore → Scene)
    WindowScene.cs           a native window scene: create/show, title, mode, icon, samples, debug, perf, geometry, close         (WindowRenderer → WindowScene)
    OffscreenScene.cs        an offscreen scene: bind/unbind, Send*, RenderBatch(handles)                                        (OffscreenRenderer → OffscreenScene)
    OffscreenInput.cs        vvvv notifications → OffscreenScene.Send*                                                           (new, out of OffscreenRenderer)
    InputMapping.cs          vvvv → Qt key/button/modifier values                                                                (moved)
    Space.cs                 public enum                                                                                          (moved)
    SpaceMapping.cs          units ↔ DIP                                                                                          (moved, ToQtTransform to tests)
    Geometry.cs              internal: anchor → Qt alignment/offset                                                               (moved, public → internal)
    MultisampleCount.cs      public enum                                                                                          (moved)
    Multisample.cs           clamp warning                                                                                        (moved)
  Window/
    WindowPlacement.cs       create bounds, FromBounds, TryReadGeometry                                                           (split)
    WindowGeometry.cs        WindowGeometry record struct + WindowStates flags                                                    (split)
    BoundsSaver.cs           Save Bounds debounce                                                                                 (split)
    WindowSettings.cs        public immutable settings, icon path resolved once                                                   (RUN-04)
    PerfStats.cs             public record + four groups                                                                          (moved, RUN-10)
  Nodes/
    RendererNode.cs          vvvv node "Renderer" (Renderer → RendererNode)                                                       (RUN-06)
    WindowSettingsNode.cs    unchanged
```

Public after the refactoring: `RendererNode`, `WindowSettingsNode`, `WindowSettings`, `PerfStats` and its groups, `Space`, `MultisampleCount`, `Initialization`. Everything else is internal.

## 5 Questions for Sebastian

**Q1 Ending an exported app.** vvvv has no API to end an exported app, so `ExportedApp` sets the private field `AppHostBase.refCount` to 0 by reflection, with `Environment.Exit(0)` as the fallback (patch `Dispose` is skipped then). The WinForms host gets `Application.Exit` by reflection.
- (a) Keep it, contained in `ExportedApp` (RUN-08), and test it at every vvvv update.
- (b) Ask the vvvv team (devvvv forum or VL.StandardLibs issue) for `AppHost.RequestExit()`, and keep (a) until it exists.
- Recommendation: (b), with (a) in the meantime.

**Q2 Which app receives Qt's log messages when several vvvv apps run?** Qt's message handler does not know the window. Options:
- (a) The newest live app, with fallback to the previous one when it stops (RUN-03, recommended, small).
- (b) Every live app gets every message: duplicates in the Log window.
- (c) QtNative attributes messages to a window where it can (QML warnings carry the engine, so the window is known) and sends the window id with the callback, and C# routes them to that app's logger: exact, but L and across layers.
- Recommendation: (a) now, (c) as a 2.0 issue if users run several apps.

**Q3 PerfStats shape** (RUN-10, breaking for patches that read perf values):
- (a) Grouped record structs with nested Split.
- (b) Flat record with documented parameters only.

Also: may the Perf output refresh at 4 Hz instead of every frame (RUN-04)? The F5 overlay is unaffected; only the pin values update less often. Recommendation: (a) and yes.

## 6 For other areas

- Native (QtNative): `Driver::setTime` (`vlqt.cpp:135`) accepts a time below the current one, and `renderBatch` advances the driver itself (`offscreen.cpp:645`); see RUN-02.
- Native/protocol: `vlqt_window_*` functions take offscreen scene handles too (`vlqt_window_destroy` frees an offscreen scene, `OffscreenRenderer.cs:310`); a `vlqt_scene_*` prefix would say what they accept.
- Reconciler (05): `SceneRegistry` (element occurrences per app) next to the proposed `Scene` (one native scene) needs a clearer name; `Reconciler.Window` is set through `SceneCore.Handle`.
- VL.Qt.Stride: `QtTextureRenderer.DriverSeconds` duplicates the driver rule (`QtTextureRenderer.cs:124-131`) and goes with RUN-02; it calls `QtApp.Get(appHost.LoggerFactory)` and `QtPump.ForApp` separately (`QtTextureRenderer.cs:43-46`), like `ResourceClient.ForApp`, which RUN-03 makes unnecessary.
- Images: `ResourceClient.SetPumping(false)` shuts the resource worker down whenever the last pump goes, which is every F5/F8 (`ResourceClient.cs:513-520`); check that a restart with images in use does not reload every image (likely intended, not verified).
- AGENTS.md: "Never call `GC.*`" is broader than meant: `QtApp.PumpFrame` reads `GC.CollectionCount`/`GetTotalAllocatedBytes`/`GetTotalPauseDuration` for the host frame. Say "never trigger or tune a collection".
