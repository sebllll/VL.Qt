# Review 16 – QtNative: vlqt.cpp, the application and window layer

Reviewer: Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/vlqt.cpp` (2452 lines, not 2299), `src/VL.Qt.Native/vlqt.h` (620 lines), the QtNative parts of `docs/architecture.md` (lines 1–68). Looked up with targeted excerpts: `offscreen.cpp` (error helpers, `renderThreaded` 579–639, touch input 889–905), `CMakeLists.txt` (Qt modules), `QtApp.cs` 370–400, `ExportedApp.cs` 100–120, the callers of `vlqt_last_error`, `vlqt_image_write`, `vlqt_shader_reflect`, `vlqt_res_read_audio` and the test exports in `src/`. Not read: the other QtNative files (they belong to other reviews), Qt sources.

## 1 Summary

`vlqt.cpp` is the whole C ABI of QtNative plus everything that did not get a file of its own: logging, last error, the application start, the animation clock, the render gate of the pump, the scene change counter, five event filters, the window handle and its lifetime, window placement, borderless and fullscreen, the MSAA window swap, app fonts, the WSL GPU probe, synthetic test input, the thin `vlqt_res_*` wrappers and the DLL unload hook. That is about fifteen subjects in one file; a reader who comes for "why does Esc release the focus" or "why does my window render only once per frame" has to search 2,450 lines. The code itself is careful and its comments explain the reasons well, but most reasons are tied to issue numbers (K1).

What matters most:

1. **Cut the file into about fifteen files with one subject each** (NAPP-01, section 4). The cuts are clean; almost nothing in the file depends on more than two of its neighbours.
2. **The error contract of the C ABI** (NAPP-02, NAPP-03): `g_lastError` is a plain `std::string` written from any thread that throws; `-1` means "null window", "exception" and "too small buffer" at the same time; ten exports are not guarded; failures of `void` exports are invisible.
3. **Threads are a convention, not a check** (NAPP-04): only the two font exports check that they run on the Qt GUI thread. The render threads of offscreen scenes also run code of this file (`UpdateGate`'s sync hook) and are safe only because the batch serializes them, which nothing states.
4. **Private Qt API is spread over the file** (NAPP-05): ten private or QPA headers, one of them marked "TEST ONLY" although production input uses it. Each use goes into one small file named for it, with a table in `docs/` that a Qt update checks.
5. **Test exports ship in `vlqt.dll` under three naming schemes** (NAPP-09): `vlqt_test_*`, plain names (`vlqt_window_grab`, `vlqt_perf_eval`, `vlqt_perf_host_eval`) and one export missing from the header (`vlqt_test_perf_stats_text`).

## 2 What works and must survive

- **One `QGuiApplication` per process, created on the caller's thread, never deleted** (`vlqt.cpp:1066-1072`). The vvvv main thread is the Qt GUI thread. Deleting the app at unload would race Qt's own statics; keep it.
- **Basic render loop, pumped by the host** (`vlqt.cpp:1047`, `QSG_RENDER_LOOP=basic`, `QT_QPA_UPDATE_IDLE_TIME=0`). Every window renders inside `vlqt_app_process_events_for` and at most once per pump.
- **`UpdateGate` semantics** (`vlqt.cpp:332-498`): update requests outside the pump, after this pump's render, or of a window of another vvvv app are held and delivered once at the end of the next pump; the first Expose and Expose during input delivery are deferred into the pump. Several vvvv apps in one process depend on the per-app blocking (`beginPump(all, active, count)`).
- **Input delivery at the frame start without rendering** (`vlqt_app_deliver_input`, `vlqt.cpp:1142-1166`): flushes the window-system queue and the delayed touch moves, renders nothing. A call from inside the pump does nothing.
- **The animation clock is vvvv's clock** (`VlqtAnimationDriver` + `VlqtKeepAlive`, `vlqt.cpp:124-157`, `1083-1086`): `elapsed()` counts from `start()`, and the keep-alive timer keeps `QUnifiedTimer` from switching to the wall clock. `vlqt_driver_advance` first delivers queued `MetaCall`s so an animation started in this frame's apply advances in this frame (`vlqt.cpp:1179`).
- **Change count** (`vlqt_window_change_count`): counted at sync from the window's dirty item list, ignoring the footprint of snapshot grabs and the perf overlay, so a static scene keeps its count while tooltips grab it. The tooltip previews rely on it.
- **Window swap for a new MSAA count** keeps the handle, the scene, the QML engine, geometry, states, flags, overlay and the change count (`replaceWindow`, `vlqt.cpp:1692-1738`).
- **The window handle serves windows and offscreen scenes alike**: the same `vlqt_window_*` exports work on both, with no-ops or explicit errors for the window-only ones (documented in `vlqt.h:502-510`).
- **Log path**: every Qt and QtNative message goes through one callback, with an atomic pointer cut at unload so messages from Qt's static destructors do not call into a dead CLR (`vlqt.cpp:86-109`, `2438-2452`).
- **Hot paths, allocation-free after warm-up; keep them so:** `vlqt_app_process_events_for` (reused `s_active` / `s_perf`, `vlqt.cpp:1106-1116`), the `beforeSynchronizing` hook of `UpdateGate` with `sceneDirty()` (scratch `s_children`, `vlqt.cpp:339-346`, `432-486`), `vlqt_window_apply`, `vlqt_window_poll`, `vlqt_driver_advance`, `vlqt_app_deliver_input`, `vlqt_window_change_count`. The log path allocates (`msg.toUtf8()`); that is acceptable because logging is not per frame.

## 3 Findings

### NAPP-01 Cut vlqt.cpp into files with one subject each
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/vlqt.cpp:1-2452`, `src/VL.Qt.Native/CMakeLists.txt:20`
- **Finding:** The file holds about fifteen subjects (list in section 4). The anonymous namespace (`vlqt.cpp:84-896`) mixes classes used by every window (`UpdateGate`, `FocusRelease`) with Linux start-up code (`probeGlx`), test filters and the font registry. Functions after line 1013 are `static` at file scope instead of in the anonymous namespace (`applyFrameless` 1548, `applyBorderless` 1594, `screenWithLargestShare` 1604, `applyFullscreen` 1625, `replaceWindow` 1692, `testTouchDevice` 1912, `testMouseDevice` 1954). Part of `namespace vlqt::offscreen` is implemented here (`vlqt.cpp:899-1011`: `createHandle`, `releaseFocus`, `sendTouch`, `cancelTouch`, `stateOf`, `quickWindow`, `setError`) because the handle type and `setError` are private to this file. `FullscreenKeys` calls the exported `vlqt_window_set_mode` / `vlqt_window_get_mode` through the C ABI (`vlqt.cpp:193`) for the same reason. A reader who knows Qt barely cannot tell which part is the contract with C# and which is machinery.
- **Proposal:** Split as in section 4: the exports stay thin (null check, guard, call), the machinery goes into `vlqt::` types in their own files, the handle type gets a header (`handle.h`) so `offscreen.cpp` and the test files use it directly and the `offscreen::createHandle/stateOf/quickWindow/setError` bridge disappears. `FullscreenKeys` calls `vlqt::window::setMode(Handle&, mask, value)`. Each new file starts with a three-line comment that says its subject and its thread. While moving, replace the issue references (K1) by the reason they stand for; the ones of this file: #11 GUI thread naming; #15 app fonts; #24 input at the frame start; #104 animation time base; #116/#119 offscreen scene and its input; #118 MSAA window swap; #137 one render per pump; #165/#100 initial placement; #174/#216 WSL GPU and Wayland hang; #214 deferred first Expose; #233/#234 borderless / fullscreen; #258 log path; #301 WM_GETDLGCODE; #314 several vvvv apps per process; #319–#321 test touch, synthetic right click; #358/#367 shared device and device loss; #359 resources; #374 focus release; #376 popup focus in tests; #427 bundled font; #488 pipeline cache; #517 window icon; #526 window destroyed inside the pump; #544 change count.
- **Decided:** S2, O2, P4, P14, P17 → the files of section 4 go into the folders of TARGET.md 2.3 with snake_case names, and includes name the folder (e.g. `scene/commands.h`): `app/` (`exports.h`, `last_error`, `log`, `app.cpp`, `animation_clock`, `render_gate`, `scene_change`, `input_delivery.cpp`, `fonts`, `thread_name.h`), `platform/` (`platform.h`, `platform_win.cpp`, `platform_linux.cpp`), `window/` (`scene_handle.h`, `window`, `window_exports.cpp`, `window_filters`, `focus_release`, `touch_input`), `diagnostics/perf_exports.cpp`, `test/` (`test_input.cpp`, `test_probes.cpp`); the handle struct is `vlqt::SceneHandle` in `window/scene_handle.h`, not `Handle` in `handle.h`; the cut runs in wave 4 after the native break (P4), so the exports are cut under their final names (`vlqt_scene_*`, A5); the bare `#NNN` references are removed in wave 3 (P14), this cut rewrites only the comments that need a new sentence and splits the file per K3; the gate includes the WSL build `tools/build-native.sh` (P17)
- **Effort:** L, mechanical: no (moves are mechanical, the handle header and the removal of the offscreen bridge are not)
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, the new files of section 4, `src/VL.Qt.Native/offscreen.cpp`, `src/VL.Qt.Native/offscreen.h`, `src/VL.Qt.Native/test_offscreen.cpp`, `src/VL.Qt.Native/CMakeLists.txt`
- **Depends on:** none; NAPP-02, -03, -06, -07, -08, -09 are easier after it but can go first
- **Verification:** `tools/build.ps1 -Test` (native build, all SmokeTest scenarios); Linux build via `tools/build-native.sh`, since the split moves `#ifndef _WIN32` code

