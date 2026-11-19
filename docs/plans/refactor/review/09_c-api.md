# Review 09 – C API and P/Invoke

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/vlqt.h`, `src/VL.Qt/Native.cs`. Searched with short excerpts: export definitions in `vlqt.cpp`, `offscreen.cpp` and `test_offscreen.cpp` (boundary helpers `guarded`, `setError`, `g_lastError`, `emitLog`, `DllMain`; the bodies of `vlqt_app_process_events_for`, `vlqt_window_apply`, `vlqt_font_add`, `vlqt_shader_reflect`, `vlqt_res_read_audio`, `vlqt_image_write`, `vlqt_offscreen_is_bound`, `vlqt_offscreen_render_batch`, `vlqt_test_perf_stats_text`), every `DllImport` under `src/`, the callers of `Native` in `src/VL.Qt`, `src/VL.Qt.Stride`, `src/VL.Qt.Audio` and the tests, `QtLog.cs` (callback), `ProtocolDrift.cs` (scope), `protocol.h` (offscreen codes). VL.StandardLibs: `VL.Core.Skia/Egl/NativeEgl.cs`, `VL.Core.Skia/Egl/EglResource.cs`, `VL.Video/src/MF/SourceReader.cs` (excerpts). Not read: the rest of `vlqt.cpp` (2,452 lines), the other QtNative sources, the SmokeTest scenarios beyond their declarations.

The prompt speaks of about 210 exports. The real count is **101**: 100 declared in `vlqt.h` and one exported without a declaration (`vlqt_test_perf_stats_text`, CAPI-05). `Native.cs` declares 95 of them (plus one alias entry point), the SmokeTest declares the other 5 itself.

## 1 Summary

The ABI itself is sound: pure C, cdecl, only `int`/`float`/`double`/fixed-width integers, `void*` handles, `u64` resource ids and blittable structs; no `bool`, no `long`, no C++ types cross. Every declaration in `Native.cs` matches `vlqt.h` in types, order and struct layout (checked one by one, section 2). Buffers are always caller-owned, strings in are copied, nothing is freed across the boundary. The per-frame calls are allocation-free on the C# side (spans, reused arrays).

What is wrong or hard to follow:

1. **The last-error string is a plain global** written from any thread and never cleared (CAPI-01, must).
2. **Return codes follow four schemes that overlap**: an exception returns -1, the same value as "null window"; "buffer too small" is signalled in four different ways (CAPI-02).
3. **The names do not say what a function works on**: 18 `vlqt_window_*` functions also take offscreen scenes, resource kinds hide under `vlqt_res_*`, globals have no group, and every handle is an untyped `void*`/`IntPtr` (CAPI-03).
4. **A third of the API is test code** (32 of 101 exports, 26 declarations in the library's `Native.cs`), mixed into the product header (CAPI-04).
5. **Nothing checks `vlqt.h` against `Native.cs`**: the drift check covers only `protocol.h`, one struct has a `static_assert` (CAPI-06).

## 2 What works and must survive

- **Blittable-only signatures.** No `bool` (all flags are `int`), no `long` (always `long long` ↔ `long`), strings only as `const char*` in (`LPUTF8Str`) or caller buffers out. This keeps the ABI identical on win-x64, win-arm64 and Linux. Keep it as a rule in the header (CAPI-08).
- **Span wrappers over `in`/`ref` first-element externs** (`Native.cs:342`, `369`, `402`, `483`, `498`, `520`, `536`, `589`, `619–623`): no pinning objects, no copies, an empty span passes NULL with length 0. The raw externs behind them are `private`. Every new buffer function follows this pattern.
- **Hot paths, allocation-free today:** `vlqt_app_deliver_input`, `vlqt_app_process_events_for` (array reused, `QtApp.cs:278`, `522`), `vlqt_window_apply` / `Native.Apply`, `vlqt_window_poll` / `Native.Poll`, `vlqt_res_poll`, `vlqt_window_get_scene_metrics`, `vlqt_window_report_host_frame`, `vlqt_window_change_count`, `vlqt_offscreen_render_batch` (span), and on VL.Audio's audio thread `vlqt_res_read_audio` and `vlqt_now_ns` (`IAudioTapReader.cs:29–35`). A rename must keep their shapes.
- **Log callback lifetime.** The delegate is rooted in a static field (`QtLog.cs:30`), no exception escapes it (`QtLog.cs:79–90`), and QtNative cuts the pointer at unload (`vlqt.cpp:2436–2452`) because the CLR is gone when Qt's static destructors log. `g_log` is atomic because it is read on render threads (`vlqt.cpp:86–88`).
- **No `SafeHandle` for windows and scenes.** `vlqt_window_destroy` must run on the Qt main thread at a deterministic point (`WindowRenderer.cs:281`); a `SafeHandle` would call it from the finalizer thread when a renderer is leaked, and adds a ref-count round trip to every per-frame call. VL.StandardLibs uses `SafeHandle` where release has no thread affinity (`VL.Core.Skia/Egl/EglResource.cs:7`, EGL objects); Qt objects do have one. Typed handle structs (CAPI-03) give the type safety without the finalizer.
- **`DllImport`, not `LibraryImport`.** VL.StandardLibs has 13 files with `DllImport` and none with `LibraryImport` (e.g. `VL.Core.Skia/Egl/NativeEgl.cs:146–152`). With blittable signatures the runtime stub costs nothing extra; switching gains no performance and departs from the house style.
- **Library resolution.** `Native`'s static constructor loads `vlqt` by full path and registers a resolver for the VL.Qt assembly (`Native.cs:86–103`); the three folder layouts are documented in `docs/decisions/qtnative-deployment.md`.
- **Ownership, as it is today** (keep, and write down once, CAPI-08):

| Crosses | Owner | Lifetime |
|---|---|---|
| window / scene handle (`vlqt_window_create_at`, `vlqt_offscreen_create`) | caller | until `vlqt_window_destroy`, main thread |
| resource id (`vlqt_res_create`) | caller holds one host reference | until `vlqt_res_release`; freed at the end of a pump when no element holds it |
| font handle (`vlqt_font_add`) | caller, one reference per successful add | until `vlqt_font_release` |
| `const char*` in (title, paths, shader source, text) | caller | copied before the call returns |
| caller buffers (`out`, `cap`) | caller | QtNative writes at most `cap` bytes |
| `vlqt_last_error()` | QtNative | until the next error (see CAPI-01) |
| `vlqt_apply_error.reason` | QtNative | until the next apply of that window (`vlqt.h:74`) |
| foreign `ID3D11Device*`, NT handle (`vlqt_offscreen_bind`) | caller | QtNative keeps no device reference beyond its fences, does not close the handle (`vlqt.h:516–525`) |
| log callback | caller | must stay valid for the process; QtNative clears it at unload |

- **Declaration check, Native.cs ↔ vlqt.h.** All 95 entry points match in return type, parameter types and order; `out`/`in`/`ref` map to the pointer parameters; `uint`/`ushort`/`ulong` follow `unsigned`/`unsigned short`/`unsigned long long`; nullable `string?` exactly where the header allows NULL (`vlqt_window_set_icon`, `vlqt_set_pipeline_cache`). Struct layouts match field by field: `vlqt_perf`, `vlqt_perf_sample`, `vlqt_perf_stats`, `vlqt_apply_error`, `vlqt_host_frame` (padding before `alloc_bytes` at offset 36 → 40 on both sides), `vlqt_host_stats`, `vlqt_res_info`, `vlqt_pixel_desc`, `vlqt_audio_tap_state` (96 bytes, `audio_tap.cpp:32`), `vlqt_scene_metrics`. `CallingConvention.Cdecl` has no effect on x64/arm64 (the only supported architectures, `Native.cs:110–115`) but documents the header's promise; keep or drop consistently. Mismatches found are outside `Native.cs`: CAPI-05.

## 3 Findings

### CAPI-01 Make the last-error string per thread and set it on every failure
- **Kind:** threading
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/vlqt.cpp:91`, `94–95`, `600–608`, `1186–1189`, `2357`; `src/VL.Qt.Native/offscreen.cpp:1124–1128`; `src/VL.Qt.Native/vlqt.h:160`, `315`, `356`, `366`; `src/VL.Qt/Native.cs:170`
- **Finding:** `g_lastError` is a global `std::string`, and `vlqt_last_error` returns its `c_str()`. Three exports are documented for any thread: `vlqt_shader_reflect` ("any thread", `vlqt.h:315`), `vlqt_res_read_audio` (VL.Audio's audio thread, `vlqt.h:356`), `vlqt_image_write` (`vlqt.h:366`). All three run inside `guarded`, which on an exception assigns `g_lastError` (`vlqt.cpp:603–606`). `vlqt_font_add` assigns it on purpose when called from the wrong thread (`vlqt.cpp:2357–2359`). An assignment on another thread while the main thread reads `c_str()` is a data race on `std::string`: undefined behaviour, in practice a torn or freed buffer passed to `Marshal.PtrToStringUTF8`. Second, nothing ever clears the string (no `clear()` or empty `setError` anywhere in QtNative), and not every negative return sets it: `vlqt_offscreen_is_bound` returns `VLQT_OFFSCREEN_NULL_SCENE` without a message (`offscreen.cpp:1126–1128`). A caller that logs `Native.LastError()` after such a return (9 places in `src/VL.Qt`, e.g. `OffscreenRenderer.cs:44`) then prints the message of an older, unrelated failure. Suspicion, not confirmed: `offscreen.cpp:767` (`setError("offscreen " + what)`) may also run on a Qt render thread when `vlqt_offscreen_set_threaded` is > 0; reading the callers of that helper confirms or clears it. The per-thread fix below removes the race either way.
- **Proposal:** `thread_local std::string g_lastError;` in `vlqt.cpp`. Contract in `vlqt.h`: "`vlqt_last_error` returns the message of the last failing call **on the calling thread**; valid until the next failing call on that thread; not cleared by a successful call." Add one helper `int fail(int code, std::string message)` that sets the message and returns the code, and use it for every negative return of an export (replace the bare `return VLQT_OFFSCREEN_NULL_SCENE;` and similar). C# keeps calling `LastError()` right after the failing call on the same thread, which all callers already do.
- **Decided:** P22 → CAPI-01 = NAPP-02, one issue; B4 → (a), per-thread last error as proposed; P4 step 3 → in the first pull request of the break (return codes and the per-thread error text, not mechanical); question 0.2 → wave 1 confirms or drops the render-thread suspicion (`offscreen.cpp:767`) before the issue is cut; TARGET 2.3 → `g_lastError` lives in `app/last_error.*`, since `vlqt.cpp` dissolves (NAPP-01)
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, `src/VL.Qt.Native/offscreen.cpp`, `src/VL.Qt.Native/test_offscreen.cpp`, `src/VL.Qt.Native/vlqt.h`
- **Depends on:** none (do before or together with CAPI-02)
- **Verification:** SmokeTest `resources`, `font`, `qttexture` (error messages unchanged); `resource-stress` under TSan on Linux (#365 target) shows no race on `g_lastError`; a new SmokeTest check: call `vlqt_font_add` from a worker thread while the main thread fails an apply, both messages correct.

### CAPI-02 One return-code table for the whole API
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: C# code that compares raw codes (`Protocol.Offscreen`, `ResourceClient`, apply errors) changes with it; patches see nothing.
- **Where:** `src/VL.Qt.Native/vlqt.h:1`, `186`, `231`, `239`, `255–258`, `263–264`, `270–271`, `313`, `372–374`, `418`, `421`, `475`, `488–490`; `src/VL.Qt.Native/protocol.h:600–609`; `src/VL.Qt.Native/vlqt.cpp:600–609`
- **Finding:** The header says "Return 0 = ok unless noted" (`vlqt.h:1`) and then notes something different almost everywhere:
  - Null handle: -1 in most window functions (`vlqt.h:186`), -9 in `vlqt_window_change_count` (`vlqt.h:475`), `VLQT_OFFSCREEN_NULL_SCENE` = -1 in offscreen functions; window-only functions called on a scene return `VLQT_OFFSCREEN_*` codes (`vlqt.h:507–509`), so one function mixes two code sets.
  - An exception in any `guarded` export returns -1 (`vlqt.cpp:609`): for a window function that is the "null window" code, for a resource function "internal error" (`vlqt.h:270`), for `vlqt_image_write` "bad arguments" (`vlqt.h:372`).
  - -2 means "bad length" for apply (`vlqt.h:257`), "no pump yet" for perf (`vlqt.h:231`), "no QRhi" for `vlqt_test_device_info` (`vlqt.h:421`), "unknown file format" for `vlqt_image_write` (`vlqt.h:373`), "unknown type or touch state" for the test input.
  - "Buffer too small" comes in four forms: `-required` (`vlqt_window_poll`, `vlqt_res_poll`, `vlqt_res_read_pixels`, `vlqt_res_read_preview`, `vlqt_test_d3d11_read_texture`), `-(length + 1)` (`vlqt_test_perf_text`, `vlqt.h:418`), a positive required length (`vlqt_shader_reflect`, `vlqt.h:313`), silent truncation (`vlqt_font_add` families, `vlqt.h:488`). `-required` shares the number line with the error codes; `vlqt_window_poll` avoids a clash only by promising "always <= -8" (`vlqt.h:264`).
  - Error text comes from `vlqt_last_error` everywhere except `vlqt_image_write`, which has its own `err`/`errCap` buffer (`vlqt.h:374`, wrapper `Native.cs:483–491`).
  A reader who sees `rc == -2` cannot know what happened without the comment of that one function.
- **Proposal:** One enum in `vlqt.h`, used by every export; `protocol.h`'s `vlqt_offscreen_result` folds into it:
  ```c
  typedef enum vlqt_result {
      VLQT_OK = 0,
      VLQT_ERR_NULL_HANDLE = -1,     /* null or wrong kind of handle (window/scene) */
      /* -2 ... -8: apply errors, only from vlqt_window_apply / vlqt_apply_error.rc (unchanged) */
      VLQT_ERR_BAD_ARGUMENT = -7,    /* already shared by res, offscreen, apply (bad value) */
      VLQT_ERR_UNKNOWN_ID = -9,      /* resource id or font handle unknown, stale or released */
      VLQT_ERR_NO_DATA = -10,        /* no pump yet, no snapshot yet, no QRhi yet */
      VLQT_ERR_WRONG_THREAD = -11,
      VLQT_ERR_BUFFER_TOO_SMALL = -12, /* *required holds the size */
      VLQT_ERR_UNSUPPORTED = -13,    /* file format, platform, not on an offscreen scene */
      VLQT_ERR_IO = -14,             /* file could not be read or written */
      /* -20 ... -25: offscreen bind/render (unchanged values of vlqt_offscreen_result) */
      VLQT_ERR_INTERNAL = -100       /* exception or Qt failure; vlqt_last_error has the text */
  } vlqt_result;
  ```
  `guarded` returns `VLQT_ERR_INTERNAL`. Every function with a caller buffer gets the signature `(…, buf, cap, int* required)`: returns bytes written, or `VLQT_ERR_BUFFER_TOO_SMALL` with `*required` set; `vlqt_font_add` and `vlqt_shader_reflect` included. `vlqt_image_write` drops `err`/`errCap` and uses `vlqt_last_error` (thread-local after CAPI-01). `vlqt_window_change_count` returns `VLQT_ERR_NULL_HANDLE`. Mirror the enum in `Protocol.cs` (or `Native`) as `Native.Result` so `--drift` covers it. The C# wrappers (`Poll`, `ResPoll`, `ResReadSnapshot`, `ResReadPreview`, `FontAdd`, `ImageWrite`, the reflection call) adapt; their callers keep their shape.
- **Decided:** P22 → CAPI-02 = NAPP-03, one issue; P4 step 3 → first pull request of the break (not mechanical); B4 → `vlqt_image_write` drops its `err`/`errCap` buffer as proposed; A5 → `vlqt_window_apply` is `vlqt_scene_apply`; B1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → one table `vlqt_result`, the apply codes -2 … -8 named after PROTO-07, exceptions everywhere `VLQT_ERR_INTERNAL`
- **Effort:** L, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.h`, `protocol.h`, `vlqt.cpp`, `offscreen.cpp`, `test_offscreen.cpp`, the QtNative files that return codes (resources, image, snapshot, fonts, imagewriter, shader reflect); `src/VL.Qt/Native.cs`, `Protocol.cs`, `SceneCore.cs`, `Images/ResourceClient.cs`, `Images/ImageFileWriter.cs`, `Nodes/LoadFontNode.cs`, `Shaders/ShaderReflection.cs`, `OffscreenRenderer.cs`, `src/VL.Qt.Stride/SharedTextureScene.cs`; SmokeTest checks on raw codes (`QtTextureTest*.cs`, `ResourceTest.cs`, `ImageResourceTest.cs`, `FontTest.cs`, input tests)
- **Depends on:** CAPI-01; best done in the same pass as CAPI-03
- **Verification:** `tools/build.ps1 -Test` (all scenarios), `--drift` with the enum, unit tests `ResourceClientTests`, `ShaderReflectNativeTests`

### CAPI-03 Group and name the exports by what they act on, with typed handles
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: C ABI and the internal `Native` members are renamed; no public node or pin changes.
- **Where:** `src/VL.Qt.Native/vlqt.h:148–615` (all declarations), especially `502–510` (the list of window functions that take scenes); `src/VL.Qt/Native.cs:172–623`
- **Finding:** The prefixes do not say what a function acts on:
  - The same `void*` is a window or an offscreen scene; 18 `vlqt_window_*` functions work on both (`vlqt.h:502–506`), 7 are no-ops or errors on a scene (`vlqt.h:506–509`). A reader of `SharedTextureScene.cs:92` (`Native.vlqt_window_set_debug(scene, …)`) has to find the list in a comment to know this is legal.
  - Resource kinds are hidden: `vlqt_res_load_image`, `vlqt_res_load_shader`, `vlqt_res_snapshot`, `vlqt_res_audio_tap_init`, `vlqt_res_read_audio`. `vlqt_res_read_pixels` reads only snapshots (`vlqt.h:341–344`; C# already calls it `ResReadSnapshot`, `Native.cs:520`), while `vlqt_test_res_read_pixels` reads only images.
  - Globals without a group: `vlqt_set_pipeline_cache`, `vlqt_driver_advance` (advances Qt's animation driver, the clock of QML animations), `vlqt_now_ns`.
  - Getters vary: `vlqt_window_samples`, `vlqt_window_get_mode`, `vlqt_window_change_count`, `vlqt_window_is_visible`. `vlqt_offscreen_set_threaded` takes a thread count, not a flag.
  - Two short forms exist only for tests: `vlqt_window_create` (no library caller; the library uses `vlqt_window_create_at`) and `vlqt_app_process_events` (no library caller; `QtApp.cs:394` uses `_for`).
  - Handles are `void*`/`IntPtr` everywhere; `vlqt_offscreen_bind(IntPtr scene, IntPtr device, IntPtr ntHandle, …)` (`Native.cs:329`) compiles with any two swapped.
- **Proposal:** Opaque handle type `typedef struct vlqt_scene vlqt_scene;` in C; in C# `internal readonly record struct SceneHandle(IntPtr Value)` (blittable, passes as a pointer on x64 and arm64) used in every scene/window signature. Groups and names (unchanged names omitted):

  | Group | Old → new |
  |---|---|
  | `vlqt_app_*` (process, main thread) | `vlqt_driver_advance` → `vlqt_app_advance_animations`; `vlqt_set_pipeline_cache` → `vlqt_app_set_pipeline_cache`; `vlqt_app_process_events` deleted (tests call `_for(NULL, 0)`) |
  | `vlqt_scene_*` (window or offscreen scene) | `vlqt_window_destroy/apply/get_apply_error/poll/set_debug/get_debug/set_overlay/get_overlay/get_perf/get_perf_sample/get_perf_stats/report_host_frame/get_host_frame/get_host_stats` → `vlqt_scene_…` (same suffix); `vlqt_window_get_scene_metrics` → `vlqt_scene_get_metrics`; `vlqt_window_change_count` → `vlqt_scene_get_change_count` |
  | `vlqt_window_*` (window only) | `vlqt_window_create_at` → `vlqt_window_create` (old short form deleted); `vlqt_window_samples` → `vlqt_window_get_samples`; show, hide, set_title, resize, set_icon, set_mode, get_mode, take_close_request, is_visible, set_samples stay |
  | `vlqt_offscreen_*` (scene only) | `vlqt_offscreen_set_threaded` → `vlqt_offscreen_set_render_threads`; the rest stay |
  | `vlqt_res_*` (lifecycle of any resource) | create, release, set_budget, poll, wait, list, shutdown stay |
  | `vlqt_image_*` | `vlqt_res_load_image` → `vlqt_image_load`; `vlqt_res_set_pixels` → `vlqt_image_set_pixels`; `vlqt_res_read_preview` → `vlqt_image_read_preview`; `vlqt_image_write` → `vlqt_image_write_file` |
  | `vlqt_shader_*` | `vlqt_res_load_shader` → `vlqt_shader_load`; `vlqt_res_load_shader_source` → `vlqt_shader_load_source`; `vlqt_shader_reflect` stays |
  | `vlqt_snapshot_*` | `vlqt_res_snapshot` → `vlqt_snapshot_start`; `vlqt_res_snapshot_fit` → `vlqt_snapshot_start_fit`; `vlqt_res_read_pixels` → `vlqt_snapshot_read` |
  | `vlqt_audio_tap_*` | `vlqt_res_audio_tap_init` → `vlqt_audio_tap_init`; `vlqt_res_read_audio` → `vlqt_audio_tap_read`; `vlqt_now_ns` → `vlqt_audio_tap_now_ns` |
  | `vlqt_font_*` | stay |
  | `vlqt_test_*` | see CAPI-04 |

  Alternative: keep kind functions under `vlqt_res_<kind>_<verb>` (`vlqt_res_image_load`). Recommended is the short form above: the id says it is a resource, the prefix says which kind, and it matches the C# folders `Images/`, `Shaders/`. `vlqt.h` gets one section per group in this order. The C# externs take the new C names (one name across both layers stays greppable); the span wrappers take the PascalCase of the group: `SceneApply`, `ScenePoll`, `SnapshotRead`, `ImageReadPreview`, `ImageSetPixels`, `ImageWriteFile`, `AudioTapRead`, `AudioTapDrop`, `FontAdd`, `ShaderReflect`, `OffscreenRenderBatch`, `OffscreenSendText`, `ResPoll`, `ResList`.
- **Decided:** A5 → (a), both layers renamed, the C names are the C# names; P4 step 3 → the second pull request of the break, after step 2 (CAPI-04 first, as proposed); O2 → the opaque type is `vlqt_scene`, in C# `SceneHandle`; glossary 1.6 → `vlqt_host_frame` is `vlqt_host_pump`, so `vlqt_scene_report_host_pump` and `vlqt_scene_get_host_pump` (the table still writes `host_frame`); E7 → `vlqt_offscreen_set_render_threads` takes the pool size, 0 = main-thread mode, and may be called again at runtime (the pool only grows, P7); P4 → E10 (upload mode per image) also changes the image functions, in this break; P22 → NAPP-07, NDEV-04 and CAPI-03 stay three issues that share the names of O2; B3 of [DECISION-ROUND.md](../DECISION-ROUND.md) → `vlqt_window_create` with the position, `_create_at` goes; B4 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the version export is `vlqt_app_get_abi_version`; B5 of [DECISION-ROUND.md](../DECISION-ROUND.md) → `vlqt_app_init` takes the log callback as a function pointer (CAPI-09) in the same names pull request
- **Effort:** L, mechanical: yes (rename by table), the handle type no
- **Touches:** `src/VL.Qt.Native/vlqt.h`, `vlqt.cpp`, `offscreen.cpp`, `test_offscreen.cpp`, `debug.h`/`events.h`/`protocol.h` comments that name exports; `src/VL.Qt/Native.cs` and its 14 users (`Images/ImageFileWriter.cs`, `Images/ResourceClient.cs`, `Initialization.cs`, `Nodes/ElementTooltipPreview.cs`, `Nodes/LoadFontNode.cs`, `OffscreenRenderer.cs`, `PerfStats.cs`, `Protocol.cs`, `QtApp.cs`, `QtLog.cs`, `Renderer.cs`, `SceneCore.cs`, `Shaders/ShaderReflection.cs`, `WindowRenderer.cs`); `src/VL.Qt.Stride/StrideInterop.cs`, `SharedTextureScene.cs`; `src/VL.Qt.Audio/IAudioTapReader.cs`, `AudioTapPlayer.cs`; `src/VL.Qt.Audio.Tests/SimulatedTap.cs`; `src/VL.Qt.Probe/Program.cs`; `src/VL.Qt.Tests/*` (4 files); `src/VL.Qt.SmokeTest/*`; `docs/` pages that name exports (architecture, decisions/render-texture.md, diagnostics.md)
- **Depends on:** CAPI-04 (test exports move first, fewer renames), CAPI-06 (check catches a missed rename)
- **Verification:** `tools/build.ps1 -Test`; the extended `--drift` (CAPI-06) reports no unmatched entry point; `tools/vvvvc.ps1` on the help patches of QtTexture and Audio

### CAPI-04 Move the test exports into their own header and out of the library's Native.cs
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/vlqt.h:228`, `235–237`, `245–246`, `325–327`, `376–384`, `386–470`, `493–494`; `src/VL.Qt/Native.cs:216`, `224–226`, `235–237`, `282–315`, `375–403`, `473–475`, `493–511`, `602–604`; SmokeTest declarations `EffectChainTest.cs:26`, `EffectLayerTest.cs:23`, `EffectPassesTest.cs:37`, `PerfOverlayTest.cs:18–33`, `MeasureTest.cs:444`, `SpinBoxTest.cs:14`, `ValueInputsTest.cs:18`
- **Finding:** 28 `vlqt_test_*` exports plus `vlqt_perf_eval`, `vlqt_perf_host_eval` ("Test:"), `vlqt_window_grab` ("/* test */") and the undeclared `vlqt_test_perf_stats_text`: 32 of 101 exports are test hooks, interleaved with product functions in `vlqt.h` (e.g. `vlqt_test_res_read_pixels` between shader and snapshot functions, `vlqt.h:325`). `Native.cs` in the shipped library declares 26 of them plus two wrappers (`ResReadPixels`, `TestReadTexture`); no library code calls any of them (search over `src/VL.Qt`, `VL.Qt.Stride`, `VL.Qt.Audio`). The SmokeTest declares 6 more itself, `vlqt_test_perf_text` four times and `vlqt_test_send_key_text` twice, deliberately "so VL.Qt's Native.cs stays untouched" (`PerfOverlayTest.cs:17`). Those declarations resolve "vlqt" without the resolver `Native` registers, which covers only the VL.Qt assembly (`Native.cs:101`); they work because the module is already loaded when they run. This is K6 for this area.
- **Proposal:** `src/VL.Qt.Native/vlqt_test.h` holds every test export, all with the prefix `vlqt_test_`: `vlqt_perf_eval` → `vlqt_test_perf_eval`, `vlqt_perf_host_eval` → `vlqt_test_perf_host_eval`, `vlqt_window_grab` → `vlqt_test_window_grab`. `vlqt.h` no longer mentions tests. On the C# side one file `src/VL.Qt.TestSupport/NativeTest.cs` (`internal static class NativeTest`, all test externs and their span wrappers), linked into `VL.Qt.SmokeTest` and `VL.Qt.Tests` with `<Compile Include="..\VL.Qt.TestSupport\NativeTest.cs" Link="NativeTest.cs" />`; its static constructor calls `Native.EnsureLoaded()` and registers a resolver for its own assembly that returns `Native`'s library handle (expose it as `internal static IntPtr Handle`). The 13 per-file SmokeTest declarations go. Unit tests that use `vlqt_perf_eval` (`PerfStatsTests.cs`) switch to `NativeTest`.
- **Decided:** F1 → (a), test exports stay in every build, in `vlqt_test.h` and `test/test_*.cpp`; P22 → CAPI-04 = NAPP-09 = TEST-08, one issue, together with PROTO-06; P4 step 2 → before the renames; F2 → the stand-in D3D11 helpers among them (`vlqt_test_d3d11_*`) go to `vlqt_testkit` with NDEV-09, not into `vlqt_test.h` of `vlqt.dll`; TARGET 2.5 → `src/VL.Qt.TestSupport/NativeTest.cs` as proposed
- **Effort:** M, mechanical: mostly
- **Touches:** `src/VL.Qt.Native/vlqt.h`, new `src/VL.Qt.Native/vlqt_test.h`, `vlqt.cpp`, `test_offscreen.cpp`, `CMakeLists.txt` (install the header if headers are installed); `src/VL.Qt/Native.cs`; new `src/VL.Qt.TestSupport/NativeTest.cs`; `src/VL.Qt.SmokeTest/VL.Qt.SmokeTest.csproj`, `src/VL.Qt.Tests/VL.Qt.Tests.csproj`; SmokeTest files with own declarations (list above) and every SmokeTest file calling `Native.vlqt_test_*`, `Native.vlqt_window_grab`, `Native.ResReadPixels`, `Native.TestReadTexture`; `src/VL.Qt.Tests/PerfStatsTests.cs`
- **Depends on:** none; question Q1
- **Verification:** `tools/build.ps1 -Test` (all scenarios), `dotnet test src/VL.Qt.Tests`; a search for `vlqt_test_` in `src/VL.Qt` returns nothing

### CAPI-05 Declare or delete the undeclared export vlqt_test_perf_stats_text
- **Kind:** dead-code
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/vlqt.cpp:2097–2110`; `src/VL.Qt.SmokeTest/PerfOverlayTest.cs:22–28`
- **Finding:** `vlqt_test_perf_stats_text` is exported but not declared in `vlqt.h`; the only caller declares it locally. It returns the full length (not `-required`) and writes a truncated NUL-terminated copy, a fifth buffer convention. `vlqt_test_perf_text(w, page, …)` returns the text of an F5 page and looks like its successor; whether page 0 (Summary) holds the same text as `statsText()` was not checked.
- **Proposal:** If `PerfOverlayTest` can use `vlqt_test_perf_text` with the page that shows `statsText()`, delete `vlqt_test_perf_stats_text` and its declaration. Otherwise declare it in `vlqt_test.h` (CAPI-04) with the common buffer convention (CAPI-02).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, `src/VL.Qt.Native/perf.cpp` (if `statsText` becomes unused), `src/VL.Qt.SmokeTest/PerfOverlayTest.cs`
- **Depends on:** CAPI-04
- **Verification:** SmokeTest `perf-overlay` scenario

### CAPI-06 Check vlqt.h against Native.cs mechanically
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.SmokeTest/ProtocolDrift.cs:10`; `src/VL.Qt.Native/audio_tap.cpp:32`; `src/VL.Qt.Native/vlqt.h:32`, `43–46`, `88–90`, `104–105`
- **Finding:** The drift check compares only `protocol.h` with `Protocol.cs` (`ProtocolDrift.cs:10`). For the C API the only guard is one `static_assert` on `vlqt_audio_tap_state`. Fields have been appended to `vlqt_perf`, `vlqt_perf_sample`, `vlqt_host_frame` and `vlqt_host_stats` over time (`vlqt.h:32`, `43`, `88`, `104`); a field added on one side only shifts every later field and fails silently with plausible numbers. A missing or misspelled export fails only when the call runs (`EntryPointNotFoundException`), and a parameter added on one side is not detected at all. Today everything matches (section 2); the rename of CAPI-03 touches all of it.
- **Proposal:** (a) In `vlqt.cpp`, one block of `static_assert(sizeof(…) == n)` and `offsetof` for the last field of every API struct, with the numbers also written in the C# test. (b) Unit test `src/VL.Qt.Tests/NativeLayoutTests.cs`: `Unsafe.SizeOf<Native.vlqt_…>()` and `Marshal.OffsetOf` for the same fields. (c) Extend `--drift` to parse the `VLQT_API` declarations of `vlqt.h` and `vlqt_test.h` (name and parameter count) and compare them with the `[DllImport]` methods found by reflection in `VL.Qt` and `NativeTest` (entry point name, parameter count): every C# entry point exists in a header, every non-test header export is declared in `Native`. Recommended: all three; (c) is the one that protects CAPI-03.
- **Decided:** P4 step 1 → one of the three checks that come first in wave 3, before the renames (it catches a missed rename of CAPI-03); C1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the custom runner's options go: `--drift` and the check of `vlqt.h` against the `DllImport`s (CAPI-06 (c)) are unit tests, `--only`/`--list` are NUnit filters, `--replay`, `--tearing` and `--update-refs` are explicit NUnit tests or test parameters; the docs follow in wave 6
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.cpp`, `src/VL.Qt.SmokeTest/ProtocolDrift.cs` (or a new `ApiDrift.cs` next to it), `src/VL.Qt.SmokeTest/Program.cs` (`--drift`), new `src/VL.Qt.Tests/NativeLayoutTests.cs`, `docs/development.md` (what `--drift` checks), `AGENTS.md` (Tests section)
- **Depends on:** none
- **Verification:** `dotnet run --project src/VL.Qt.SmokeTest -c Release -- --drift` passes; deliberately removing a field in `Native.cs` makes both the unit test and the drift fail

### CAPI-07 Split Native.cs by responsibility and group
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Native.cs:81–168` (loading, PATH, MAX_PATH diagnostics), `172–623` (externs, structs, wrappers in historical order)
- **Finding:** One 625-line file holds three things: finding and loading QtNative (static constructor, `FindNativeDir`, process `PATH` prepend), a Windows MAX_PATH diagnostic for the Qt tree (`LongestPath`, `PathsTooLong`, `PathTooLongHint`, `maxPath`; used by `QtApp.cs` and `SceneCore.cs`), and the P/Invoke surface. The declarations follow the order in which features arrived, not `vlqt.h`: the resource structs sit between test functions (`Native.cs:405`), `vlqt_set_pipeline_cache` sits among shader functions (`Native.cs:470`), `vlqt_scene_metrics` between window functions (`Native.cs:254`), and the public wrappers `ResPoll`, `ResList`, `Apply`, `Poll` are at the very end (`Native.cs:619–623`), far from their externs. A reader looking for "how does C# call apply" finds the extern at line 274 and its wrapper at line 622. Two raw externs are public although every other buffer extern is private behind a span wrapper: `vlqt_shader_reflect` with `ref byte buf` (`Native.cs:464`) and `vlqt_app_process_events_for` with array plus separate count (`Native.cs:178`). `vlqt_apply_error` carries the summary meant for `vlqt_host_frame` (`Native.cs:43–44`, two stacked `<summary>` tags), and `vlqt_host_frame` has none.
- **Proposal:** Folder `src/VL.Qt/Interop/`, namespace `VL.Qt.Interop`:
  - `QtNativeLibrary.cs`: `internal static class QtNativeLibrary` with `Directory`, `Handle`, `EnsureLoaded()`, the resolver and the PATH prepend (from `Native.cs:81–129`, `167–168`).
  - `QtTreePathCheck.cs`: the MAX_PATH diagnostic (`Native.cs:131–165`), constant `maxPath` → `windowsMaxPath`.
  - `Native.cs` becomes `internal static partial class Native` with `const string lib`, `LastError()` and the `vlqt_result` codes; one partial file per group of CAPI-03, each with its structs (nested), externs and wrappers in header order: `Native.App.cs`, `Native.Scene.cs` (apply, poll, metrics, debug, overlay, change count, `vlqt_apply_error`), `Native.Perf.cs` (5 perf/host structs and their getters), `Native.Window.cs`, `Native.Offscreen.cs`, `Native.Resources.cs` (`vlqt_res_info`, lifecycle), `Native.Images.cs` (`vlqt_pixel_desc`, image functions, file writer), `Native.Shaders.cs`, `Native.Snapshots.cs`, `Native.AudioTap.cs` (`vlqt_audio_tap_state`), `Native.Fonts.cs`.
  - Make `vlqt_shader_reflect` and `vlqt_app_process_events_for` private behind `ShaderReflect(string source, int stage, Span<byte> buffer, out int required)` and `ProcessEventsFor(ReadOnlySpan<IntPtr> windows)`.
  - Fix the stacked summary.
  Accessibility: `Native` stays `internal`; its members may stay `public` (equivalent inside an internal class). Across assemblies, through `InternalsVisibleTo` (`VL.Qt.csproj:35–40`), only these are used: by `VL.Qt.Stride` the offscreen functions and `vlqt_window_set_debug` (`SharedTextureScene.cs:92`, `236`; `StrideInterop.cs:14`), by `VL.Qt.Audio` `vlqt_now_ns`, `ResReadAudio`, `ResDropAudio`, `vlqt_audio_tap_state` (`IAudioTapReader.cs:18–35`), and the tests. Nothing in `Native.cs` has to be public outside the VL.Qt assemblies.
- **Decided:** DECISIONS and P4 → done in wave 4, after the break, so the names of CAPI-03 are final and 'do the rename first' holds; S1 and TARGET 2.2 → folder `Interop/`, namespace `VL.Qt.Interop`, with `NativeConstants.cs` (PROTO-06) beside the files listed; A5 and glossary 1.6 → the host values are `vlqt_host_pump` in `Native.Perf.cs`
- **Effort:** M, mechanical: yes
- **Touches:** `src/VL.Qt/Native.cs` → `src/VL.Qt/Interop/*.cs`; `using` lines in the 14 users of `Native` in `src/VL.Qt`, `src/VL.Qt.Stride/StrideInterop.cs`, `SharedTextureScene.cs`, `src/VL.Qt.Audio/IAudioTapReader.cs`, `AudioTapPlayer.cs`, `src/VL.Qt.Audio.Tests/SimulatedTap.cs`, `src/VL.Qt.Probe/Program.cs`, `src/VL.Qt.SmokeTest/*`, `src/VL.Qt.Tests/*` (4 files); `docs/architecture.md` (file map)
- **Depends on:** CAPI-03 (do the rename first or in the same change), CAPI-04
- **Verification:** `dotnet build` of all projects, `dotnet test src/VL.Qt.Tests`, SmokeTest `--all`

### CAPI-08 vlqt.h gets a conventions block; design notes move to docs
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/vlqt.h:1`, `162–172`, `210–224`, `496–510`, `515–569`, `570–593`; `src/VL.Qt/Native.cs:192–196`, `263–266`, `322–327`, `335–338`, `344–348`, `539–543`
- **Finding:** The rules that hold for the whole API are nowhere stated together: thread (main thread unless noted; "any thread" for three functions), return codes, string encoding and copying, buffer convention, ownership (section 2 table), the meaning of a scene handle in window functions. Each is repeated or varied in single function comments, and line 1 ("Return 0 = ok unless noted") is the only general statement. At the same time several function comments are design notes: `vlqt_window_set_samples` (15 lines, how a QQuickWindow is replaced), `vlqt_offscreen_bind` (30 lines), `vlqt_offscreen_render_batch` (16 lines of fence order); `docs/decisions/render-texture.md` already exists for them. `Native.cs` repeats parts of these comments in other words (e.g. `Native.cs:192–196` vs `vlqt.h:162–172`, `322–327` vs `515–546`), so one fact lives in two places that can drift.
- **Proposal:** Top of `vlqt.h`: a "Conventions" block of about 15 lines: ABI (C, cdecl, blittable, no `bool`), threads, `vlqt_result` (CAPI-02), `vlqt_last_error` per thread (CAPI-01), strings (UTF-8, copied before return), buffers (`buf`, `cap`, `*required`), ownership table in short, handles (`vlqt_scene`: window or offscreen scene, which functions are window-only). Function comments keep the contract (parameters, units, return values, thread if different) in at most about five lines and point to `docs/decisions/render-texture.md` or `docs/architecture.md` for mechanism (fence order, window replacement, D3D details). `Native.cs` summaries say only what the C# side adds (span semantics, what the wrapper returns) and name the C function otherwise.
- **Decided:** G5 → `docs/decisions/render-texture.md` is `docs/design/render-texture.md`; O2 → the conventions block says `vlqt_scene` for the handle
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/vlqt.h`, `src/VL.Qt/Native.cs` (or the files of CAPI-07), `docs/decisions/render-texture.md`, `docs/architecture.md`
- **Depends on:** CAPI-01, CAPI-02, CAPI-03 (the block describes their result)
- **Verification:** review only; `--drift` stays green

### CAPI-09 Log callback as an UnmanagedCallersOnly function pointer
- **Kind:** lifetime
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Native.cs:78–79`, `173`; `src/VL.Qt/QtLog.cs:29–30`, `79–90`; `src/VL.Qt.Probe/Program.cs:13`; `src/VL.Qt.SmokeTest/Program.cs:19`
- **Finding:** The callback is a marshalled delegate. Each of the three hosts (library, Probe, SmokeTest) must remember to root it in a static field ("Delegate must stay reachable for the lifetime of the process", `QtLog.cs:29`); forgetting it gives a crash at the first Qt message after a GC, far from the cause. VL.StandardLibs already uses unmanaged function pointers for native callbacks (`VL.Video/src/MF/SourceReader.cs:326–328`, `delegate* unmanaged[Stdcall]`).
- **Proposal:** `[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })] static void OnLog(int level, byte* category, byte* message)` in `QtLog`, and `vlqt_app_init(string nativeDir, delegate* unmanaged[Cdecl]<int, byte*, byte*, void> log)`; delete `Native.LogFn`. A static method cannot be collected, so the rooting rule disappears. Probe and SmokeTest get their own static `[UnmanagedCallersOnly]` method. The try/catch in `OnLog` stays (an exception still must not escape).
- **Decided:** B5 of [DECISION-ROUND.md](../DECISION-ROUND.md) → joins the break: the new signature of `vlqt_app_init` lands in the names pull request of step 3 of P4
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Native.cs`, `src/VL.Qt/QtLog.cs`, `src/VL.Qt/QtApp.cs:292`, `src/VL.Qt.Probe/Program.cs`, `src/VL.Qt.SmokeTest/Program.cs`, `VL.Qt.csproj` (`AllowUnsafeBlocks`, if not already set)
- **Depends on:** none
- **Verification:** SmokeTest `--all` (the log is checked for Qt messages, `Program.cs:441`); the help patch `HowTo Configure logging providers` shows Qt messages in vvvv

