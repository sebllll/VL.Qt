# Review 19 – QtNative: device, offscreen rendering, render thread

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/device.h`, `device.cpp`, `offscreen.h`, `offscreen.cpp`, `render_thread.h`, `render_thread.cpp`, `threadname.h`, `test_offscreen.cpp`, `CMakeLists.txt`, `VL.Qt.Native.csproj`, `docs/decisions/device-sharing.md`. Skimmed: `docs/decisions/render-texture.md` (sections "Decided" and "Qt render thread"), `CMakePresets.json`, excerpts of `vlqt.cpp` (pump, `guarded`, grab), `image_resource.cpp` (ContextLock users), `tools/build.ps1` / `build-native.ps1` (thread modes, RID). Not read: `linear_pass.*`, the SmokeTest sources (only located by search).

## 1 Summary

The area is in good technical shape: one shared D3D11 device per process with a working loss/rebuild path, offscreen scenes that render into a foreign (Stride) texture ordered by two shared fences, and a small render-thread pool with a clear lock-order rule. The design decisions are documented and the SmokeTest covers binding, leaks, device loss and both thread modes. What a newcomer struggles with is the shape, not the idea: `offscreen.cpp` is 1,200 lines with five jobs, `device.cpp` mixes about 150 lines of test-only D3D11On12 machinery into the production path, three COM ownership styles coexist, and the headers carry design notes and project history instead of contracts.

The changes that matter most:

1. **NDEV-01 (must):** the threaded render batch hands a render thread a pointer into a `std::vector` that the next batch may reallocate: a use-after-free with a pool of two or more threads.
2. **NDEV-02:** a failed device creation or rebuild is never retried; the process stays without a shared device and offscreen scenes silently move to a private device.
3. **NDEV-03 / NDEV-09:** take the test machinery out of the production files (D3D11On12 into its own file, the `vlqt_test_d3d11_*` helpers out of `vlqt.dll`).
4. **NDEV-04 / NDEV-05:** split `offscreen.cpp` by responsibility and give the two render paths (main thread, render thread) one sequence.
5. **NDEV-08:** CMake defaults every Windows build to `win-arm64`, and the only Windows preset hard-codes an ARM64 Qt path.

## 2 What works and must survive

- **One shared device, never released at exit.** Device, immediate context and upload QRhi live until process end (`device.cpp:34-37`); only `rebuild` replaces them. Releasing them at exit would run after Qt's own teardown.
- **Rebuild order** (`device.cpp:496-554`): frame locks released → listeners `lost` → upload QRhi deleted → device retired (not released) → new device → windows on the old device get the new one with `setGraphicsDevice` and an update → listeners `restored`. A retired device is released only when no window QRhi points to it any more (`releaseRetired`, `device.cpp:315-333`).
- **Adapter move only while nothing renders on the device** (`canMove`, `device.cpp:459-473`): Qt replaces a window's QRhi only after it noticed a loss. Offscreen scenes (render-control windows) do not count.
- **Lock order** (`device.h:101-108`): the shared immediate context is multithread-protected; stateful sequences take `ContextLock` (recursive `ID3D11Multithread::Enter/Leave`). The main thread never waits for a render-thread job while holding it.
- **Blocking jobs drain.** Jobs of one render thread run in queue order, so a blocking `call` runs after every posted render. Unbind, destroy, device loss, adapter move and `testLost` rely on this (`render_thread.h:7-9`, `offscreen.cpp:66-72`).
- **The write fence is signalled on every path** (`renderAndSignal`, `offscreen.cpp:549-569`): the foreign context already waits for it; a value never signalled hangs the host's GPU.
- **Fence values count from 0 per binding**; the NT handle of a fence is closed right after opening (`offscreen.cpp:345-359`).
- **WARP fallback** (`State::software`, `offscreen.cpp:112-114`, `630-638`): a software device deadlocks on a wait-before-signal, so its batch waits for the submission.
- **Thread affinity** as in the table of `render-texture.md` ("(e) Thread affinity"): items, polish, input, bind on the main thread; initialize, beginFrame, sync, render, endFrame, invalidate on the render thread; `prepareThread` once at create, before `initialize`.
- **Render threads are never stopped** (`render_thread.h:11`): stopping them at exit would run after Qt.
- **Hot paths:** `vlqt_offscreen_render_batch` per host frame (reused `static std::vector threaded`, reused `ThreadBatch` lists, no allocation of its own); `device::checkLost` twice per pump (only `GetDeviceRemovedReason` while healthy); `FrameLock` per window frame (one `QueryInterface`). Known exception: each `RenderThread::post`/`call` allocates a Qt slot object and event (`QMetaObject::invokeMethod`), two per render thread per frame. Acceptable; keep it at that.

## 3 Findings

### NDEV-01 Give the threaded batch lists a stable address
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/offscreen.cpp:519-527`, `offscreen.cpp:591-607`, `offscreen.cpp:622-634`
- **Finding:** `renderThreaded` posts a render job that captures `list = &b.lists[b.current]`, a pointer into an element of `std::vector<ThreadBatch> s_threadBatches`, and returns without waiting (`offscreen.cpp:625-633`). In the next `render_batch`, a scene on a render thread that has not rendered before makes `s_threadBatches.emplace_back()` (`offscreen.cpp:595`). If that reallocates, the old `ThreadBatch` objects are moved and freed while the posted job of the previous batch may not have started yet: it then dereferences a freed `std::vector`. Nothing drains the old thread before the `emplace_back` (the blocking sync jobs come later, `offscreen.cpp:609-617`). It needs a pool of two or more threads (`VLQT_OFFSCREEN_THREAD=2`, `vlqt_offscreen_set_threaded(2)`) and a scene on a second thread that becomes dirty for the first time after another thread has rendered, which is the normal case when a second `QtTexture` is added to a running patch. The default `tools/build.ps1 -Test` runs only pool sizes 1 and 0 (`tools/build.ps1:84-96`), so no test exercises this.
- **Proposal:** keep one batch per render thread at a fixed address. Preferred: `std::array<ThreadBatch, render_thread::maxThreads> s_threadBatches` indexed by the thread's pool index (add `int RenderThread::index() const`, the constructor already receives it), with `used`/`software` reset per batch. Alternative: `std::vector<std::unique_ptr<ThreadBatch>>`. The posted job may keep its pointer then. Add a SmokeTest run with `VLQT_OFFSCREEN_THREAD=2` (two scenes, the second bound a few frames after the first) to `tools/build.ps1 -Test`.
- **Decided:** E7, P7, A5, S2 → question 4 is answered (a): a pool of two or more threads is supported, with a pool-2 run in `tools/build.ps1 -Test`; the size is the pin `Render Threads` of QtTexture and QtRenderer (default 1, hidden), not `VLQT_OFFSCREEN_THREAD`, so the new run sets it through `vlqt_offscreen_set_render_threads` (was `vlqt_offscreen_set_threaded`, CAPI-03); a larger value adds threads at once and a smaller one applies to scenes created afterwards (P7), which the fixed `std::array` indexed by the pool index fits; files `offscreen/offscreen_render.cpp` and `device/render_thread.h`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/offscreen.cpp` (or `offscreen_render.cpp` after NDEV-04), `src/VL.Qt.Native/render_thread.h`, `tools/build.ps1`, `src/VL.Qt.SmokeTest/QtTextureTest*.cs`
- **Depends on:** none
- **Verification:** SmokeTest `qttexture` with `VLQT_OFFSCREEN_THREAD=2` (new run in `build.ps1 -Test`), `qttextureperf` with pool 2; ideally once under Application Verifier / page heap.

### NDEV-02 Retry a failed device creation and keep scenes off private devices
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/device.cpp:229-233`, `device.cpp:265-271`, `device.cpp:360-366`, `device.cpp:412-417`, `device.cpp:521-552`, `src/VL.Qt.Native/offscreen.cpp:294-303`, `offscreen.cpp:393-395`
- **Finding:** `ensureDevice` sets `s_tried = true` before it tries; on failure `s_dev` stays null and nothing tries again: `uploadRhi` retries only while `!s_tried`, `checkLost` returns at once without `s_dev`. After a failed `rebuild` (for example right after a TDR, while the driver still resets: plausible, not confirmed) the process stays without a shared device for its whole life: image uploads return null, new offscreen scenes fail at create (`offscreen.cpp:182`). Existing scenes are worse off: `rebuild` gives their windows an empty `QQuickGraphicsDevice` (`device.cpp:535`), and `deviceRestored` still calls `initialize` (`offscreen.cpp:298`), so Qt creates a private D3D11 device for each scene on its render thread. Such a scene can bind (or fails with `ADAPTER_MISMATCH`, since `canMove` is false), its loss is never noticed (`checkLost` only watches `s_dev`), and images of the shared device cannot be drawn in it.
- **Proposal:** (1) Replace `s_tried` by a retry timer: after a failed creation, `checkLost` tries `ensureDevice` again at most once per second (`QElapsedTimer`, no allocation) and, on success, runs the same window/listener step as `rebuild` (steps 4 and 5) so windows that fell back to their own device are moved only after their next loss (as today) and scenes rebind. (2) In `offscreen.cpp`, `deviceRestored` and the retry in `bind` call `initialize` only when `device::isAttached(window)` (NDEV-06); without a shared device `bind` returns `VLQT_OFFSCREEN_DEVICE_LOST` ("try again next frame", which the C# side already handles). Write the policy into `device.h` in one sentence.
- **Decided:** B8, S2 → question 3 is answered (a), as proposed: retry once per second, windows keep their own devices meanwhile, images do not upload, offscreen scenes report `DEVICE_LOST`, and the log says so once (not at every retry); the test switch lives in `test/test_device.*`; files `device/device.*` and `offscreen/offscreen_scene.cpp`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/device.cpp`, `device.h`, `src/VL.Qt.Native/offscreen.cpp`
- **Depends on:** NDEV-06
- **Verification:** device-loss scenario of the SmokeTest (`DeviceLostTest.cs`) extended by a forced creation failure (a test switch in the D3D11On12 test file of NDEV-03 that fails the next creation once); `qttexture` device-loss cases in both thread modes.

### NDEV-03 Move the D3D11On12 test device out of the production device code
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/device.cpp:13`, `device.cpp:40-51`, `device.cpp:129-227`, `device.cpp:245-254`, `device.cpp:304-307`, `device.cpp:515-519`, `device.cpp:561-588`, `src/VL.Qt.Native/device.cpp:4`, `src/VL.Qt.Native/device.h:98-99`
- **Finding:** (K6 for this area.) About 150 of the 620 lines of `device.cpp` exist only for `vlqt_test_device_lost`: the D3D11On12 creation with a device factory and a singleton fallback, `dropWindowRhisOnRetired` (which needs the private `qsgrenderloop_p.h`), the globals `s_dev12`/`s_queue12`, two extra fields in `Retired`, and `testLost`. They are interleaved with `ensureDevice` and `rebuild`, so a reader of the production path has to understand Windows 11 24H2 device-factory behaviour first. `testLost` also makes `device.cpp` include `render_thread.h` (for `drainAll`) while `render_thread.h` includes `device.h`: a dependency cycle created by a test hook. The machinery itself is needed: D3D11 has no way to remove a device, D3D12 has (`ID3D12Device5::RemoveDevice`), and the test must remove QtNative's own device.
- **Proposal:** new internal files `device_testing.h/.cpp` (Windows only in CMake) with `HRESULT createOn12(IDXGIAdapter1*, UINT flags, Objects& out)`, `bool isOn12Requested()` (today `testD3D11On12`), `void dropWindowRhisOn(const std::vector<Objects>& retired)` and `int removeDevice(const Objects&)`. `device.cpp` keeps one struct `Objects { ComPtr<ID3D11Device> dev; ComPtr<ID3D11DeviceContext> ctx; ComPtr<IUnknown> keepAlive; }` for the current and the retired devices (`keepAlive` holds the D3D12 device and queue of a test device; production leaves it empty) and calls `createOn12` in one place of `ensureDevice`. Move `render_thread::drainAll()` from `testLost` into the export `vlqt_test_device_lost` in `vlqt.cpp`, so `device.cpp` no longer includes `render_thread.h`. Rename `testLost` → `removeForTest`.
- **Decided:** F1, S2, TARGET.md 2.7 → the test device is `test/test_device.h/.cpp` (not `device_testing.*`); the export `vlqt_test_device_lost` is declared in `vlqt_test.h` and defined in `test/test_probes.cpp` (NAPP-09); `device/` keeps `device.*`, `context_lock.*`, `render_thread.*`, `renderer_info.*`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/device.cpp`, `device.h`, new `device_testing.h`, `device_testing.cpp`, `src/VL.Qt.Native/vlqt.cpp` (export `vlqt_test_device_lost`), `CMakeLists.txt`
- **Depends on:** NDEV-07 (do both in one pass)
- **Verification:** device-loss scenario (`DeviceLostTest.cs`) and the `qttexture` device-loss cases with `VLQT_TEST_D3D11ON12=1`, both thread modes.

### NDEV-04 Split offscreen.cpp by responsibility and name the scene `Scene`
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/offscreen.cpp:1-1192`, `src/VL.Qt.Native/offscreen.h:23-50`
- **Finding:** `offscreen.cpp` holds five things: scene lifecycle and the device listener (`163-212`, `276-303`, `678-721`), the D3D11 binding (open texture, fences, formats, MSAA, adapter match: `214-486`), two render paths (`488-672`), input injection with its own render-control subclass for focus (`139-161`, `761-1057`), and the C exports (`1061-1191`). Three `#ifdef _WIN32` blocks cut across these, and two anonymous namespaces are opened and closed four times. `offscreen.h` is a two-way contract: its first half declares functions that `vlqt.cpp` implements for `offscreen.cpp` (`createHandle`, `stateOf`, `quickWindow`, `setError`, `releaseFocus`, `sendTouch`, `cancelTouch`), its second half the reverse. A reader cannot tell from the header who provides what. `State` is a name that says nothing; the thing is an offscreen scene.
- **Proposal:** files (all in `vlqt::offscreen`):
  - `offscreen.h` – only what `vlqt.cpp` and `snapshot_resource.cpp` call: `release`, `destroy`, `isScene`, `requestFrame`, `testState`. The functions `vlqt.cpp` provides go into the headers of their topic (see section 6).
  - `offscreen_scene.h` – internal: `struct Scene` (renamed from `State`; binding fields in a nested `struct Binding` under `#ifdef Q_OS_WIN`, input fields in `struct InputState`), `Scene* sceneOf(const QQuickWindow*)`, `onRenderThread`.
  - `offscreen_scene.cpp` – `create`, `release`, `destroy`, `deviceLost`/`deviceRestored`, `dropRhi`, `isScene`, `requestFrame`, `testState`.
  - `offscreen_binding.cpp` (Windows) – `bind`, `unbind`, `createFence`, `isAllowedFormat` (today `formatFits(format, format)`, `offscreen.cpp:387`), `formatFits`, `isSoftware`, `matchAdapter`.
  - `offscreen_render.cpp` (Windows) – `renderBatch` and the batch of NDEV-05.
  - `offscreen_input.cpp` – `SceneRenderControl(Private)`, pointing devices, mouse/touch/pen/wheel/key/text, `setFocus`.
  - `offscreen_api.cpp` – the `vlqt_offscreen_*` exports with `guardedCode`; the non-Windows stubs there, in one `#ifndef` block.
- **Decided:** O2, S2, P22, TARGET.md 3.3, P17 → `State` becomes `OffscreenScene` (`vlqt::OffscreenScene`, header `offscreen/offscreen_scene.h`), not `offscreen::Scene`, which would be a second `Scene` in QtNative; the files are those of TARGET.md 2.3, all in `offscreen/`: `offscreen.h`, `offscreen_scene.*`, `offscreen_binding.cpp`, `offscreen_render.cpp`, `offscreen_input.cpp`, `offscreen_exports.cpp` (was `offscreen_api.cpp`), `linear_pass.*`; the bridge functions of `vlqt.cpp` go away with `SceneHandle` (NAPP-07), NAPP-07, NDEV-04 and CAPI-03 stay three issues that share the names of O2; it is a pure move in the second native round of wave 4, after NAPP-01, and the gate includes the WSL build (P17)
- **Effort:** M, mechanical: yes (moves and one rename; no logic change)
- **Touches:** `src/VL.Qt.Native/offscreen.cpp` (deleted), `offscreen.h`, new `offscreen_scene.h/.cpp`, `offscreen_binding.cpp`, `offscreen_render.cpp`, `offscreen_input.cpp`, `offscreen_api.cpp`, `CMakeLists.txt`, `src/VL.Qt.Native/vlqt.cpp` (includes), `snapshot_resource.cpp` (include)
- **Depends on:** do NDEV-01 first (small fix on the current file), NDEV-05 together with or after this
- **Verification:** `tools/build.ps1 -Test` (both thread modes of `qttexture`, `msaa`, `multirenderer`, input cases of `QtTextureTest.Input.cs`); `vlqt.map` / exported symbols unchanged (`dumpbin /exports` before and after).

### NDEV-05 One frame sequence for both thread modes
- **Kind:** performance
- **Severity:** should
- **Breaking:** no (Stride's GPU may start Qt's frame earlier in main-thread mode)
- **Where:** `src/VL.Qt.Native/offscreen.cpp:489-514`, `offscreen.cpp:529-639`, `offscreen.cpp:663-666`
- **Finding:** the frame sequence (read signal, wait, polish, beginFrame, sync, render, endFrame, linear pass, write signal, flush, foreign wait) exists twice: `render` for the main-thread mode and `beginAndSync` + `renderAndSignal` + `renderThreaded` for the render thread. They have already drifted: the threaded path flushes the foreign context after the read signals so Qt's GPU can start before the host's next flush (`offscreen.cpp:587`, reason in `render-texture.md`: Stride flushes only at Present); the main-thread path does not, so in that mode Qt's GPU frame cannot start before Stride's Present. The threaded path catches exceptions per step, the main path does not. The TODO about GPU time is duplicated (`506`, `564`). A fix in one path will be forgotten in the other.
- **Proposal:** one batch function for both modes. The per-thread grouping stays; for scenes without a render thread the "thread" is the main thread and `call`/`post` run the job inline (a tiny `Executor` with `call(F)` and `post(F)` that either forwards to the `RenderThread` or calls directly; `onRenderThread` becomes `executorOf(scene).call(...)`). Then the sequence is: per scene Signal(read) + polish → one foreign Flush → per executor `beginAndSync` (blocking) → foreign Waits (non-software) → per executor `renderAndSignal` (posted; blocking for software) → foreign Waits (software). Delete `render`. Whether the main-thread mode stays at all is question Q2.
- **Decided:** E6, P7, S2 → question 2 is answered (a): one batch path with an inline executor, as proposed, and the switch stays as the value 0 of the pin `Render Threads` (E7) with `VLQT_OFFSCREEN_THREAD` removed, so the perf verification sets the count through `vlqt_offscreen_set_render_threads` (CAPI-03); a scene gets its executor (render thread or inline) when it is created and keeps it, since a smaller value and 0 apply only to scenes created afterwards (P7); files `offscreen/offscreen_render.cpp`, `offscreen/offscreen_scene.cpp`, `device/render_thread.h`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/offscreen.cpp` (or `offscreen_render.cpp`, `offscreen_scene.cpp` after NDEV-04), `render_thread.h`
- **Depends on:** NDEV-01, NDEV-04
- **Verification:** `qttexture` in both thread modes (`build.ps1 -Test`), WARP cases, `qttextureperf` with `VLQT_OFFSCREEN_THREAD=0` (main-thread numbers should not get worse).

### NDEV-06 Replace the dynamic property `_vlqt_shared_device` by a device function
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/device.cpp:344`, `device.cpp:532`, `device.cpp:536`, `src/VL.Qt.Native/offscreen.cpp:181-182`, `offscreen.cpp:394`, `src/VL.Qt.Native/qtimageitem.cpp:81`
- **Finding:** whether a window got the shared device is stored as a Qt dynamic property with a string name and read in three files. The contract ("set by `attach`, cleared by a failed rebuild, read before the window has a QRhi") is only visible by searching for the string; a typo compiles and silently returns false.
- **Proposal:** `bool device::isAttached(const QQuickWindow* window)` in `device.h` ("True if attach or the last rebuild gave window the shared device; also before the window has a QRhi. Main thread."). The storage stays private to `device.cpp` (the property is fine there, or a `QSet<const QQuickWindow*>` cleaned on `destroyed`). Callers in `offscreen.cpp` and `qtimageitem.cpp` use the function.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/device.h`, `device.cpp`, `offscreen.cpp`, `qtimageitem.cpp`
- **Depends on:** none
- **Verification:** `qttexture` (create fails without a shared device: `VLQT_SHARED_DEVICE=0` case if present, else manual), device-loss scenario.

### NDEV-07 One COM ownership style: `Microsoft::WRL::ComPtr`
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/device.cpp:35-52`, `device.cpp:87-127`, `device.cpp:298-312`, `device.cpp:433-441`, `device.cpp:597-619`, `src/VL.Qt.Native/offscreen.cpp:103-111`, `offscreen.cpp:216-224`, `offscreen.cpp:466-472`, `src/VL.Qt.Native/test_offscreen.cpp:22-37`
- **Finding:** the area uses three ways to own a COM pointer: raw pointers with hand-written `Release` on every path (`device.cpp`, e.g. `chooseAdapter` with five release sites), WRL `ComPtr` for locals that are then `Detach`ed into raw `State` fields and released by a local `releaseCom` (`offscreen.cpp`), and a hand-made `ComPtr` template (`test_offscreen.cpp:23-37`). Each early return in `device.cpp` is a chance to leak; a reader has to check every path.
- **Proposal:** WRL `ComPtr` (header-only, Windows SDK) for every COM pointer in Windows code: `s_dev`, `s_ctx`, `Retired` (→ `Objects` of NDEV-03; `ComPtr::Reset()` returns the remaining reference count for the leak warning in `release`), the binding fields of the scene (`ComPtr<ID3D11Texture2D> texture`, …; `unbind` becomes `binding = {}` after the render-target reset), `chooseAdapter`/`adapterLuidOf` locals, `ContextLock::m_multithread`. Delete `releaseCom` and the test `ComPtr`. Raw pointers stay only at the C ABI and in `void*` API of `device.h` (needed for the Linux build).
- **Decided:** F2, S2 → `test_offscreen.cpp` is moved to `testkit/testkit.cpp` by NDEV-09 (step 2 of the native break, before this issue), so the hand-made `ComPtr` is replaced there; the test device is `test/test_device.*` (NDEV-03)
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/device.cpp`, `device.h` (private member type behind `#ifdef`), `offscreen.cpp` (or its split files), `test_offscreen.cpp`
- **Depends on:** do together with NDEV-03; after NDEV-04 if both are planned
- **Verification:** leak test of `qttexture` (`QtTextureTest.Leak.cs`: reference counts and handle count after 1000 cycles), device-loss scenario (no "still has reference(s)" warning in the log).

### NDEV-08 CMake: correct RID default, an x64 preset, Windows defines in one place
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/CMakeLists.txt:7-14`, `CMakeLists.txt:20`, `CMakeLists.txt:73-78`, `CMakeLists.txt:90`, `CMakeLists.txt:119`, `src/VL.Qt.Native/CMakePresets.json` (`dev-win-arm64`), `src/VL.Qt.Native/offscreen.cpp:38-45`, `src/VL.Qt.Native/threadname.h:11-19`
- **Finding:** (1) On Windows `VLQT_RID` defaults to `win-arm64` whatever the target (`CMakeLists.txt:8`), while the Linux branch derives it from the processor. The scripts pass `-DVLQT_RID` (`build-native.ps1:48`), but a newcomer who follows the csproj comment ("open this folder in VS (CMake)") on an x64 machine builds an x64 `vlqt.dll` into `runtimes/win-arm64/native`. (2) The only Windows preset is `dev-win-arm64` with `CMAKE_PREFIX_PATH` hard-coded to `C:/Qt/6.12.0/msvc2022_arm64`; there is none for x64, the architecture most contributors have. (3) The 40 sources stand on one line (`CMakeLists.txt:20`), so a diff that adds a file is unreadable and the Windows-only files are split between that line and `target_sources` at line 76. (4) `NOMINMAX`/`WIN32_LEAN_AND_MEAN` are defined per file (`offscreen.cpp:38-40`, `threadname.h:12-17`), which forces an include order that `offscreen.cpp:45` has to explain. (5) History markers a newcomer cannot resolve: "L3" (`CMakeLists.txt:90`, `119`) and issue numbers (K1).
- **Proposal:** (1) Windows: `if(CMAKE_CXX_COMPILER_ARCHITECTURE_ID STREQUAL "ARM64") set(_rid win-arm64) else() set(_rid win-x64) endif()` as the default of `VLQT_RID`. (2) Add preset `dev-win-x64`; take the Qt path from an environment variable with the setup-script default (`"CMAKE_PREFIX_PATH": "$env{VLQT_QT_DIR}"`, documented in `docs/development.md`, `tools/setup-qt.ps1` prints the value) instead of a hard-coded version. (3) `add_library(vlqt SHARED)` + `target_sources` one file per line, grouped (core, elements, resources, device/offscreen, Windows-only in the `if(WIN32)` block, including `device_testing.cpp` of NDEV-03). (4) `target_compile_definitions(vlqt PRIVATE $<$<PLATFORM_ID:Windows>:NOMINMAX WIN32_LEAN_AND_MEAN>)`; delete the per-file defines and the include-order comment. (5) Replace "L3" by what it means ("Linux: Qt libraries are deployed by tools/deploy-linux.sh").
- **Decided:** TARGET.md 3.3, S2, P17, F2 → this is the first native step of wave 4, alone, because every cut edits `CMakeLists.txt`; sources one per line, grouped by the folders of TARGET.md 2.3 as the cuts fill them (the list names `test/test_device.cpp`, not `device_testing.cpp`); the Linux check is the local WSL build `tools/build-native.sh` in the gate (P17), the Linux CI build in the verification only runs at the checkpoints (P18); the target `vlqt_testkit` (`VLQT_BUILD_TESTKIT`) is not packed
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/CMakeLists.txt`, `CMakePresets.json`, `offscreen.cpp`, `threadname.h`, `docs/development.md`
- **Depends on:** none (rebase the source list on NDEV-03/04/09 if they land first)
- **Verification:** `tools/build-native.ps1` on win-arm64 and (CI) win-x64; a configure without `-DVLQT_RID` on x64 writes to `runtimes/win-x64/native`; Linux CI build.

### NDEV-09 Move the stand-in "Stride" device helpers out of vlqt.dll
- **Kind:** tests
- **Severity:** should
- **Breaking:** no (only TEST ONLY exports; the C# declarations in `Native.cs` move with them)
- **Where:** `src/VL.Qt.Native/test_offscreen.cpp:1-311`, `src/VL.Qt.Native/CMakeLists.txt:20`
- **Finding:** (K6 for this area.) `test_offscreen.cpp` compiles into the shipped `vlqt.dll` and exports `vlqt_test_d3d11_create_device`, `_release_device`, `_create_shared_texture`, `_read_texture`, `vlqt_test_com_ref_count`, `vlqt_test_com_release`, `vlqt_test_close_handle`, `vlqt_test_process_handle_count` and `vlqt_test_offscreen_state`. All but the last do not need QtNative at all: they create and inspect a second D3D11 device that stands in for Stride. Shipping them gives every user of the package an export that calls `Release` on any pointer it gets. The only coupling is `qtNativeAdapter` (`test_offscreen.cpp:91-105`), which can take a LUID instead. Minor defect on the way: `vlqt_test_d3d11_read_texture` returns the byte count as `int` (`test_offscreen.cpp:203-205`, `233`), which overflows for a 16384×16384 RGBA16F texture.
- **Proposal:** a separate native test library `vlqt_testkit` (`src/VL.Qt.Native/testkit/testkit.cpp`, CMake target behind `option(VLQT_BUILD_TESTKIT ON)`, output next to `vlqt.dll` in the build folder but not packed) with the eight helpers; `create_device` takes `unsigned long long adapterLuid` (0 = default, the SmokeTest gets QtNative's LUID from the existing device-info export). `vlqt_test_offscreen_state` stays in `vlqt.dll` (it reads scene internals) and moves to `offscreen_api.cpp` (NDEV-04). The C# declarations move from `src/VL.Qt/Native.cs` to a `TestKit` class in `src/VL.Qt.SmokeTest`. Alternative: write the helpers in C# in the SmokeTest with Vortice.Direct3D11; rejected by `render-texture.md` ("All unsafe D3D11 code stays in C++"), see Q1.
- **Decided:** F2, F1, P4, S2 → question 1 is answered (a): a separate native `vlqt_testkit`, not packed, source `testkit/testkit.cpp`; it is step 2 of the native break (P4), with the other test exports; `vlqt_test_offscreen_state` is a test export of `vlqt_test.h` defined in a `test/test_*.cpp` file (F1), not in `offscreen_exports.cpp`; the C# declarations go to `src/VL.Qt.TestSupport/` (TARGET.md 2.5), not to a `TestKit` class in the SmokeTest project
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/test_offscreen.cpp` (moved), new `src/VL.Qt.Native/testkit/`, `CMakeLists.txt`, `src/VL.Qt/Native.cs`, `src/VL.Qt.SmokeTest/*` (QtTextureTest*.cs, ClearPinTest.cs, DeviceLostTest.cs, MsaaTest.cs, MultiRendererTest.cs, QtTexturePerfTest.cs), `tools/build-native.ps1` (copy for the SmokeTest), packing (exclude)
- **Depends on:** Q1; NDEV-04 for the place of `vlqt_test_offscreen_state`
- **Verification:** `tools/build.ps1 -Test` (all `qttexture`, `msaa`, `multirenderer`, `clearpin`, device-loss scenarios); the packed `.nupkg` has no `vlqt_testkit.dll` and `vlqt.dll` exports no `vlqt_test_d3d11_*`.

### NDEV-10 Headers state contracts; design notes and history go to docs
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/device.h:1-30`, `device.h:41-49`, `device.h:58-63`, `device.h:101-108`, `src/VL.Qt.Native/render_thread.h:1-11`, `src/VL.Qt.Native/offscreen.cpp:50-72`, `src/VL.Qt.Native/offscreen.h:1-8`, `src/VL.Qt.Native/threadname.h:3-4`, `src/VL.Qt.Native/device.cpp:12-13`, `offscreen.cpp:23-24`, `offscreen.cpp:36`, `docs/decisions/device-sharing.md:28-31`, `docs/decisions/render-texture.md` ("Measured", `VLQT_OFFSCREEN_FOREIGN_FLUSH`)
- **Finding:** `device.h` opens with a 30-line note that repeats `device-sharing.md` and mixes contract with history ("images package 01", "shared-device spike", "#116 phase 5", "docks here later"); `offscreen.cpp:50-72` is a second design note with "decisions c and e" and "phase 5", which only the decision document explains. Comments refer to "spike 5.1" and "T6" (`device.cpp:579`, `offscreen.cpp:113`). `threadname.h:4` lists "vlqt-gui", "vlqt-res-N" but not "vlqt-render-N". The private Qt headers of the area (`qquickwindow_p.h`, `qsgrenderloop_p.h`, `qquickrendercontrol_p.h`) are justified one by one in comments but not listed anywhere a person updating Qt would look. `render-texture.md` describes `VLQT_OFFSCREEN_FOREIGN_FLUSH=1`, which no code reads any more (the flush is unconditional, `offscreen.cpp:587`); the "Open questions" of `device-sharing.md` are answered (adapter move, render thread, `ID3D11Device5`).
- **Proposal:** each header starts with at most five lines: what the module is, which thread, who owns what, a link to the decision document. `device.h` keeps per function the thread and lock rules (they are the contract) and drops the history. The lifetime and threading prose of `offscreen.cpp:50-72` moves into `render-texture.md` ("Lifetime of a scene", next to the thread-affinity table); `offscreen_scene.h` keeps three lines and the link. Replace "spike 5.1"/"T6"/"decisions c and e" by the reason in one clause (e.g. "WARP deadlocks when its context waits for a value signalled later"). Add `vlqt-render-N` to `threadname.h`. Add a section "Private Qt API" to `docs/development.md` (file, header, what for, what to check on a Qt update) and point the include comments there. Remove the `VLQT_OFFSCREEN_FOREIGN_FLUSH` sentence, mark the open questions of `device-sharing.md` as answered with one line each.
- **Decided:** D9, G5, S2 → the private Qt headers of this area are listed in the one place D9 names, the head comment of `elements/elements.h`, not in a new section of `docs/development.md`, and the include comments point there; the design notes live in `docs/design/` (G5, page names per the DOCD reports), not in `docs/decisions/`; `threadname.h` is `app/thread_name.h`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/device.h`, `device.cpp`, `render_thread.h`, `offscreen.h`, `offscreen.cpp` (or split files), `threadname.h`, `docs/decisions/device-sharing.md`, `docs/decisions/render-texture.md`, `docs/development.md`
- **Depends on:** after NDEV-03/04 to avoid editing moved text twice
- **Verification:** review only.

### NDEV-11 Make the frame-lock release in `RenderThread::call` an assertion, not a silent fix
- **Kind:** threading
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/render_thread.h:15`, `render_thread.h:55-56`, `src/VL.Qt.Native/device.cpp:58-82`
- **Finding:** every blocking job first calls `device::releaseFrameLocks()`. Window frame locks are already released at the end of each pump and after a grab (`vlqt.cpp:1127`, `1371`), so this call only matters if `call` runs inside a window frame (between `beforeRendering` and `afterFrameEnd`). In that case it silently drops the lock that protects that window's `endFrame`, and the rest of the frame races with the render thread on the shared context. Whether that ever happens is not known (no log). It is also the only reason `render_thread.h` includes `device.h`.
- **Proposal:** keep the release (it prevents the deadlock) but log once (`qCWarning`, category `vlqt.device`) when a frame lock was actually held, with the window: `bool releaseFrameLocks()` returns whether one was released. Move `ContextLock`, `lockFrames`, `releaseFrameLocks` into `context_lock.h/.cpp` so `render_thread.h` includes only that (see section 4).
- **Decided:** S2 → `device/context_lock.h/.cpp` and `device/render_thread.h`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/render_thread.h`, `device.h`, `device.cpp`, new `context_lock.h/.cpp`, includes in `image_resource.cpp`, `vlqt.cpp`, `offscreen.cpp`, `test_offscreen.cpp`
- **Depends on:** none
- **Verification:** `qttexture` threaded with a window open (`multirenderer`); the warning does not appear.

### NDEV-12 Exceptions in the offscreen exports: own result code, no double unbind
- **Kind:** api
- **Severity:** could
- **Breaking:** no (only the error code of an unexpected exception changes)
- **Where:** `src/VL.Qt.Native/offscreen.cpp:748-759`, `offscreen.cpp:1084-1089`, `src/VL.Qt.Native/protocol.h:600-609`
- **Finding:** `guardedCode` maps any exception to `VLQT_OFFSCREEN_BAD_ARGUMENT` (-7), so the C# side reports "bad argument" for an internal failure; `vlqt.cpp`'s `guarded` returns -1 for the same case, which in the offscreen codes means `NULL_SCENE`. `vlqt_offscreen_bind` unbinds again after a failed `bind`, although `bind` unbinds first and assigns the binding only after every check passed: the second unbind is a second blocking render-thread job that does nothing.
- **Proposal:** add `VLQT_OFFSCREEN_INTERNAL_ERROR = -27` ("exception in QtNative, message in vlqt_last_error") to `protocol.h` and `Protocol.cs`, return it from `guardedCode`; drop the unbind in the failure branch of `vlqt_offscreen_bind` (or keep it only in the `catch` path, where a partially applied binding is possible).
- **Decided:** P4, S2 → the result codes of the offscreen exports change once, in the first pull request of step 3 of the native break (NAPP-02/03, B4), so the guard of `guardedCode` goes into the shared guard in `app/exports.h`; the file is `offscreen/offscreen_exports.cpp` (was `offscreen_api.cpp`); B1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → no `VLQT_OFFSCREEN_INTERNAL_ERROR = -27`: the offscreen exports return the shared `VLQT_ERR_INTERNAL`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/offscreen.cpp` (`offscreen_api.cpp`), `protocol.h`, `src/VL.Qt/Protocol.cs`, C# caller that maps codes to messages
- **Depends on:** none
- **Verification:** `dotnet run --project src/VL.Qt.SmokeTest -c Release -- --drift`; `qttexture` robustness cases (`QtTextureTest.Robust.cs`).

### NDEV-13 Check that render_batch gets the context of the bound device
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/offscreen.cpp:641-666`, `offscreen.cpp:492`, `offscreen.cpp:582`, `offscreen.cpp:612-621`
- **Finding:** the foreign fences are opened on the device passed to `bind`; `render_batch` signals and waits on them with whatever context it gets, unchecked. A host that passes the immediate context of another device (after recreating its device without rebinding) makes D3D11 use a fence of device A on a context of device B: undefined, typically the host's device is removed. The C# side probably rebinds on its own device loss (not verified here), so this is a guard against a wrong caller, not a known bug.
- **Proposal:** keep the foreign device pointer at bind (`ID3D11Device* foreignDevice`, compare only, no reference: the fences keep it alive) and in `renderBatch` compare it once per batch with `context->GetDevice` (one AddRef/Release, no allocation); a scene bound to another device is skipped and the batch returns `VLQT_OFFSCREEN_BAD_ARGUMENT` with "render_batch: scene bound to another device, bind again".
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/offscreen.cpp` (`offscreen_binding.cpp`, `offscreen_render.cpp`)
- **Depends on:** NDEV-04 (optional)
- **Verification:** new robustness case in `QtTextureTest.Robust.cs` (bind on device 1, render_batch with the context of device 2 → error, no device removal).

### NDEV-14 Small naming and consistency fixes
- **Kind:** naming
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/device.cpp:20` vs `offscreen.cpp:33` and `test_offscreen.cpp:7`; `src/VL.Qt.Native/render_thread.cpp:16` vs `offscreen.cpp:120`; `render_thread.cpp:65`; `device.cpp:38`
- **Finding:** the area mixes `Q_OS_WIN` (`device.cpp`) and `_WIN32` (`offscreen.cpp`, `test_offscreen.cpp`) for the same condition. `render_thread.cpp` declares a second logging category object with the name `vlqt.offscreen` (`lcRenderThread`), so its messages look like the scene's. The error text of `setThreadCount` hard-codes "16" next to the constant `maxThreads`. `s_tried` says less than "creation attempted" (disappears with NDEV-02).
- **Proposal:** `Q_OS_WIN` in Qt-side code throughout the area (it is what `device.cpp` and Qt use); category `vlqt.renderthread` for `render_thread.cpp` (document it in `docs/diagnostics.md` next to `vlqt.offscreen`); build the error text with `maxThreads`.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/offscreen.cpp`, `test_offscreen.cpp`, `render_thread.cpp`, `docs/diagnostics.md`
- **Depends on:** none
- **Verification:** build on Windows and Linux.

## 4 Target structure

All in `src/VL.Qt.Native/`, flat as today. Namespaces unchanged: `vlqt::device`, `vlqt::render_thread`, `vlqt::offscreen`. Public is only the C ABI (`vlqt.h`); everything here is internal.

| File | Responsibility | Change |
|---|---|---|
| `device.h` / `device.cpp` | shared D3D11 device: creation with retry, `attach`, `isAttached`, `uploadRhi`, `generation`, listeners, `checkLost`/`rebuild`, `canMove`/`moveTo`, `clampSamples`, adapter LUIDs | keeps; test code and `ContextLock` move out; WRL `ComPtr` |
| `context_lock.h` / `context_lock.cpp` | `ContextLock`, `lockFrames`, `releaseFrameLocks` (`FrameLock`) | **new**, split from `device.*` (NDEV-11) |
| `device_testing.h` / `device_testing.cpp` (Windows) | D3D11On12 test device, `removeForTest` (old `testLost`), `dropWindowRhisOn` | **new**, split from `device.cpp` (NDEV-03) |
| `render_thread.h` / `render_thread.cpp` | `RenderThread` (+ `index()`), pool, `assign`, `drainAll`, `setThreadCount` | keeps; includes `context_lock.h` instead of `device.h` |
| `threadname.h` | thread names | keeps |
| `offscreen.h` | interface for `vlqt.cpp` / `snapshot_resource.cpp`: `release`, `destroy`, `isScene`, `requestFrame`, `testState` | **reduced** (NDEV-04) |
| `offscreen_scene.h` / `offscreen_scene.cpp` | `struct Scene` (old `State`), lifecycle, device listener, `dropRhi`, executor per scene | **new** (split, rename `State` → `Scene`) |
| `offscreen_binding.cpp` (Windows) | bind/unbind, fences, formats, adapter match, software detection | **new** (split) |
| `offscreen_render.cpp` (Windows) | `renderBatch`, one batch for both modes, per-thread batches at fixed addresses | **new** (split, NDEV-01, NDEV-05) |
| `offscreen_input.cpp` | `SceneRenderControl(Private)`, pointing devices, pointer/wheel/key/text/focus injection | **new** (split) |
| `offscreen_api.cpp` | `vlqt_offscreen_*` exports, `guardedCode`, `vlqt_test_offscreen_state`, non-Windows stubs | **new** (split) |
| `offscreen.cpp` | – | **deleted** (split into the five files above) |
| `test_offscreen.cpp` | – | **moved** to `testkit/testkit.cpp` (target `vlqt_testkit`, not packed) (NDEV-09) |
| `CMakeLists.txt` | sources one per line and grouped, RID default per architecture, Windows defines, `VLQT_BUILD_TESTKIT` | changed (NDEV-08) |
| `CMakePresets.json` | `dev-win-arm64`, **new** `dev-win-x64`, Qt path from environment | changed |
| `VL.Qt.Native.csproj` | wrapper for the solution build | keeps |

## 5 Questions for Sebastian

**Q1 Where do the stand-in "Stride" D3D11 helpers live?** (NDEV-09) The SmokeTest needs a second D3D11 device, shared textures, readback, COM reference counts and the process handle count. (a) A separate native `vlqt_testkit.dll`, built with QtNative but not packed: keeps the decision "all unsafe D3D11 code in C++", one more CMake target. (b) C# in the SmokeTest with Vortice.Direct3D11: no native test code at all, one NuGet dependency for the SmokeTest, D3D11 code in two languages. (c) Leave them in `vlqt.dll`: no work, every user ships exports that release arbitrary pointers. Recommendation: (a).

**Q2 Keep the main-thread mode of offscreen scenes (`VLQT_OFFSCREEN_THREAD=0`)?** (NDEV-05) It is the reference for the perf test and a diagnosis switch; it costs a second code path that has already drifted (no foreign flush) and a second `qttexture` run in every `build.ps1 -Test`. (a) Keep it, but through the same batch code (inline executor): one path, the switch stays. (b) Remove it: less code and test time; no fallback if a render-thread problem shows up in the field. Recommendation: (a).

**Q3 What happens when QtNative cannot create its device?** (NDEV-02) (a) Retry once per second; meanwhile windows use their own devices, images do not upload, offscreen scenes return `DEVICE_LOST`, and the log says so once. (b) As today: stay degraded until restart, offscreen scenes fall back to private devices that nobody watches for loss. Recommendation: (a).

**Q4 Is a render-thread pool larger than 1 a supported setting?** (NDEV-01) It is reachable only through `VLQT_OFFSCREEN_THREAD` (no node pin; `vlqt_offscreen_set_threaded` is called only by the perf test) and untested in `build.ps1 -Test`, which is how NDEV-01 went unnoticed. (a) Supported: fix NDEV-01 and add a pool-2 run to the tests. (b) Diagnosis only: cap at 1 and remove `setThreadCount`'s range and the round robin. Recommendation: (a), the measurements in `render-texture.md` show a gain for several heavy scenes.

## 6 For other areas

- **vlqt.cpp (review 16):** `offscreen.h:26-50` declares seven functions implemented in `vlqt.cpp` (`createHandle`, `stateOf`, `quickWindow`, `setError`, `releaseFocus`, `sendTouch`, `cancelTouch`); they belong in headers of their topic (`setError` in an error header shared by all exports, `sendTouch`/`cancelTouch` in `touch.h`, the handle functions in a window-handle header).
- **vlqt.cpp:** `guarded` returns -1 on an exception (`vlqt.cpp:599-609`), `offscreen.cpp`'s `guardedCode` -7; one shared helper with one code (NDEV-12).
- **qtimageitem.cpp:81:** reads `_vlqt_shared_device` and `_vlqt_device_warned` as dynamic properties (see NDEV-06).
- **C ABI / C# (review 09):** `src/VL.Qt/Native.cs` declares the TEST ONLY D3D11 exports in the shipped assembly (NDEV-09).
- **Perf monitor:** `render-texture.md` ("(e) Thread affinity") says `before/afterRendering` of a threaded scene arrive on the render thread concurrently and the PerfMonitor needs atomics or per-thread values; not checked here.
- **Docs:** `docs/decisions/render-texture.md` still describes `VLQT_OFFSCREEN_FOREIGN_FLUSH`, which no code reads (NDEV-10).