### NAPP-02 Make the last error per thread and owned in one place
- **Kind:** threading
- **Severity:** must
- **Breaking:** no
- **Where:** `vlqt.cpp:91`, `94-95`, `598-609`, `1186-1189`, `1006-1009`; `offscreen.cpp:233`, `748-760`; `test_offscreen.cpp:39-44`
- **Finding:** `g_lastError` is a plain `std::string`. It is written by `setError` from every thread that runs an export through `guarded`: the exports documented as "any thread" (`vlqt_image_write` from the FileWriter, `vlqt.h:366`; `vlqt_shader_reflect` from the shader node factory, `vlqt.h:315`; `vlqt_res_read_audio` on VL.Audio's audio thread, `vlqt.h:356`) set it when they throw. Two writers at once, or a writer while C# copies the string from the pointer `vlqt_last_error` returned (`Native.cs:170`), is a data race on the string buffer (undefined behaviour; the pointer can dangle after a reallocation). The message is also never cleared, so a negative return that sets no message (`vlqt_window_get_scene_metrics`, `vlqt_window_get_apply_error`, the `-required` of `vlqt_window_poll`) leaves the previous, unrelated text, which `SceneCore.cs:230` then shows ("poll: …"). `offscreen.cpp` has a second guard (`guardedCode`, `offscreen.cpp:748-760`) that reports an exception as `VLQT_OFFSCREEN_BAD_ARGUMENT`.
- **Proposal:** New `last_error.h/.cpp` with `namespace vlqt { void setLastError(std::string); void setLastError(const QString&); const char* lastError(); }` backed by `thread_local std::string`. `vlqt_last_error` returns the calling thread's message, which is what every C# caller wants (it calls right after the failing call on the same thread). One guard template in `exports.h`, `template <class F> int guarded(const char* function, int errorCode, F&& f)`, used by `vlqt.cpp`, `offscreen.cpp` and the test files; an exception becomes `VLQT_ERR_INTERNAL` (NAPP-03), not a bad-argument code. Every negative return sets a message; `vlqt.h` says "valid until the next failing call on the same thread".
- **Decided:** B4, P22, P4, S2 → per-thread last error as proposed (B4 (a)); one issue with CAPI-01 (CAPI-01 = NAPP-02), in the first pull request of step 3 of the native break (return codes and per-thread error text, together with NAPP-03 = CAPI-02); files `app/last_error.h/.cpp` and `app/exports.h`; `vlqt_image_write` drops its own error buffer (B4); B1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → exceptions return the shared `VLQT_ERR_INTERNAL` of the one table `vlqt_result`
- **Effort:** S, mechanical: yes (after the header exists)
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, `offscreen.cpp`, `offscreen.h`, `test_offscreen.cpp`, new `last_error.h`, `last_error.cpp`, `exports.h`, `vlqt.h` (doc of `vlqt_last_error`)
- **Depends on:** none
- **Verification:** SmokeTest scenarios that read `LastError()` after failures (`apply`, `handlers`, `fonts` – including "add from another thread", `FontTest.cs:153`); a unit-level SmokeTest that calls `vlqt_shader_reflect` with a bad stage on a worker thread while the main thread provokes an apply error, and checks both messages

### NAPP-03 One result code convention for the C ABI
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: C# `Native.cs` and the SmokeTest change their checks; patches see nothing
- **Where:** `vlqt.h:1`, `148-160`, `184-185`, `231-262`, `476`; `vlqt.cpp:598-609`, `1341-1354`, `1513-1537`, `1766-1793`, `1818-1826`, `2085-2111`, `2321-2332`, `2428-2432`
- **Finding:** The header says "Return 0 = ok unless noted", and then notes something different almost everywhere. `-1` is "null window" (`vlqt_window_*`), "exception" (`guarded`, `vlqt.cpp:608`), "app not initialized", "wrong thread" (fonts) and, in `vlqt_test_perf_text`, also `-(0+1)` = "buffer too small" (`vlqt.cpp:2123-2124`). `vlqt_window_change_count` answers a null window with the resource code `-9` (`vlqt.cpp:2323-2327`), `vlqt_window_resize` and `_set_samples` with `VLQT_OFFSCREEN_BAD_ARGUMENT`, `vlqt_window_set_icon` with a bare `-7`, `vlqt_window_grab` with `-2/-3`. The codes are literals in `vlqt.cpp` while `protocol.h` has named codes for apply, resources and offscreen. Twelve exports are not guarded at all (`vlqt_window_take_close_request`, `_is_visible`, `_get_overlay`, `_get_debug`, `_get_mode`, `_samples`, `_get_scene_metrics`, `_get_apply_error`, `vlqt_test_perf_spike`, `vlqt_test_perf_stats_text`, `vlqt_test_font_refs`, `vlqt_set_pipeline_cache`). The `void` exports (`vlqt_app_process_events(_for)`, `vlqt_window_destroy/show/hide`, `vlqt_res_shutdown`) swallow an exception: the caller learns nothing. `vlqt_app_init` cannot report the most likely start failure at all, because Qt aborts the process with `qFatal` when no platform plugin loads; `vlqt.h` does not say so.
- **Proposal:** One enum in `vlqt.h` (C# mirror in `Protocol.cs`): `VLQT_OK = 0, VLQT_ERR_INTERNAL = -1 (exception, see vlqt_last_error), VLQT_ERR_NOT_INITIALIZED = -2, VLQT_ERR_WRONG_THREAD = -3, VLQT_ERR_NULL_HANDLE = -4, VLQT_ERR_BAD_ARGUMENT = -7, VLQT_ERR_NOT_SUPPORTED = -8, VLQT_ERR_UNKNOWN_ID = -9`; buffer-size answers stay `-required` but every such export documents `required >= 16` so it cannot collide (or uses an `int* required` out parameter; recommended, it removes the overload of the return value). Apply, offscreen and resource codes keep their own named enums but take the shared values for the shared meanings (null handle, bad argument, unknown id). Every export goes through the guard of NAPP-02, including the getters (cost: one try block, no allocation). `vlqt_app_process_events_for` returns `int`. `vlqt.h` documents in its first comment: return convention, `vlqt_last_error`, "Qt may abort the process at `vlqt_app_init` when no platform plugin can be loaded; the log then has the reason".
- **Decided:** P22, P4, B4 → one issue with CAPI-02 (CAPI-02 = NAPP-03); it is the result-code part of the first pull request of step 3 of the native break, after NAPP-02 and with the per-thread error text (B4); the renames of the second step-3 pull request (NAPP-17, CAPI-03) come after it, so the export names in `Where` are still the old ones when this lands; the codes change once and later additions raise the protocol version (P4); B1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → one table `vlqt_result` (CAPI-02), also for `poll` (B2)
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.h`, `vlqt.cpp`, `offscreen.cpp`, `test_offscreen.cpp`, `protocol.h`, `src/VL.Qt/Native.cs`, `src/VL.Qt/Protocol.cs`, the C# callers that compare literal codes (`SceneCore.cs`, `Renderer.cs`, `WindowRenderer.cs`, `LoadFontNode.cs`, `OffscreenRenderer.cs`), `src/VL.Qt.SmokeTest/*.cs` that check literal codes
- **Depends on:** NAPP-02
- **Verification:** `dotnet run --project src/VL.Qt.SmokeTest -c Release -- --drift` (extend the drift check to the new enum), full SmokeTest

### NAPP-04 State the thread of every export and check it
- **Kind:** threading
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:88-92`, `146`, `332-498`, `866-873`, `1106-1109`, `2355-2362`, `2412-2416`; `offscreen.cpp:609-617`
- **Finding:** The threads that touch this file and their state:

  | State | Written by | Read by | Safe? |
  |---|---|---|---|
  | `g_log` (`:88`, atomic) | `vlqt_app_init` (GUI), `DllMain`/ELF destructor | `emitLog` on any thread (Qt messages from render threads, resource workers, Qt's own threads) | yes; the C# callback must be thread-safe (other area) |
  | `g_lastError` (`:91`) | any thread through `guarded` | any thread through `vlqt_last_error` | no, NAPP-02 |
  | `g_app`, `g_driver`, `g_nativeDir` (`:89`, `:92`, `:146`) | once in `vlqt_app_init` (GUI) | GUI thread; `g_app` also by `vlqt_font_add` on other threads (rejected there) | yes after init; the start is not synchronized, fine for a single host thread |
  | `g_pipelineCachePath` (`:90`) | `vlqt_set_pipeline_cache` (C# `Initialization.cs:32`) | `initShared` (GUI) | yes if the initializer runs on the main thread; not checked |
  | `g_fonts`, `g_nextFontHandle` (`:872-873`) | GUI thread (checked) | GUI thread; `vlqt_test_font_refs` unchecked | yes |
  | `UpdateGate::s_gates/s_inPump/s_delivering/s_children` (`:492-497`) | GUI thread; **the sync hook of an offscreen scene's gate runs on that scene's render thread** (`:339-346`, gate created at `:911`) | same | yes only because `renderThreaded` runs the syncs of all render threads one after another with `call()` while the main thread waits (`offscreen.cpp:609-617`). A parallel sync would race on `s_children`. Not written down anywhere. |
  | `s_active`, `s_perf` (`:1106-1109`) | GUI thread | GUI thread | yes, unless the pump re-enters (NAPP-11) |
  | test devices (`:1914`, `:1956`) | GUI thread | GUI thread | yes |

  Hand-overs in this file: queued `MetaCall`s delivered in `vlqt_driver_advance` (`:1179`); `processEvents` delivers posted events and the Win32 messages of the host thread (`:1123-1124`); the window-system queue flushed in `vlqt_app_deliver_input` (`:1161`); `DeferredDelete` flushed after destroy and swap (`:1253`, `:1737`); the device context lock in `vlqt_window_grab` (`:1369-1371`) and the frame locks released after every pump (`:1127`). There is no mutex in this file. Only the font exports check the thread (`:2355-2362`, `:2412`); every other GUI-thread export called from a wrong thread is undefined behaviour inside Qt, with no message.
- **Proposal:** (1) A helper in `exports.h`: `bool onGuiThread(const char* function)` (one `QThread::currentThread()` compare, sets `VLQT_ERR_WRONG_THREAD`), called by the guard variant `guardedGui(...)` that every GUI-thread export uses; the "any thread" exports (`vlqt_image_write`, `vlqt_shader_reflect`, `vlqt_res_read_audio`, `vlqt_now_ns`, `vlqt_last_error`) use the plain guard. (2) `vlqt.h` gets one line per group: "GUI thread (the thread of vlqt_app_init)" or "any thread". (3) In the change-counter file (NAPP-06) a comment and an assertion (`Q_ASSERT` that no two syncs run at once, e.g. an atomic flag) that names the invariant "syncs of all scenes are serialized by the batch". (4) `docs/architecture.md` gets a "Threads" subsection with the table above, shortened.
- **Decided:** A5, S2 → the exports are named by CAPI-03: the any-thread ones are `vlqt_image_write_file` (was `vlqt_image_write`), `vlqt_shader_reflect`, `vlqt_audio_tap_read` (was `vlqt_res_read_audio`), `vlqt_audio_tap_now_ns` (was `vlqt_now_ns`) and `vlqt_last_error`, the GUI-thread check in the verification is on `vlqt_scene_apply`; the helpers live in `app/exports.h`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp` (or the split files), `exports.h`, `vlqt.h`, `docs/architecture.md`
- **Depends on:** NAPP-02, NAPP-03
- **Verification:** SmokeTest `fonts` (thread check), a new SmokeTest check that `vlqt_window_apply` from a worker thread returns `VLQT_ERR_WRONG_THREAD`; `qttextureperf` with several render threads

### NAPP-05 Isolate and list every use of Qt private API
- **Kind:** architecture
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:45-51`, `57-59`, `67-72`, `396-403`, `432-486`, `587`, `951-976`, `990-992`, `1086`, `1155-1161`, `1584-1589`, `1919`, `1961`, `1992-1994`, `2034-2045`; `CMakeLists.txt:17`, `63`
- **Finding:** Qt gives no compatibility promise for private headers (`*_p.h`) and only a limited one for QPA (`qpa/`) and RHI (`rhi/`) headers; the project pins Qt 6.12 (`CMakeLists.txt:17`). The uses in this file:

  | Header | Used for | Where | Public way? |
  |---|---|---|---|
  | `QtCore/private/qabstractanimation_p.h` | `QAbstractAnimationTimer`, `QUnifiedTimer::startAnimationTimer` for the keep-alive of the vvvv clock | `:151-157`, `:1086` | Partly: an endless `QAbstractAnimation` subclass (`duration() == -1`, empty `updateCurrentTime`) started once also keeps the driver running, but counts as one running animation (perf overlay must subtract it) and must never be paused. See NAPP-10. |
  | `QtQuick/private/qquickitem_p.h` | `dirtyAttributes`, `nextDirtyItem`, `childItems`, dirty bits for the change count | `:432-486` | No. The change count needs Qt's dirty list. |
  | `QtQuick/private/qquickwindow_p.h` | `QQuickWindowPrivate::dirtyItemList`, `deliveryAgentPrivate()` | `:399`, `:439` | No. |
  | `QtQuick/private/qquickdeliveryagent_p_p.h` | `delayedTouch`, `deliverDelayedTouchEvent()` for input at the frame start | `:396-403` | No; the most fragile one (a `_p_p` header). |
  | `QtGui/private/qhighdpiscaling_p.h` (included twice, `:57` and `:72`) | `QHighDpi::toNativeWindowGeometry` (Linux borderless); `toNativeLocalPosition/GlobalPosition` (test queue input) | `:1588`, `:1993-1994`, `:2039` | For the tests: multiply by `window->devicePixelRatio()` (what `QHighDpi` does for one screen). For Linux borderless: no. |
  | `QtGui/private/qwindow_p.h` | `QWindowPrivate::positionPolicy` (Linux borderless) | `:1587` | No. |
  | `QtGui/qpa/qplatformwindow.h` | `QPlatformWindow::setGeometry` (Linux borderless) | `:1586-1588` | No. |
  | `QtGui/private/qeventpoint_p.h` | `QMutableEventPoint` to build touch points | `:961-963` | No for synthetic touch through `sendEvent`. |
  | `QtGui/private/qpointingdevice_p.h` | `pointById` / `removePointById`, what `QGuiApplication` does around a touch | `:951-976`, `:990-992` | Possibly: `QWindowSystemInterface::handleTouchEvent<SynchronousDelivery>` registers points itself; whether it works for a render-control window without platform window is unverified. |
  | `QtGui/qpa/qwindowsysteminterface.h` | production: `windowSystemEventsQueued` / `flushWindowSystemEvents` (`:1155-1161`); tests: `registerInputDevice`, `handleMouseEvent`, `handleTouchEvent`, `handleFocusWindowChanged` | see left | No public way to flush the input queue without rendering. |
  | `rhi/qrhi.h` | `QRhiSwapChain::sampleCount` | `:1771` | Semi-public (RHI API, limited guarantee). |

  The comment "TEST ONLY" above `qeventpoint_p.h` and `qpointingdevice_p.h` (`vlqt.cpp:67`) is wrong: `sendTouch`/`cancelTouch` also deliver the touch input of offscreen scenes (`offscreen.cpp:889-905`), which ships to users.
- **Proposal:** Each private use moves into the one file that owns its subject (section 4): `scene_change.cpp` (item and window private), `input_delivery.cpp` (delivery agent, window-system queue), `touch_input.cpp` (event point, pointing device), `platform_linux.cpp` (high DPI, window private, QPA window), `animation_clock.cpp` (animation private), `test_input.cpp` (test-only QPA calls). Each file starts with "Private Qt API: <what>, checked on every Qt update". The table above goes to `docs/qt-private-api.md` (with the private uses of other files added by their reviews) and the Qt update checklist in `docs/development.md` links it. Test code computes native positions with `devicePixelRatio()` and drops `qhighdpiscaling_p.h` from the test path.
- **Decided:** D9, S2 → private Qt classes are allowed, and the classes in use are listed in one place, the head comment of `elements/elements.h` (the checklist for a Qt update), instead of a new `docs/qt-private-api.md` and a section in `docs/development.md`; the code goes into the files of TARGET.md 2.3: `app/scene_change.cpp`, `app/input_delivery.cpp`, `window/touch_input.cpp`, `platform/platform_linux.cpp`, `app/animation_clock.cpp`, `test/test_input.cpp`; the column 'Public way?' is information only, no use has to move to a public API
- **Effort:** M, mechanical: no
- **Touches:** the split files of section 4, `docs/qt-private-api.md` (new), `docs/development.md`, `docs/README.md`
- **Depends on:** NAPP-01
- **Verification:** native build on Windows and Linux; SmokeTest `touch`, `input-latency`, `measure`, `borderless` / `fullscreen` scenarios (whichever exist under these names), `qttexture` input

### NAPP-06 Split UpdateGate into the pump gate and the scene change counter
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:325-498`, `909-911`, `1122-1125`, `1163`, `1707-1711`, `1812`, `1838`, `2328`
- **Finding:** `UpdateGate` has three jobs: it gates rendering to the pump (event filter, `beginPump`/`endPump`, per-app blocking), it hands delayed touch to Qt Quick for the input delivery (`deliverDelayedTouch`, a static that walks all gates), and it counts scene changes for tooltips (`sceneDirty`, `isGrabSource`, `inPerfOverlay`, `contentChildrenChanged`, `changeCount`, `markChanged`, `continueFrom`). An offscreen scene creates a gate only for the third job and does not install its event filter (`vlqt.cpp:909-911`), and its sync hook then runs on a render thread (NAPP-04). The name says only the first job. `rendered()` stays false forever for a scene, so `vlqt_window_poll` polishes a scene on every poll (NAPP-15).
- **Proposal:** Three types:
  - `vlqt::RenderGate` (`render_gate.h/.cpp`): the event filter and the pump statics (`beginPump(bool all, span<RenderGate*>)`, `endPump()`, `inPump()`, `rendered()`, `beginDelivery()/endDelivery()` as an RAII `RenderGate::DeliveryScope`). Windows only.
  - `vlqt::SceneChangeCounter` (`scene_change.h/.cpp`): connects to `beforeSynchronizing`, owns `sceneDirty` and its helpers, `count()`, `markChanged()`, `continueFrom()`. Windows and scenes. Holds the private API of `qquickitem_p.h` / `qquickwindow_p.h` and the serialization comment of NAPP-04. Its scratch vector becomes a member (one vector per counter, allocated once) instead of a shared static, which removes the cross-thread hazard at no per-frame cost.
  - `deliverDelayedTouch(QQuickWindow*)` moves to `input_delivery.cpp` next to `vlqt_app_deliver_input`; it walks the handles, not the gates.
  The `m_rendered` flag set by the counter's sync hook moves to the gate (it connects to `beforeSynchronizing` itself).
- **Decided:** S2, A5 → files `app/render_gate.h/.cpp`, `app/scene_change.h/.cpp` and `app/input_delivery.cpp`; the export of the counter is `vlqt_scene_get_change_count` (was `vlqt_window_change_count`; shared by windows and offscreen scenes, CAPI-03); `perf.cpp` is `diagnostics/perf_*` (NDIAG-04)
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp` (or split files), new `render_gate.*`, `scene_change.*`, `input_delivery.cpp`, `perf.cpp` only if it reads the gate (check), `CMakeLists.txt`
- **Depends on:** NAPP-01
- **Verification:** SmokeTest `pump` (one render per pump, several apps), `input-latency` (#24 path), tooltip/change-count scenario (`vlqt_window_change_count` with snapshots and the perf overlay open), `qttexture` change count

### NAPP-07 Give the window handle a name, an owner and no test state
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:551-573`, `899-916`, `1203-1256`, `1692-1738`
- **Finding:** `struct Window` is the handle behind every `void*` of the `vlqt_window_*` and offscreen exports. Its name collides with `QWindow`/`QQuickWindow` (the struct has a member `window`, so `w->window->window()` style code reads badly). Ownership is manual: `engine`, `scene`, `perf`, `window` are raw `new` and deleted in a fixed order in `vlqt_window_destroy` (`:1246-1252`), while `closeFilter` and `gate` are children of the window and die with it, and `replaceWindow` re-creates the latter and `perf` (`:1708-1718`). The order matters (perf before scene, scene before window, window before engine, render control after window) and is explained only in two inline comments. Five members exist only for the test exports (`testButtons`, `testClock`, `testTouch`, `testQueuedButtons`, `testQueuedTouch`, `:562-566`). The `fs*` members are the saved geometry of fullscreen.
- **Proposal:** `struct vlqt::Handle` in `handle.h` (rename `Window` → `Handle`; `vlqt::SceneHandle` is the alternative if "handle" is too vague for the reader – recommended: `Handle`, it matches the C ABI's word), members in destruction order with `std::unique_ptr` for owned objects (`engine`, `scene`, `perf`) and `QPointer` for children of the window (`closeFilter`, `gate`, `changes`). A destructor `~Handle()` does what `vlqt_window_destroy` does now, with one comment block on the order. `struct FullscreenRestore { QPoint framePos; QSize size; Qt::WindowStates states; }` as `std::optional<FullscreenRestore>` replaces `fsSaved` + three fields. Test state moves to `std::unique_ptr<TestInput> test` created on first use by `test_input.cpp`. `bool isOffscreen() const`.
- **Decided:** O2, S2 → `vlqt::SceneHandle` in `window/scene_handle.h` (was `Window`; not `Handle`, the name O2 did not take); C# uses `SceneHandle` too (CAPI-03); if this issue separates the window half from it, that half is `vlqt::WindowScene`, otherwise the name stays unused in C++; the test state `TestInput` is created by `test/test_input.cpp`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp` (or split files), new `handle.h`, `offscreen.cpp`, `offscreen.h`, `test_offscreen.cpp`
- **Depends on:** NAPP-01
- **Verification:** SmokeTest scenarios that destroy windows inside the pump (`GroupShaderWindowDestroyTest`), the MSAA swap (`MsaaTest`), device loss (`DeviceLostTest`), offscreen create/destroy; the Linux ASan preset (`tools/test-linux.sh --sanitize`)

### NAPP-08 One small platform interface instead of #if blocks
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:53-65`, `301-323`, `536-549`, `611-742`, `1035-1037`, `1057-1059`, `1074-1080`, `1555-1590`, `2368-2370` (font key), `2438-2452`
- **Finding:** Ten `#ifdef _WIN32` / `#ifndef _WIN32` blocks in one file, of four kinds: environment preparation before the app (WSL GPU probe, display check, D3D11 graphics API), work after the app exists (bundled font, `WM_GETDLGCODE` filter), window behaviour (borderless keeping the client rect), and process plumbing (environment read for the test filter, unload hook). The Linux part alone is 130 lines (`:611-742`) in the middle of the window code. `clampWindowSamples` passes a DXGI format constant also on Linux (`:824-828`), which reads as a bug although `device.cpp` presumably ignores it on OpenGL.
- **Proposal:** `platform.h`:
  ```cpp
  namespace vlqt::platform {
  void prepareBeforeApp();                       // Win: D3D11; Linux: WSL GPU default, display check
  void initAfterApp(QGuiApplication& app);        // Win: dialog-code filter; Linux: bundled font fallback
  bool testSyntheticInputOnly();                  // process environment
  void setFrameless(QQuickWindow& window, bool on, bool keepClientRect);
  QString fontKey(const QFileInfo& file);         // case folding on Windows
  }
  ```
  with `platform_win.cpp` and `platform_linux.cpp` selected in `CMakeLists.txt`. `DllMain` / the ELF destructor go to the same two files (they call `vlqt::log::detach()`). `clampWindowSamples` passes the swap chain format through `device.h` (`vlqt::device::windowSwapChainFormat()`), so the D3D constant lives in `device.cpp`.
- **Decided:** S2, P17 → files `platform/platform.h`, `platform/platform_win.cpp`, `platform/platform_linux.cpp`; `windowSwapChainFormat` is in `device/device.h`; the local WSL build `tools/build-native.sh` is part of the gate (P17), the CI only at the checkpoints (P18)
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, new `platform.h`, `platform_win.cpp`, `platform_linux.cpp`, `device.h`, `device.cpp`, `CMakeLists.txt`
- **Depends on:** NAPP-01
- **Verification:** Windows build and SmokeTest (`keyroute` for `WM_GETDLGCODE`, borderless toggles); Linux build and `tools/test-linux.sh` (WSL probe, offscreen borderless, font fallback)

### NAPP-09 Separate the test exports and name them alike
- **Kind:** tests
- **Severity:** should
- **Breaking:** yes: test export names change (SmokeTest and `Native.cs` only)
- **Where:** `vlqt.h:228`, `235-246`, `325-327`, `376-384`, `386-432`, `434-470`, `493-494`; `vlqt.cpp:500-549`, `562-566`, `575-596`, `1356-1383`, `1430-1440`, `1488-1498`, `1844-2180`, `2290-2318`, `2428-2432`; `PerfOverlayTest.cs:19-28`
- **Finding (K6 for this area):** Exports that exist only for the SmokeTest and ship in `vlqt.dll`: `vlqt_test_send_mouse`, `_send_wheel`, `_send_touch`, `_queue_mouse`, `_queue_touch`, `_send_key`, `_send_key_text`, `_measure_count`, `_handler_count`, `_perf_spike`, `_perf_text`, `_perf_stats_text`, `_device_info`, `_device_lost`, `_res_read_pixels`, `_write_image`, `_res_start`, `_res_transition`, `_res_element_ref`, `_font_refs`, plus the D3D11 probes of `test_offscreen.cpp`, plus three without the prefix: `vlqt_window_grab` (`vlqt.h:228`, marked "test"; used only by SmokeTest and Probe), `vlqt_perf_eval`, `vlqt_perf_host_eval` (only `PerfStatsTest.cs`). `vlqt_test_perf_stats_text` is not in `vlqt.h` at all and is declared ad hoc in `PerfOverlayTest.cs:23`; it overlaps `vlqt_test_perf_text` page 0. The synthetic-input filter `SystemInputFilter` is installed in production windows when an environment variable is set (`vlqt.cpp:819-820`). The `Window` handle carries the test input state (NAPP-07).
  How the same tests could work without them: the computations (`vlqt_perf_eval`, `_host_eval`) are pure functions and could be tested by a small C++ test executable linked to `perf.cpp`; the input tests need synthetic delivery and have no realistic replacement (OS-level `SendInput` would be flaky on CI and cannot do multitouch). So the exports stay, but visibly separated.
- **Proposal:** (1) All test exports live in `test_input.cpp`, `test_probes.cpp` (counters, perf text, device info/lost, resources, fonts, `window_grab` renamed `vlqt_test_window_grab`) and the existing `test_offscreen.cpp`, declared in `vlqt_test.h` (not in `vlqt.h`), all with the `vlqt_test_` prefix: `vlqt_perf_eval` → `vlqt_test_perf_eval`, `vlqt_perf_host_eval` → `vlqt_test_perf_host_eval`. (2) Drop `vlqt_test_perf_stats_text` in favour of `vlqt_test_perf_text(w, 0, …)` if the SmokeTest assertion reads the same text (check `PerfOverlayTest.cs:23-30`), else declare it in `vlqt_test.h` and guard it. (3) The C# declarations move from `Native.cs` into the SmokeTest project (`NativeTest.cs`), so the library's P/Invoke surface has no test functions. (4) Whether to compile them out of release packages is Question 1.
- **Decided:** F1, P22, P4, S2 → question 1 is answered A: the test exports stay in every build, separated in `vlqt_test.h` and `test_*.cpp`; one issue with CAPI-04 = TEST-08, and PROTO-06 (the test constants of `protocol.h` into `vlqt_test.h`) belongs to it; it is step 2 of the native break (P4), in wave 3 before the cut of `vlqt.cpp`, so the exports move straight into `test/test_input.cpp` and `test/test_probes.cpp` and the dependency on NAPP-01 and NAPP-07 goes; the renames of NAPP-17 are step 3 and are not done in one pass with this one; `test_offscreen.cpp` becomes `testkit/testkit.cpp` (F2, NDEV-09); the C# declarations go to `src/VL.Qt.TestSupport/NativeTest.cs`, linked into both test projects (TARGET.md 2.5), not into the SmokeTest project
- **Effort:** M, mechanical: mostly
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, `vlqt.h`, new `vlqt_test.h`, `test_input.cpp`, `test_probes.cpp`, `test_offscreen.cpp`, `CMakeLists.txt`, `src/VL.Qt/Native.cs`, `src/VL.Qt.SmokeTest/*.cs` (new `NativeTest.cs`), `src/VL.Qt.Probe/Program.cs`
- **Depends on:** NAPP-01, NAPP-07
- **Verification:** full SmokeTest; `--drift`

### NAPP-10 Animation clock: own file, guard the input, correct the scrubbing claim
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no (the doc correction); yes if Question 3 is answered with "rewind"
- **Where:** `vlqt.cpp:124-157`, `1082-1086`, `1168-1184`; `docs/architecture.md:19-36`
- **Finding:** The driver is correct for a clock that moves forward and holds. Three points: (a) `setTime` converts `seconds * 1000.0` to `qint64` without a check (`:135`); a NaN or infinite time from a broken clock is undefined behaviour in C++. (b) Suspicion, not run: `QUnifiedTimer::updateAnimationTimers` (Qt 6, `qabstractanimation.cpp`) advances animations only when the delta is positive, so a clock that jumps back does not rewind QML animations; they hold until the clock passes the old time again. `docs/architecture.md:31-32` promises "Deterministic: same tree and time, same output" and "Scrubbing and pause". Confirm with a SmokeTest: start a `NumberAnimation` in a `QmlElement`, advance to 2 s, back to 1 s, read the value. (c) The keep-alive uses private API (NAPP-05) and the driver and keep-alive objects are leaked by design (`:1083-1086`) without saying so at the declaration.
- **Proposal:** `animation_clock.h/.cpp` with `namespace vlqt::clock { void install(); int advance(double seconds); }`; `advance` rejects a non-finite time with `VLQT_ERR_BAD_ARGUMENT`. A comment on the two objects: "live as long as the process, like the QGuiApplication". Correct `docs/architecture.md`: QML animations follow the clock forward and stop with it; a jump back does not rewind them (if confirmed). The public alternative to the keep-alive (endless `QAbstractAnimation`) is recorded in `docs/qt-private-api.md` as the fallback if the private class changes.
- **Decided:** E11, 0.2, S2, A5, D9 → question 3 is answered A (document, no rewind; Breaking: no); wave 1 confirms or drops the suspicion with the scrub check before this issue is cut; the file is `app/animation_clock.h/.cpp` and the export `vlqt_driver_advance` is `vlqt_app_advance_animations` (CAPI-03); the fallback note about the public keep-alive goes where D9 puts the private-API list (head comment of `elements/elements.h`), not into `docs/qt-private-api.md`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, new `animation_clock.h/.cpp`, `offscreen.cpp` (calls `vlqt_driver_advance`, `offscreen.cpp:645`, → `vlqt::clock::advance`), `docs/architecture.md`, `CMakeLists.txt`
- **Depends on:** none
- **Verification:** SmokeTest `qml` (animation time base, `QmlTest.cs:213`); the new scrub check
- **Wave 1 (W1-15):** confirmed by `AnimationScrub` for (b) (a `NumberAnimation` at 10 per second: v = 20.17 after 2 s of the frame clock, still 20.17 after the jump back to 1 s, 25.17 at 1.5 s: animations neither rewind nor hold until the old time, they go on forward from their value with every positive delta); (a) and (c) are plain code facts and need no run.

### NAPP-11 Refuse a nested pump instead of corrupting it
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:1099-1140`, `367-389`
- **Finding:** `vlqt_app_process_events_for` keeps its state in statics (`s_active`, `s_perf`, `UpdateGate::s_inPump`, `m_blocked`). `processEvents` dispatches the Win32 messages of the host thread (the comment at `:1107-1108` says a host callback can run inside it). If such a callback reaches `QtApp` and pumps again, the inner call clears `s_active`/`s_perf` while the outer loop later iterates `s_perf` (`:1135-1137`), and its `endPump` sets `s_inPump = false` while the outer pump still runs, so the rest of the outer pump swallows update requests. `vlqt_app_deliver_input` already protects itself (`:1150`); the pump does not. `QtApp.cs:370-400` has no re-entrancy flag either. Not observed; whether vvvv can run a frame inside a message dispatch of `processEvents` would be confirmed by a debug assertion in the pump.
- **Proposal:** At the start of the pump: `if (RenderGate::inPump()) return VLQT_ERR_NOT_SUPPORTED` with the message "nested pump", and a warning in the log (once). Document in `vlqt.h`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp` (or `app.cpp`), `vlqt.h`
- **Depends on:** NAPP-03 (return type)
- **Verification:** SmokeTest `pump`; a new check that calls `vlqt_app_process_events` from a posted `QMetaObject::invokeMethod` callback is not possible from C#, so verify with a debug build in vvvv (restart a patch while windows are open, the #526 scenario)

### NAPP-12 Run the WSL GL probe as a separate process, not a fork of the host
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:626-667`, `676-687`
- **Finding:** `probeGlx` forks the vvvv/.NET process, which has many threads at that point, and the child calls `dlopen` and X11/GL functions. After `fork` in a multithreaded process only async-signal-safe functions are allowed in the child; `dlopen` and `malloc` take locks another thread may have held at the fork, so the child can deadlock. The parent then waits the full 10 s, and a second probe (`:682`) may wait another 10 s, and both read the hang as "no GL" and downgrade to llvmpipe or the software backend. Not observed; the code comment says the probe "only uses libX11/libGL through dlopen", which is exactly the unsafe part.
- **Proposal:** Ship a tiny executable `vlqt-glprobe` (same C code, `main` instead of the child branch) next to `libvlqt.so` and start it with `posix_spawn`; keep the 10 s timeout. Alternative: keep the fork but `exec` `/proc/self/exe` is impossible for a library; so the helper binary is the recommendation. Both probes can be one run of the helper with two attempts.
- **Decided:** S2, P17 → the probe code moves to `platform/platform_linux.cpp`, the helper source is `tools/glprobe.c` as proposed (TARGET.md 2.3); the local WSL build is part of the gate (P17)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp` (→ `platform_linux.cpp`), new `src/VL.Qt.Native/tools/glprobe.c`, `CMakeLists.txt`, `tools/deploy-linux.sh`
- **Depends on:** NAPP-08
- **Verification:** `tools/test-linux.sh` under WSLg; the log line "WSLg detected" without a fallback on a working system

### NAPP-13 One helper per handle export instead of forty copies
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `vlqt.cpp:1271-1842` and `1844-2180` (about forty copies of the null-window block); duplicate logic at `772-784` and `1604-1618`; `1999-2050` vs `936-979`
- **Finding:** Every handle export repeats `auto* w = static_cast<Window*>(p); if (!w) { setError("… null window"); return -1; }`, with the message spelt differently each time; the offscreen check repeats too. The screen with the largest share of a rect is computed twice (`placeWindow` and `screenWithLargestShare`). `vlqt_test_queue_touch` repeats the active-point bookkeeping of `sendTouch`.
- **Proposal:** `template <class F> int withHandle(void* p, const char* function, F&& f)` in `exports.h` (null check, GUI-thread check, guard, message `"<function>: null handle"`), and `withWindow(...)` that also rejects offscreen scenes with `VLQT_ERR_NOT_SUPPORTED`. `QScreen* screenWithLargestShare(const QRect&)` used by both callers. A `TouchPoints` class (`touch_input.h`) owns the active points and produces both the `QEventPoint` list and the `QWindowSystemInterface::TouchPoint` list.
- **Decided:** S2, O7 → helpers in `app/exports.h`, the touch bookkeeping in `window/touch_input.h`; the pointer is a handle (O7), so the message says 'null handle' as proposed
- **Effort:** M, mechanical: mostly
- **Touches:** the split files of section 4, `exports.h`, `touch_input.*`
- **Depends on:** NAPP-01, NAPP-03
- **Verification:** full SmokeTest

### NAPP-14 Fix the comments and docs that say something untrue
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `vlqt.cpp:67`, `744-751`, `808-809`, `830-831`; `vlqt.h:176-180`, `228`; `docs/architecture.md:63`
- **Finding:** (a) `vlqt.cpp:67` marks private headers "TEST ONLY" that production uses (NAPP-05). (b) The comment block `vlqt.cpp:744-749` describes `placeWindow`, but `defaultIcon` (`:750-760`) was inserted between it and the function, so it reads as the icon's comment. (c) `initShared`'s comment starts with "... and," continuing the comment of `installWindowFilters` (`:830`); each depends on the other's comment to be read. (d) `vlqt.h:179` documents `vlqt_set_pipeline_cache` as returning "0 / -7"; it always returns 0 (`vlqt.cpp:1197-1201`) and does not enforce "before the first window". (e) `docs/architecture.md:63` says offscreen "Rendering runs on the main thread for now; a Qt render thread is planned", while `vlqt.h:570-576` and `render_thread.cpp` run scenes on a pool with one render thread by default. (f) `docs/architecture.md:48` ("No `exec()` and no render thread") is right only for windows; say so.
- **Proposal:** Rewrite each comment in place at its function; `vlqt.h` for `vlqt_set_pipeline_cache`: "0; a call after the first window has no effect on existing windows". `docs/architecture.md`: windows render on the GUI thread in the pump; offscreen scenes render on a render thread pool (`vlqt_offscreen_set_threaded`), link `decisions/render-texture.md`.
- **Decided:** G5, E7, A5 → the link is `docs/design/render-texture.md` (the folder is `docs/design/`, the page names follow the DOCD reports); the text says that the size of the render-thread pool is the pin `Render Threads` of QtTexture and QtRenderer (0 = main-thread mode), not an environment variable, and names the export `vlqt_offscreen_set_render_threads` (was `vlqt_offscreen_set_threaded`, CAPI-03)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, `vlqt.h`, `docs/architecture.md`
- **Depends on:** none
- **Verification:** reading; no behaviour change

### NAPP-15 Polish an offscreen scene in the poll only when it needs it
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `vlqt.cpp:1836-1839`, `350-351`, `909-911`
- **Finding:** `vlqt_window_poll` polishes the scene when the gate says it did not render in the last pump. For an offscreen scene the gate never sees a pump render (`m_rendered` is set only `if (s_inPump && !m_blocked)`, and scenes render in `vlqt_offscreen_render_batch`), so every poll of every scene runs `Scene::polish`. The batch polishes again before sync (`offscreen.cpp:583`). Suspicion on the cost: `polish` is cheap when no item asked for one; measure with the F5 overlay on a scene with many layouts.
- **Proposal:** After NAPP-06 the decision belongs to the handle: a window asks its `RenderGate::rendered()`, a scene asks the offscreen state whether the last batch polished it (`offscreen::polishedSinceLastPoll(State*)`).
- **Decided:** O2, A5 → the scene state is `OffscreenScene` (`vlqt::OffscreenScene`, was `offscreen::State`), so the function is `polishedSinceLastPoll(OffscreenScene*)`; the poll export is `vlqt_scene_poll` (was `vlqt_window_poll`, CAPI-03)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp` (→ `window_exports.cpp`), `offscreen.cpp`, `offscreen.h`
- **Depends on:** NAPP-06
- **Verification:** SmokeTest `measure` (EV_MEASURED stays correct for scenes), `qttextureperf` numbers before/after

### NAPP-16 Write down the lifetime at unload and at process exit
- **Kind:** lifetime
- **Severity:** could
- **Breaking:** no
- **Where:** `vlqt.cpp:1066-1069`, `1078-1079`, `1083-1086`, `1909-1922`, `1953-1964`, `2436-2452`; `ExportedApp.cs:106-113`
- **Finding:** What happens today: the `QGuiApplication`, the animation driver, the keep-alive timer, the test devices and the registered app fonts live until the process ends; windows not destroyed by C# stay alive too. At DLL detach only the log callback is cut (`:2440-2452`); the Qt message handler stays installed and points into `vlqt.dll`. That is safe at process exit (DLLs are not unmapped then), and .NET never unloads a native library, but an unload with `FreeLibrary`/`dlclose` would leave Qt calling into unmapped code. On Linux, exported apps end with `_exit` (`ExportedApp.cs:113`), so the ELF destructor does not run there. The static `DialogCodeFilter` (`:1078`) unregisters itself from the still-alive app in its destructor at DLL detach. None of this is written in one place; a reader has to reconstruct it from five comments.
- **Proposal:** In the logging file (section 4) at detach: `qInstallMessageHandler(nullptr)` when the library is being unloaded (Windows: `lpReserved == nullptr`; ELF destructor: always), in addition to cutting `g_log`. One paragraph "Lifetime" in `docs/architecture.md` (QtNative part): what lives until exit and why, what C# must destroy (`vlqt_res_shutdown`, windows), and that unloading QtNative is not supported.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp` (→ `log.cpp`, `platform_*.cpp`), `docs/architecture.md`
- **Depends on:** NAPP-08
- **Verification:** SmokeTest exit (no output after "done"), an exported app ending on Windows and Linux without a crash

### NAPP-17 Name the exports alike
- **Kind:** naming
- **Severity:** could
- **Breaking:** yes: C# `Native.cs` only
- **Where:** `vlqt.h:173-262`, `476`, `340`, `481`
- **Finding:** Getters are named three ways: `vlqt_window_get_mode`, `_get_overlay`, `_get_debug`, `_get_perf` vs `vlqt_window_samples`, `vlqt_window_change_count` vs `vlqt_window_is_visible`. `vlqt_window_create` exists only as a short form for tests and the probe; the library uses `_create_at`. `vlqt_res_snapshot` and `_snapshot_fit` take a window handle but sit in the `res` group (fine), yet `vlqt_window_*` exports take offscreen scene handles under the name "window".
- **Proposal:** Getters `vlqt_window_get_*` throughout (`vlqt_window_get_samples`, `vlqt_window_get_change_count`); keep `is_visible`. Remove `vlqt_window_create` and call it `vlqt_window_create_at` in tests. Rename the handle group only if Question 2 says so.
- **Decided:** A5, P4, CAPI-03 → NAPP Q2 is answered 'rename': the exports that windows and offscreen scenes share are `vlqt_scene_*` (`vlqt_window_get_perf`, `_get_overlay`, `_get_debug`, `_report_host_frame`, `_apply`, `_poll` … → `vlqt_scene_…` with the same suffix, `vlqt_window_change_count` → `vlqt_scene_get_change_count`, `vlqt_window_get_scene_metrics` → `vlqt_scene_get_metrics`; the host-frame pair becomes `vlqt_scene_report_host_pump` / `vlqt_scene_get_host_pump`), the window-only ones stay `vlqt_window_*` with getters `vlqt_window_get_*` (`vlqt_window_samples` → `vlqt_window_get_samples`, `is_visible` stays); this is the mechanical second pull request of step 3 of the native break (P4), the test-export renames of NAPP-09 are step 2, so the two are not one pass; the full table is the one of CAPI-03; B3 of [DECISION-ROUND.md](../DECISION-ROUND.md) → one function `vlqt_window_create` that takes the position (as CAPI-03); `vlqt_window_create_at` goes
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/vlqt.h`, `vlqt.cpp`, `src/VL.Qt/Native.cs`, C# callers, `src/VL.Qt.SmokeTest/*.cs`, `src/VL.Qt.Probe/Program.cs`
- **Depends on:** NAPP-09 (do both renames in one pass)
- **Verification:** build, full SmokeTest

## 4 Target structure

All files in `src/VL.Qt.Native/`, namespace `vlqt` (sub-namespaces as noted). The exports stay `extern "C"` and are thin; each `*_exports.cpp` contains only exports of its group.

**C ABI headers**
- `vlqt.h` – public C ABI: result codes (NAPP-03), log callback, app, windows/handles, perf, apply/poll, resources, fonts, offscreen (unchanged content minus tests). Optionally split later into `vlqt_res.h` and `vlqt_offscreen.h` included by `vlqt.h`.
- `vlqt_test.h` (new) – all `vlqt_test_*` declarations (from `vlqt.h:228`, `235-246`, `325-327`, `376-470`, `493-494`).

**Shared plumbing**
- `exports.h` (new) – `guarded`, `guardedGui`, `withHandle`, `withWindow`, `onGuiThread` (NAPP-02/-04/-13).
- `last_error.h/.cpp` (new) – thread-local last error, `vlqt_last_error` (from `vlqt.cpp:91-95`, `1186-1189`).
- `log.h/.cpp` (new) – `g_log`, `vlqt::log::emit(level, category, msg)`, Qt message handler, `detach()` (from `vlqt.cpp:86-122`).
- `handle.h` (new) – `vlqt::Handle` (renamed from `Window`, `vlqt.cpp:551-573`), `FullscreenRestore`, `TestInput` forward declaration (NAPP-07).

**Application**
- `app.cpp` (new) – `vlqt_app_init`, `vlqt_app_process_events(_for)`, `vlqt_set_pipeline_cache` (from `vlqt.cpp:1015-1140`, `1197-1201`); calls `platform::prepareBeforeApp/initAfterApp`, `clock::install`, `fonts::endFrame`.
- `animation_clock.h/.cpp` (new) – `VlqtAnimationDriver` → `vlqt::clock::Driver`, `VlqtKeepAlive` → `vlqt::clock::KeepAlive`, `install()`, `advance()`, export `vlqt_driver_advance` (from `vlqt.cpp:124-157`, `1168-1184`).
- `render_gate.h/.cpp` (new) – `RenderGate` (split from `UpdateGate`, `vlqt.cpp:325-423`).
- `scene_change.h/.cpp` (new) – `SceneChangeCounter` (split from `UpdateGate`, `vlqt.cpp:339-346`, `353-364`, `425-486`), export `vlqt_window_get_change_count` (renamed from `vlqt_window_change_count`, `vlqt.cpp:2321-2332`).
- `input_delivery.cpp` (new) – `vlqt_app_deliver_input`, `deliverDelayedTouch` (from `vlqt.cpp:390-403`, `1142-1166`).

**Windows and handles**
- `window.h/.cpp` (new) – creation (`newEngine`, `initShared`, `installWindowFilters`, `placeWindow`, `defaultIcon`, `clampWindowSamples`), `setMode` with borderless/fullscreen (`applyFrameless` platform part via `platform::setFrameless`, `applyBorderless`, `applyFullscreen`, `screenWithLargestShare`), `replaceWindow` → `window::swapForSamples` (from `vlqt.cpp:744-853`, `1539-1738`).
- `window_exports.cpp` (new) – all `vlqt_window_*` exports except perf and change count (from `vlqt.cpp:1191-1383`, `1500-1537`, `1663-1842`).
- `window_filters.h/.cpp` (new) – `CloseFilter`, `FullscreenKeys` (from `vlqt.cpp:159-201`).
- `focus_release.h/.cpp` (new) – `FocusRelease` and `releaseFocus` (from `vlqt.cpp:203-299`, `918-934`; used by windows and `offscreen.cpp`).
- `touch_input.h/.cpp` (new) – `sendTouch`, `cancelTouch`, `TouchPoints` (from `vlqt.cpp:936-994`; used by `offscreen.cpp` and tests). Not to be confused with the existing `touch.cpp` (the touch handler element); the name says "input".
- `perf_exports.cpp` (new, or appended to `perf.cpp`) – `vlqt_window_get_perf*`, host frame exports, overlay exports (from `vlqt.cpp:1385-1517`, minus the two eval functions).

**Fonts, resources, platform**
- `fonts.h/.cpp` (new) – `FontEntry`, registry, `endFrame`, `vlqt_font_add`, `vlqt_font_release` (from `vlqt.cpp:855-894`, `2346-2426`).
- `resources.cpp`, `image_resource.cpp`, `shader_resource.cpp`, `snapshot_resource.cpp`, `audio_tap.cpp`, `image_writer.cpp` (existing) – each gets its own `vlqt_res_*` / `vlqt_shader_reflect` / `vlqt_image_write` / `vlqt_now_ns` exports (from `vlqt.cpp:2182-2288`, `2295-2298`, `2334-2344`), using `exports.h`.
- `platform.h`, `platform_win.cpp`, `platform_linux.cpp` (new) – NAPP-08 (from `vlqt.cpp:301-323`, `536-549`, `611-742`, `1555-1590`, `2436-2452`).
- `tools/glprobe.c` (new, Linux only) – NAPP-12.

**Tests (shipped, see Question 1)**
- `test_input.cpp` (new) – `SystemInputFilter`, `TestInput`, test devices, `testKeyTarget`, `vlqt_test_send_*`, `vlqt_test_queue_*` (from `vlqt.cpp:500-549`, `575-596`, `1844-2050`, `2147-2180`).
- `test_probes.cpp` (new) – `vlqt_test_measure_count`, `_handler_count`, `_perf_spike`, `_perf_text`, `_perf_eval` (renamed), `_perf_host_eval` (renamed), `_window_grab` (renamed), `_device_info`, `_device_lost`, `_res_*`, `_write_image`, `_font_refs`; `vlqt_test_perf_stats_text` deleted or declared (NAPP-09).
- `test_offscreen.cpp` (existing) – unchanged apart from `exports.h`.

**Deleted**
- `vlqt.cpp` (nothing left), the `vlqt::offscreen::createHandle/stateOf/quickWindow/setError` bridge (`vlqt.cpp:899-1011`, `offscreen.h`), `guardedCode` in `offscreen.cpp`, `vlqt_window_create` (NAPP-17), the second include of `qhighdpiscaling_p.h`.

**Public:** only the `extern "C"` functions of `vlqt.h` (and `vlqt_test.h`, Question 1); everything else is hidden by `-fvisibility=hidden` / no `dllexport`, as today.

## 5 Questions for Sebastian

1. **Test exports in the shipped `vlqt.dll`?** Today about thirty test functions ship with every release, including synthetic input and a forced device loss (`vlqt_test_device_lost`).
   - A: keep them in the release binary, separated in `vlqt_test.h` and `test_*.cpp` (NAPP-09). The CI SmokeTest tests exactly the binary that ships. Cost: the exports are visible to anyone who looks.
   - B: a CMake option `VLQT_TEST_EXPORTS` (on in development and CI, off in the NuGet package). The SmokeTest then tests a different build than the one users get; the CI would build twice or test the test build only.
   - Recommendation: A. The functions are harmless without a caller, and testing the shipped binary is worth more.

2. **Name of the handle.** The `vlqt_window_*` exports take windows and offscreen scenes. Keep the `window` prefix and document it (today's state), or rename the shared part to `vlqt_scene_*` (apply, poll, debug, overlay, perf, change count) and keep `vlqt_window_*` for the window-only functions? A rename makes the C ABI say the truth; it touches every P/Invoke in `Native.cs`. Recommendation: rename together with NAPP-03 and NAPP-17, in one change, since breaking changes are allowed now and the C ABI is internal to VL.Qt.

3. **Animations and a clock that goes back** (NAPP-10, if confirmed). Qt animations are delta-based and do not rewind. Options: A, document it ("QML animations follow the clock forward"); B, on a backward jump, restart the QML engine's animations from the new time, which Qt has no API for and would mean tracking every animation. Recommendation: A.

## 6 For other areas

- offscreen (`offscreen.cpp:748-760`): `guardedCode` turns an exception into `VLQT_OFFSCREEN_BAD_ARGUMENT`; and check that `offscreen::release` waits for a render job still running from the last batch before `vlqt_window_destroy` deletes the window (`vlqt.cpp:1244-1251`, `offscreen.cpp:626-633` posts render jobs that outlive the call).
- C# interop (`src/VL.Qt/Native.cs`, `QtApp.cs`): the log callback passed to `vlqt_app_init` is called from any thread (render threads, resource workers); verify the managed side is thread-safe and its delegate is kept alive for the process lifetime.
- C# interop (`src/VL.Qt/SceneCore.cs:225-230`): shows `LastError()` for a poll that returned `-required`, which sets no message; the text is stale (NAPP-02).
- perf (`perf.cpp`): `vlqt_perf_eval` / `vlqt_perf_host_eval` are pure computations that a C++ unit test could cover instead of an export.
- docs (`docs/architecture.md:63`): offscreen rendering is described as main-thread only; it runs on a render-thread pool by default (NAPP-14).