### CAPI-10 Copy the apply-error reason when the struct is read
- **Kind:** lifetime
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Native.cs:45–55`; `src/VL.Qt.Native/vlqt.h:74`; `src/VL.Qt/SceneCore.cs:197`, `203`
- **Finding:** `vlqt_apply_error.Reason` is a property that dereferences a native pointer valid only until the next apply of that window. The struct is a copyable value; a copy kept past the next apply (stored in a field, or read after a resync that applies again) reads freed memory. Today's callers read it at once (`SceneCore.cs:197`, `203`), so this is a trap for the next change, not a present defect.
- **Proposal:** `Native.GetApplyError(SceneHandle scene, out ApplyError error)` returns a managed `internal readonly record struct ApplyError(int Rc, int Command, uint Id, ushort Op, ushort Prop, string Reason)` with the reason copied inside the wrapper; the raw `vlqt_apply_error` becomes private to `Native`. The copy happens only on an error, so no per-frame allocation.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Native.cs` (or `Interop/Native.Scene.cs`), `src/VL.Qt/SceneCore.cs`, SmokeTest files that read `vlqt_apply_error`
- **Depends on:** CAPI-07
- **Verification:** SmokeTest scenarios that provoke apply errors (resync, `elements`), unit test `ResyncTests`

## 4 Target structure

