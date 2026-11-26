# Native boundary

The API between C# and QtNative: one command buffer per frame in, one event buffer out, a plain C ABI around both. Code: `src/VL.Qt.Native/vlqt.h` (exports, with the conventions block at its head), `protocol.h` and `src/VL.Qt/Wire/Protocol.cs` (wire format), `src/VL.Qt/Interop/` (P/Invoke surface, one `Native.*.cs` per group of `vlqt.h`, and the library loader). How the layers fit together is in [../architecture.md](../contributing/architecture.md); how QtNative is packaged and found is in [deployment.md](deployment.md).

## Context

The C# reconciler knows what the scene should look like; QtNative owns Qt and its objects. Every call over the boundary is a managed/native transition, and every change of the native API means a restart of vvvv, because the DLL stays loaded for the life of the process. The boundary therefore has to be coarse (few calls per frame) and small (little logic on the native side).

## Design

### One command buffer per frame

- The reconciler writes the differences of a frame as a binary command list (`CREATE`, `SET`, `REPARENT`, `DESTROY`, `BIND` and the list ops) and hands it over with **one** call, `vlqt_scene_apply(scene, buf, len)`. QtNative keeps a type and property table and applies the commands in order.
- The way back follows the same principle: `vlqt_scene_poll(scene, out, cap, written, required)` copies the queued events (feedback of elements, window geometry) into a caller buffer, once per frame after the pump. Resource status records have their own queue, `vlqt_res_poll`.
- Why: one transition per frame; a small, stable native API (a new element property is a table entry, not a new export); logic that stays live-editable in C#; a buffer that can be recorded and replayed in tests.
- Commands, events and resource records start with an 8-byte header (`u16` op or event, `u16` flags, `u32` length; `VLQT_HEADER_SIZE` in `protocol.h`) and are 4-byte aligned. The length is a `u32`, capped at `VLQT_MAX_COMMAND_PAYLOAD`; a payload that is too long or does not add up fails with `VLQT_ERR_BAD_LENGTH`. Processing stops at the bad command and earlier commands stay applied.

### The C ABI

- Plain C, cdecl, blittable types only (no `bool`), UTF-8 strings. The exports are named `vlqt_<group>_<verb>`; the groups are `app` (process, main thread), `scene` (window or offscreen scene), `window`, `offscreen`, `res`, `image`, `shader`, `snapshot`, `audio_tap` and `font`. `vlqt_scene*` is one handle type for a window and an offscreen scene; the `vlqt_scene_*` functions take both.
- Every export that can fail returns a code of `vlqt_result` (`>= 0` success, `< 0` a `VLQT_ERR_*`); a pointer export returns `NULL`. The text of the last failure is kept **per thread** and read with `vlqt_last_error` on the same thread, right after the failing call. Exceptions never cross the ABI: they come back as `VLQT_ERR_INTERNAL` with their text in `vlqt_last_error`.
- An export that fills a caller buffer returns `VLQT_ERR_BUFFER_TOO_SMALL` with the needed size and copies nothing; the caller calls again with a larger buffer. Queued events stay queued meanwhile.
- All exports run on the Qt GUI thread (the thread that called `vlqt_app_init`, vvvv's main thread) except the few marked "any thread" in `vlqt.h`; on another thread they return `VLQT_ERR_WRONG_THREAD`. Qt needs a single-threaded apartment on that thread (`OleInitialize`): vvvv's main thread is one, a custom host marks its `Main` with `[STAThread]`.
- `vlqt_test.h` holds exports for the unit tests and the SmokeTest. Every build of `vlqt.dll` contains them, so the CI tests the binary that ships, but they are no part of the C ABI: they can change in any version and no production code calls them.

### Version rule

- One number, `VLQT_ABI_VERSION` (`protocol.h`, mirrored by hand in `Protocol.cs`), covers the command protocol and the C ABI of `vlqt.h` and `vlqt_test.h`. It is bumped on every change to a constant, a struct layout or an exported signature, additions included.
- `QtApp` reads `vlqt_app_get_abi_version()` (needs no init) before the first window or resource and refuses a `vlqt.dll` with another number or without the export: no window opens, the log names both versions and the path, and every Renderer node shows that message. One package ships both files, so the refusal catches stale local builds and mixed folders.
- The drift tests (`ProtocolDriftTests`, `NativeLayoutTests`, `ApiDriftTests`) compare constants, struct layouts and exports with their C# mirrors, without a GPU.

### Logging

- All Qt messages (debug to fatal, QML warnings included) end up in the vvvv log window: `qInstallMessageHandler` in QtNative calls the log callback given to `vlqt_app_init`, which `QtLog` forwards to `ILogger` (category `VL.Qt`, Qt levels mapped). The target is the vvvv log window, not `OutputDebugString`.
- The message handler is process-wide, like the `QGuiApplication`. On Windows `DllMain` cuts the managed callback at process exit, because the CLR is gone when Qt's static destructors log; this works because `vlqt.dll` detaches before `Qt6Core`. Unloading QtNative otherwise is not supported.

## Consequences

- QtNative stays small: logic lives in C# where vvvv reloads it live. A native change needs a rebuild and a restart of vvvv.
- A protocol or ABI change touches `protocol.h` or `vlqt.h`, the C# mirror and the version number together; the drift tests fail when one side is forgotten.
- A debugger sees one apply and one poll per frame and scene, not thousands of calls.

## Alternatives considered

- **One P/Invoke call per property:** too many transitions, and the native API grows with every property.
- **JSON or QML text as a diff:** expensive to parse every frame, no type safety.
- **Qt's own C# bridge:** see [qt-bridge.md](qt-bridge.md).

## Open questions

None.

## Evidence

- `vlqt.h` (conventions block, `vlqt_result`), `protocol.h` (header, `VLQT_ABI_VERSION`), `src/VL.Qt/Runtime/QtApp.cs` (`CheckAbi`), `src/VL.Qt.Native/platform/platform_win.cpp` (`DllMain`).
- `dotnet test src/VL.Qt.Tests --filter "FullyQualifiedName~DriftTests|FullyQualifiedName~NativeLayoutTests"` runs the drift tests.