**QtNative (C API)**

- `src/VL.Qt.Native/vlqt.h`: product API only (about 67 exports after the deletions). Conventions block, then one section per group in this order: app, scene, window, offscreen, resources, image, shader, snapshot, audio tap, font. Types `vlqt_scene` (opaque), `vlqt_result` (moved from `protocol.h`'s `vlqt_offscreen_result` plus the general codes), the API structs. Renames: table in CAPI-03.
- `src/VL.Qt.Native/vlqt_test.h` (new): every `vlqt_test_*` export (about 31), including the renamed `vlqt_test_perf_eval`, `vlqt_test_perf_host_eval`, `vlqt_test_window_grab`.
- Deleted exports: `vlqt_window_create` (short form), `vlqt_app_process_events` (use `_for(NULL, 0)`), `vlqt_test_perf_stats_text` (if CAPI-05 confirms the successor).
- `vlqt.cpp`: `thread_local g_lastError`, `fail(code, message)`, `guarded` returning `VLQT_ERR_INTERNAL`, the layout `static_assert` block. Whether the exports move into per-group `api_*.cpp` files is for the review of `vlqt.cpp` (section 6).

**C# (`src/VL.Qt/Interop/`, namespace `VL.Qt.Interop`, all `internal`)**

- `QtNativeLibrary.cs`: locate and load `vlqt`, resolver, `Handle`, `EnsureLoaded` (split from `Native.cs`).
- `QtTreePathCheck.cs`: Windows MAX_PATH diagnostic of the Qt tree (split from `Native.cs`).
- `SceneHandle.cs`: `readonly record struct SceneHandle(IntPtr Value)` (new).
- `ApplyError.cs`: managed copy of the last apply error (new, CAPI-10).
- `Native.cs`: partial root (`lib`, `LastError`, `Result` codes).
- `Native.App.cs`, `Native.Scene.cs`, `Native.Perf.cs`, `Native.Window.cs`, `Native.Offscreen.cs`, `Native.Resources.cs`, `Native.Images.cs`, `Native.Shaders.cs`, `Native.Snapshots.cs`, `Native.AudioTap.cs`, `Native.Fonts.cs`: externs with the C names, nested structs with the C names, PascalCase span wrappers; raw buffer externs `private`.
- `src/VL.Qt/Native.cs`: deleted (split).

**Tests**

- `src/VL.Qt.TestSupport/NativeTest.cs` (new, linked into SmokeTest and VL.Qt.Tests): all test externs and wrappers, own resolver through `QtNativeLibrary.Handle`.
- `src/VL.Qt.Tests/NativeLayoutTests.cs` (new): struct sizes and offsets.
- `src/VL.Qt.SmokeTest/ProtocolDrift.cs`: extended (or `ApiDrift.cs` next to it) to compare header exports with the `[DllImport]` entry points.

## 5 Questions for Sebastian

**Q1 Test exports in the shipped binary?** 32 of 101 exports exist only for the SmokeTest and unit tests; `vlqt_test_device_lost` can make the shared D3D11 device lost, the `vlqt_test_d3d11_*` functions create devices and textures.
- (a) Keep them in every build, separated into `vlqt_test.h` (CAPI-04). The SmokeTest keeps running against exactly the binary that ships. Cost: about 30 extra symbols, a few KB.
- (b) A CMake option `VLQT_TEST_API` (on in `tools/build.ps1`, off for the package). The package is smaller and cannot be misused, but the CI smoke test then runs on a different binary than the one in the `.nupkg`, or the CI has to build twice.
- Recommendation: (a). Nothing outside the repository calls `vlqt.dll` directly; the separation in headers and C# files gives the reader the clarity, and testing the shipped binary is worth more than hiding 30 symbols.

**Q2 Rename the C ABI in 0.10.0, or only the C# side?** CAPI-03 renames about 35 exports and changes the handle type. QtNative is only ever loaded by VL.Qt itself (no third-party consumers of `vlqt.dll` are known), so the ABI break has no outside cost; the effort is a large mechanical change across both layers, the SmokeTest and the docs.
- (a) Rename both layers now (one name per function across C and C#).
- (b) Keep the C names and only regroup and rename the C# wrappers.
- Recommendation: (a), together with the protocol changes of review 08 if that one also touches `protocol.h`, so the native layer is broken once.

**Q3 Error text: per-thread last error, or a buffer per call?** CAPI-01 makes `vlqt_last_error` per thread. The alternative is an `err`/`errCap` buffer on every function, as `vlqt_image_write` has today: thread-safe by construction, but every signature grows by two parameters and every wrapper needs a stack buffer.
- Recommendation: per-thread last error (the model of `GetLastError`/`errno`), and `vlqt_image_write` drops its buffer (CAPI-02).

## 6 For other areas

- Native runtime (`vlqt.cpp`): 2,452 lines mix about 85 exports with their implementation; the export bodies could move into per-group `api_*.cpp` files that only check arguments and call into the subsystems, as `vlqt_shader_reflect` and the `vlqt_res_*` exports already do (`vlqt.cpp:2247–2300`).
- C# runtime: `src/VL.Qt/OffscreenRenderer.cs` is classified as binary by `grep` and `file` ("data"), so it contains a byte that is not valid text (no NUL); find and remove it, otherwise searches and diffs skip the file.
- Tests: the SmokeTest declares Win32 functions (`FindWindow`, `SendMessage`, `PostMessage`, monitor queries) separately in 10 files (`BorderlessTest.cs:16`, `FullscreenTest.cs:18`, `KeyRouteTest.cs:24`, `MsaaTest.cs:23`, `QtTextureTest.Input.cs:371`, `RendererTest.cs:20`, `RestartTest.cs:22`, `TearingTest.cs:313`, `WindowIconTest.cs:11`, `PumpTest.cs:65`); one `Win32.cs` in the SmokeTest would remove the duplicates.
- Deployment: `Native`'s static constructor prepends the Qt folder to the process `PATH` (`Native.cs:96–98`), which every later native load of every package sees; the reason is documented, but `docs/decisions/qtnative-deployment.md` should say it as a contract, since a change there affects other vvvv packages.
