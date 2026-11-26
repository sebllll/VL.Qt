# Review 22 – Companion packages: VL.Qt.Stride and VL.Qt.Audio

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Stride/` (all 11 files: `QtRendererNode.cs`, `QtTextureNode.cs`, `QtTextureRenderer.cs`, `SharedTextureScene.cs`, `StrideInterop.cs`, `LayerComposition.cs`, `NotificationQueue.cs`, `TextureFormats.cs`, `TexturePixelMapping.cs`, `ToTexturePixelsNode.cs`, `VL.Qt.Stride.csproj`), `src/VL.Qt.Audio/` (all 5 files), `src/VL.Qt.Native/audio_tap.cpp`, `audio_tap.h`, the package folders `VL.Qt.Stride/` and `VL.Qt.Audio/` (`*.vl`, `README.md`, `help/Help.xml`), `docs/video-audio.md`, `docs/decisions/render-texture.md` lines 1–140 (status, decisions, Stride layer, render thread, package layout, input contract). Skimmed (targeted lookups): `vlqt.cpp` (`guarded`, `vlqt_res_read_audio`), `Native.cs:545–594`, `ResourceClient.CreateAudioTap`, `NodeErrors.LogOnly`, `VideoNode.cs` pin docs, the test project files, VL.StandardLibs `VL.Stride.Runtime/src/SkiaRenderer.cs`, `VL.ImGui/src/Properties/AssemblyInfo.cs`, `VL.ImGui.Stride/`. Not read: the help patches (`.vl`) of both packages, `render-texture.md` from line 141 (Clear pin, MSAA), the test sources (only counted).

## 1 Summary

Both companion packages are in good shape: small, focused, and the hard parts are deliberate and documented. The Stride side follows VL.Stride closely (`QtRenderer` mirrors `SkiaRenderer` attribute for attribute, the draw-start hook replaces an internal event by public API, ownership and device reset are handled in one class). The audio side separates a pure, tested control loop (`AudioTapPlayer`) from VL.Audio and Qt, and the native tap is a correct lock-free SPSC ring with generation guards.

What matters most:

1. The NuGet README of `VL.Qt.Stride` is wrong: it lists one node and says "no input yet" (COMP-01).
2. The audio callback does take a lock: every native `read` locks the tap registry mutex, and the last reference of a tap can be freed on the audio thread (COMP-02).
3. Two real-behaviour suspicions in the audio path: a tap already owned by another Video item is silently ignored and that item plays Qt's own sound at full volume (COMP-03); the WASAPI output latency uses the minimum device period, not the period the stream runs with (COMP-04).
4. `QtRenderer` keeps state for exactly one render target; drawn into two targets per frame it rebuilds a view and re-subscribes input on every draw (COMP-05).
5. The dependency on VL.Qt internals is fine in principle (VL.ImGui does the same), but it has no defined seam: the companions call `Native` and `Protocol` directly (COMP-07).

## 2 What works and must survive

- **Ownership of the shared texture** (`SharedTextureScene.cs:156–204`, `StrideInterop.cs:35–72`): Stride creates the texture, every bind gets its own NT handle that is closed right after `Bind`, release order is Qt side → Stride texture (`ReleaseTexture`, `:216–224`). Stride's own leaked named handle is closed once (`StrideInterop.cs:59–62`). Keep the order and the per-bind handle.
- **Same frame without CPU wait**: nodes schedule in `Update`, one `DrawStartSystem` with `DrawOrder = int.MinValue` renders the whole batch in one native call, fenced on Stride's immediate context (`QtTextureRenderer.cs:48–51, 86–122`). Public API only, no reflection on `VLGame.DrawStarted`.
- **Device reset**: `IGraphicsDeviceService.DeviceResetting/DeviceDisposing` → `StrideDeviceGone` → release now, recreate in the next `Update` (`QtTextureRenderer.cs:52–58`, `SharedTextureScene.cs:208–213`). QtNative's own device loss only rebinds, throttled for structural failures (`:173–179`).
- **Premultiplied output**: the clear colour is premultiplied in C# because Qt does not (`SharedTextureScene.cs:94–95`); the layer blends with `BlendStates.AlphaBlend` (premultiplied in Stride).
- **QtRenderer composition**: a UNorm view on the target's resource gives Qt-window blending; fallback to the linear path otherwise (`LayerComposition.cs`, `QtRendererNode.cs:128–160`). It matches VL.Stride's `SkiaRenderer` (`VL.Stride.Runtime/src/SkiaRenderer.cs:22–23` same `ProcessNode` category and `FragmentSelection.Explicit`, `:62–104` same `Destroyed` handling and `GetWindowInputSource`).
- **Input queue semantics**: queued notifications survive a source change (`NotificationQueue.cs:27–41`), the remark explains why (a patch that builds its observable every frame). `ToTexturePixels` returns the same observable while Input stays the same and swaps the mapping with `Volatile` (`ToTexturePixelsNode.cs:48–61`).
- **Audio**: `AudioTapPlayer` is free of VL.Audio and Qt behind `IAudioTapReader` and is tested with simulated clocks; settings cross threads as single volatile fields. Native: ring and records allocated in `init`, producer and reader lock free with release/acquire publishing (`audio_tap.cpp:97–141, 190–256`), the generation counter makes buffers of an old `QAudioBufferOutput` harmless (`:105–110, 307`), the attach order (buffer output before `setAudioOutput(nullptr)`, `:326–330`) and `releaseVideo` before the player dies (`:480–489`) avoid known Qt crashes and stalls. Keep the comments that explain these orders.
- **Hot paths** (no allocation per frame/buffer today; keep it so): `QtTextureRenderer.OnDrawStarted` (reused `_batch` array), `SharedTextureScene.Update`, `QtRendererNode.DrawCore` (`StrideInterop.GetNativeResource` per draw), `NotificationQueue.Drain` (list swap), `TexturePixelMapping.Map`, `AudioTapPlayer.Fill` and everything it calls, native `produce` and `readRing`.

## 3 Findings

### COMP-01 Rewrite the VL.Qt.Stride package README
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `VL.Qt.Stride/README.md:5–11`, `VL.Qt.Stride/help/Help.xml:3`
- **Finding:** The README is the NuGet page of the package. It says the package contains "the node `QtTexture`" and lists "no input yet" as a limit. The package has three nodes (`QtTexture`, `QtRenderer [Stride.Rendering]`, `ToTexturePixels`) and `QtTexture` takes input through `Input Notifications`. It also says the help patches are "in the Help Browser under `Qt`"; `Help.xml` files them under the topic `Stride`. A user who reads the NuGet page gets a wrong picture of what the package does.
- **Proposal:** Replace "What it contains" with three bullets, one per node (one sentence each: `QtTexture` – scene into a Stride texture, premultiplied, same frame, input via `Input Notifications`; `QtRenderer` – layer after the 3D scene, blends like a Qt window, takes the window's input; `ToTexturePixels` – maps window input to the pixels of a QtTexture on a quad). Limits: Windows/Direct3D 11 only, same GPU adapter as Qt windows, experimental. Help: "Help Browser, topic `Stride`". Link the section of the main README and `render-texture.md` as today.
- **Effort:** S, mechanical: no
- **Touches:** `VL.Qt.Stride/README.md`
- **Depends on:** none
- **Verification:** read the README against the `[ProcessNode]` attributes of `src/VL.Qt.Stride/*Node.cs` and `Help.xml`.

### COMP-02 No lock and no free in the native audio read
- **Kind:** threading
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/audio_tap.cpp:44–55, 69–77, 460–472`, `audio_tap.h:7–9`
- **Finding:** `read` (the audio thread, several calls per VL.Audio buffer: `AudioTapPlayer.Prepare`, `Refill`, `Resync`) starts with `findTap`, which takes `std::mutex s_mutex`. The header says the registry is "readable from the audio thread without the manager's mutex", true only in that it is a different mutex. The main thread holds it during `unordered_map` insert (may rehash and allocate) and erase, so the audio thread can block on a main-thread allocation – rare (once per tap create/destroy of any `Video (VL.Audio)` in the process), but exactly the priority inversion the audio rule forbids. Second, `destroy` erases the map entry while a reader may hold a `shared_ptr` copy; if the reader's copy is the last one, `~TapWork` frees the ring (`samples`, `records`) on the audio thread.
- **Proposal:** A registry the reader never locks and never frees from. Concretely: a fixed table of `maxTaps = 64` slots `struct Slot { std::atomic<uint64_t> id; std::atomic<int> readers; std::shared_ptr<TapWork> work; }` (main thread writes `work` before publishing `id` with release). Reader: for each slot with `id == wanted` (acquire): `readers.fetch_add(1)`, re-check `id`, use `work.get()`, `readers.fetch_sub(1)`. `destroy` (main thread): `id.store(0, release)`, spin until `readers == 0` (microseconds: one read), then `work.reset()` on the main thread. `init` returns `VLQT_RES_...` "too many taps" when the table is full. Update the comment in `audio_tap.h:7–9` to describe this. Keep `vlqt_res_read_audio`'s signature.
- **Decided:** A5 (names of the C ABI), S2, P4 step 3, B4 → the files are `src/VL.Qt.Native/resources/audio_tap.{h,cpp}` (folder `resources/`, TARGET 2.3); the export `vlqt_res_read_audio` is `vlqt_audio_tap_read` (group `vlqt_audio_tap_*`, signature kept); a full table fails `init` through the return code that every export gets plus the per-thread last-error text, not through a message buffer
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/audio_tap.cpp`, `src/VL.Qt.Native/audio_tap.h`
- **Depends on:** none
- **Verification:** SmokeTest `videoaudio` (`--only videoaudio`); add a step that creates and destroys 100 taps on the main thread while a second thread reads another tap in a loop and asserts no read takes longer than 1 ms.

### COMP-03 A tap already owned by another Video must not leave Qt's sound on
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/audio_tap.cpp:506–510`, `src/VL.Qt.Audio/VideoAudioNode.cs:97–101`
- **Finding:** `setVideoTap` returns `true` without attaching when the tap is owned by another `TapAttachment` ("ignored until it detaches"). Nothing re-runs the setter when the other item detaches (a prop setter runs on change only), so "until it detaches" never comes. Meanwhile the C# node sets the element's `Volume` to 1 whenever it has a tap (`VideoAudioNode.cs:101`), assuming Qt's output is off; for the ignored item it is not: its player still has Qt's `audioOutput`, so that item plays through the system device at full volume while the first item plays through VL.Audio – the "sound from two places" the node comment wants to prevent. This happens when one `Video (VL.Audio)` output element ends up as two items in the tree. Suspicion: I did not verify that the reconciler creates two native items for one element used twice; confirm with a patch that connects one `Video (VL.Audio)` to two `Group`s.
- **Proposal:** In the owned-by-another branch, mute the item (set its player's `audioOutput` to null as `attach` does, without a buffer output) and return `false` with `err = "audio tap already used by another Video item: use one Video (VL.Audio) per item"`, so the node shows the error (ApplyBadValue). No retry logic.
- **Decided:** A2 and the occurrence rule (glossary: the first occurrence is the primary, the others show a placeholder), 'No `Error` output pins' → a second occurrence of a Video element is a placeholder, so one node should no longer put the same tap on two native items; the mute-and-refuse branch stays as proposed for what is left, and wave 1 confirms or drops the suspicion; the message is not an `Error` pin: it goes to the log and marks the node; the file is `resources/audio_tap.cpp` (S2)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/audio_tap.cpp`
- **Depends on:** none
- **Verification:** SmokeTest `videoaudio`: two Video items with the same tap id; the second gets the error and its player has no audio output.
- **Wave 1 (W1-16):** confirmed by `VideoAudioOwnedTapTest` (SmokeTest, `[Ignore("COMP-03: ...")]`, win-x64, Release, RTX 5080, Realtek default device): the probe is the Core Audio session meter of the test process. A Video with a tap alone: peak 0.000 (Qt silent); a plain Video: peak 0.286 (the probe works); a second Video with Volume 1 (what `VideoAudioNode` sends) on the tap the first one owns: peak 0.286, i.e. Qt's own sound comes out. `setVideoTap` logs "already attached to another Video, ignored until it detaches" and returns `true` without `setAudioOutput(nullptr)`, nothing re-runs it later. Native defect confirmed; whether the reconciler still creates two native items for one element is no longer the question after A2 (second occurrence = placeholder), the mute-and-refuse branch stays for what is left, so W4-60 takes COMP-03 in.
- **Wave 1 (W1-16):** measured (C19, win-x64, Release, RTX 5080, offscreen QtNative window 640x360, `videoaudio` scenario, 5 runs, machine at 15-35 % CPU from idle WSL, nothing else playing): audio due vs frame at the sink, median of the five run medians 0.40 ms (0.29-0.45), median of the five |p90| 1.24 ms (0.94-1.51); sample rate 0.996-1.006 s/s at 48 kHz, 0.997-0.998 at 44.1 kHz, 0.985-1.004 after the MSAA swap; overrun 0, underrun 0 in all runs; first frames after re-attach 21-33 ms. The win-arm64 spike figures (about 1 ms standard deviation) hold on x64 for the native tap and the sync at the sink. Not measured: the output latency of VL.Audio and `Sync Error` in vvvv (needs vvvv with a device and the help patch, manual-check), so `Video (VL.Audio)` stays experimental until that is done (C19 as decided).

### COMP-04 Output latency: use the period the WASAPI stream runs with
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes: the sound of `Video (VL.Audio)` on WASAPI may shift by a few ms; `Audio Offset` calibrations change by that amount.
- **Where:** `src/VL.Qt.Audio/VideoAudioSignal.cs:22–23, 63–67, 89–94`, `docs/video-audio.md:39`
- **Finding:** The summary says WASAPI latency is "the device period VL.Audio opens the shared stream with"; the code reads `AudioClient.MinimumDevicePeriod` of a newly activated client (typically 3 ms), and the fallback constant is described as "the usual default period" (10 ms). Minimum period, default period and the stream's actual latency differ; the target delay of the control loop (`AudioTapPlayer.Render`, `:174`) and `Sync Error` inherit the error. Suspicion: I could not read VL.Audio's WASAPI setup (not in VL.StandardLibs); confirm which latency VL.Audio passes to NAudio's `WasapiOut`.
- **Proposal:** Take the latency VL.Audio configured (the `WasapiOut` latency setting of the engine, if VL.Audio exposes it) or, if not, `DefaultDevicePeriod` plus `AudioClient.StreamLatency` of the device; make the summary and `docs/video-audio.md:39` say which one. Keep the 10 ms fallback.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Audio/VideoAudioSignal.cs`, `docs/video-audio.md`
- **Depends on:** none
- **Verification:** help patch `HowTo Play a Video with VL.Audio` on WASAPI: `Sync Error` with `Audio Offset` 0 before and after; manual-check.
- **Wave 1 (W1-16):** confirmed in part by `WasapiLatencyTests` (Audio.Tests, category `Perf`, warn-only; win-x64, Release, Realtek default device, 48 kHz mix, 5 runs, identical): VL.Audio 1.9.21 (`WasapiInOut`, decompiled) opens `new WasapiOut(device, Shared, true, ceil(MinimumDevicePeriod / 10000))`, so the latency argument it passes is the minimum period rounded up (3 ms here), which is the value `VideoAudioSignal` reads (3.00 ms): the suspicion "not the period the stream runs with" is wrong for the argument. But the stream does not run with it: MinimumDevicePeriod 3.00 ms, DefaultDevicePeriod 10.00 ms, requested 3 ms, actual stream buffer 1056 frames = 22.00 ms, `AudioClient.StreamLatency` 0.00 ms (shared mode reports 0). So the proposed source `DefaultDevicePeriod + StreamLatency` gives 10 ms and also does not match the 22 ms buffer; `StreamLatency` is useless on this device. The true end-to-end latency is not measurable this way (needs `Sync Error` in vvvv or a loopback with a hardware clock): W4-129 must decide between 3 ms, 10 ms and the buffer from that measurement; the 1 ms warning of the test stays until then.

### COMP-05 QtRenderer: one target per frame, and no render while not drawn
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Stride/QtRendererNode.cs:36–46, 75–76, 84–111, 163–172`
- **Finding:** The node keeps one target, one resource, one composition, one view and one input source. Drawn into two targets in one frame (one layer connected to two windows, or a window plus a preview render texture, common in vvvv), every draw sees a "new" target: `ReleaseView` + `ChooseComposition` create and dispose a texture view per draw, `SubscribeInput` builds a new `GetNotifications` observable per draw, and the scene renders at the size of whichever target drew last, stretched in the other. Separately, once drawn, `_drawn` stays true: a layer that is later disconnected keeps updating and rendering its scene (on every scene change) for nobody. `SkiaRenderer` has the same single-target state (`SkiaRenderer.cs:62–104`), but its Skia surface costs nothing extra per target.
- **Proposal:** (a) Count the draw frame (`context.RenderContext.Time.FrameCount`) in `DrawCore`; a second, different target in the same frame is not drawn and sets the hint "QtRenderer draws into one target per frame; use one QtRenderer per window" at `Error` (log once). (b) Replace `_drawn` by `_lastDrawFrame`; `Update` passes `enabled && drawnLastFrame` to the scene, so a layer that is no longer drawn freezes instead of rendering. Add one sentence to the node summary: "Draws into one render target."
- **Decided:** 'No `Error` output pins', O2, G5 → the `Error` output of QtRenderer goes, so the hint 'QtRenderer draws into one target per frame; use one QtRenderer per window' (like the existing blend hint) is written once to the vvvv log and shows as a warning at the QtRenderer node, not at an `Error` pin; the freeze of a layer that is no longer drawn is as proposed; `SharedTextureScene` keeps its name; the design note is under `docs/design/` (the Stride note), not `docs/decisions/render-texture.md`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Stride/QtRendererNode.cs`, `docs/decisions/render-texture.md` (section "Stride layer", Timing)
- **Depends on:** none
- **Verification:** help patch `HowTo Draw Qt in a Stride Scene` with the layer connected to a second `RenderWindow`: hint at `Error`, no per-frame allocations in the profiler; manual-check.

### COMP-06 Remove the always-empty Perf pin of QtTexture
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: patches that connected `Perf` of `QtTexture` lose the link.
- **Where:** `src/VL.Qt.Stride/QtTextureNode.cs:35, 49, 66–67`
- **Finding:** `Perf` always outputs `PerfStats.Empty`; its doc says so ("Not measured yet for textures: always empty") and the TODO explains that QtNative has no perf ring for offscreen scenes. A pin that never carries data costs the user a look and teaches nothing.
- **Proposal:** Remove the `perf` out parameter and the TODO. When QtNative gets a perf ring for scenes, the pin comes back with data. (Alternative, if Sebastian wants the pin layout stable: keep it and implement the ring now; that is a native change of area 19 and not proportional.)
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Stride/QtTextureNode.cs`, help patches of `VL.Qt.Stride/help/` that show the pin (check with `tools/vl-patch-check.py`)
- **Depends on:** none
- **Verification:** `python tools/vl-patch-check.py VL.Qt.Stride/help/*.vl`, `tools/vvvvc.ps1 -File` on each Stride help patch.

### COMP-07 Name the seam between VL.Qt and its companions
- **Kind:** architecture
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/VL.Qt.csproj:38–40`; uses: `SharedTextureScene.cs:40, 92, 177, 189` (`OffscreenRenderer`, `SceneRegistry`, `Native.vlqt_window_set_debug`, `Protocol.Offscreen`), `QtTextureRenderer.cs:43, 46, 108–109` (`QtApp`, `QtPump`, `OffscreenRenderer.RenderBatch/Describe`), `SharedTextureScene.cs:229` (`Multisample`), `VideoAudioNode.cs:26, 36–37, 100–101` (`ElementNode`, `VideoNode.Props`, `CommonInputs`, `ResourceClient`, `ImageResource`), `IAudioTapReader.cs:18–35` (`Native.vlqt_audio_tap_state`, `Native.ResReadAudio`)
- **Finding:** The companions see all of VL.Qt through `InternalsVisibleTo`. That is the vvvv pattern for packages built and versioned together (`VL.ImGui/src/Properties/AssemblyInfo.cs:3–4` gives `VL.ImGui.Stride` and `VL.ImGui.Skia` the same access), and the CI builds all three from one solution, so a break shows at compile time. The problem is that nothing says which internals are the contract: `SharedTextureScene` calls the raw P/Invoke `Native.vlqt_window_set_debug` on `OffscreenRenderer.Scene` and compares raw `Protocol.Offscreen` codes, so a person refactoring `Native` or `OffscreenRenderer` cannot tell that a Stride path depends on them. `VideoNode.Props` and `CommonInputs` tie the audio node to the reconciler's record layout.
- **Proposal:** Keep `InternalsVisibleTo`. Make the companions use only these VL.Qt types, and list them in `docs/architecture.md` under a new section "Companion packages" (one line each): `OffscreenRenderer` (add `bool Debug { set; }` that calls `vlqt_window_set_debug` itself; add `bool LastBindRetriesNow` or move the rebind throttle of `SharedTextureScene.RebindIfUnbound` into a method `OffscreenRenderer.ShouldRetryBind(int code)` so `Protocol.Offscreen` codes stay inside VL.Qt), `SceneRegistry.ForApp`, `QtApp`/`QtPump.ForApp`, `NodeErrors`, `Multisample.ClampWarning`, `ElementNode<,>` + `CommonInputs` + `VideoNode.Props`/`VideoNode.Create`, `ResourceClient.CreateAudioTap/Release`, `Native.ResReadAudio/ResDropAudio/vlqt_now_ns` + `Native.vlqt_audio_tap_state`, `Categories`. Remove every other direct use of `Native` and `Protocol` from `src/VL.Qt.Stride/`. A public API is not needed before a third party builds a companion (question 5.1).
- **Decided:** D7, O2, O3, A5, TARGET 1.2 and 2.2 → `InternalsVisibleTo` with the documented seam, as proposed; the names of the seam change: `OffscreenRenderer` → `OffscreenScene` (`Scene/`), `SceneRegistry` → `OccurrenceRegistry` (`Reconciliation/`), `ElementNode<,>` → `ElementNodeCore`, `ImageResource` → `QtResource`, `vlqt_window_set_debug` → `vlqt_scene_set_debug`, `ResReadAudio`/`ResDropAudio`/`vlqt_now_ns` → the group `vlqt_audio_tap_*`, `Native.cs` → `Interop/Native.*.cs`, `Protocol` → `Wire/`; `NodeErrors` is one of the message mechanisms that P22 merges into one (TARGET 3.2); `NotificationQueue` goes to `src/VL.Qt/Scene/` (TARGET 2.4)
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/OffscreenRenderer.cs`, `src/VL.Qt.Stride/SharedTextureScene.cs`, `src/VL.Qt.Stride/QtTextureRenderer.cs`, `docs/architecture.md`
- **Depends on:** none
- **Verification:** `grep -rn "Native\.\|Protocol\." src/VL.Qt.Stride` is empty; SmokeTest `qttexture`; help patches of `VL.Qt.Stride/help/` compile with `tools/vvvvc.ps1`.

### COMP-08 Guard the pin parity of Video and Video (VL.Audio) with a test
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Audio/VideoAudioNode.cs:43–90`, `src/VL.Qt/Nodes/VideoNode.cs:63–81`
- **Finding:** `Video (VL.Audio)` promises "same picture and pins as Video" and copies all 16 input pins with their defaults, visibilities and XML docs by hand. They already differ in text (`rate`: the audio variant adds the tape note, `VideoNode.cs:67` vs `VideoAudioNode.cs:50`, which is intended) and nothing would notice when a pin is added to or renamed in `Video` only. A person changing `VideoNode` does not know that a second file must follow.
- **Proposal:** A unit test in `src/VL.Qt.Audio.Tests/VideoAudioNodePinTests.cs` that reflects over both `Update` methods and asserts that the parameters of `VideoNode.Update` appear in `VideoAudioNode.Update` in the same order with the same name, type, `[DefaultValue]` and `PinVisibility`, allowing exactly the extra pins `audioOffset`, `audio` and `syncError`. Add one line to the summary of `VideoNode`: "Video (VL.Audio) in VL.Qt.Audio mirrors these pins (test VideoAudioNodePinTests)."
- **Decided:** C2, catalog, O3 → both nodes get `Enabled` (optional, default true, last input pin) and both use `FillMode` for `Fill Mode`, so the test needs no exception besides `audioOffset`, `audio` and `syncError`; `VideoNode.Props` and `ElementNode<,>` are `ElementNodeCore` helpers
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Audio.Tests/VideoAudioNodePinTests.cs` (new), `src/VL.Qt/Nodes/VideoNode.cs`
- **Depends on:** none
- **Verification:** the new test passes; renaming a pin of `VideoNode` alone makes it fail.

### COMP-09 Unit-test TexturePixelMapping
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Stride/TexturePixelMapping.cs`, `src/VL.Qt.Tests/VL.Qt.Tests.csproj:32–34`
- **Finding:** The math behind `ToTexturePixels` (ray through the inverse of World·View·Projection, plane z = 0, top-left pixels, miss value) has no test; a search for `TexturePixelMapping` in `src/*Tests` finds nothing. It is pure (Stride.Core.Mathematics only), so it could be linked into `VL.Qt.Tests` like `LayerComposition.cs` and `TextureFormats.cs`. Today a refactoring of it is verified only by clicking in `HowTo Interact with a QtTexture`. The rest of the Stride side (`SharedTextureScene`, `QtTextureRenderer`, `QtRendererNode`) needs Stride's device and is covered only by the help patches; the native half is covered by SmokeTest `qttexture`.
- **Proposal:** Link `TexturePixelMapping.cs` into `VL.Qt.Tests` and add `TexturePixelMappingTests.cs`: fullscreen (`World` = Scaling(2, 2, 1), identity view/projection) maps window corners to texture corners; a perspective camera with a rotated quad hits the centre; a ray parallel to the quad and a singular matrix give `Miss`; a zero matrix counts as identity.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Tests/VL.Qt.Tests.csproj`, `src/VL.Qt.Tests/TexturePixelMappingTests.cs` (new)
- **Depends on:** none
- **Verification:** `dotnet test src/VL.Qt.Tests`.

### COMP-10 Remove the frame-order debug log of task 3.4
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Stride/QtTextureRenderer.cs:88–92`
- **Finding:** A debug log of a past investigation ("Task 3.4 (frame order) … Expected: false") runs at the start of every draw. Its question is answered (`render-texture.md`, Frame timing); the reader cannot tell whether it is still needed. It also boxes five values per frame when Debug logging is on.
- **Proposal:** Delete the log statement and its comment. `_clock` and `_app` stay (used by `DriverSeconds`).
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Stride/QtTextureRenderer.cs`
- **Depends on:** none
- **Verification:** build; help patch `HowTo Render many QtTextures` still renders.

### COMP-11 Schedule with the scene's flag instead of List.Contains
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Stride/QtTextureRenderer.cs:75–79`, `src/VL.Qt.Stride/SharedTextureScene.cs:75–76, 98–102, 237–239`
- **Finding:** `Schedule` checks `_scheduled.Contains(scene)` for every scene every frame: quadratic in the number of QtTextures (the help patch `HowTo Render many QtTextures` is about many). `SharedTextureScene` already keeps the same fact in its own `_scheduled` flag. Two flags for one fact.
- **Proposal:** `SharedTextureScene.Update` calls `_renderer.Schedule(this)` only when its `_scheduled` is false; `Schedule` drops the `Contains`. `ApplyCommands` keeps clearing the flag.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Stride/QtTextureRenderer.cs`, `src/VL.Qt.Stride/SharedTextureScene.cs`
- **Depends on:** none
- **Verification:** help patch `HowTo Render many QtTextures`.

### COMP-12 Say "one reader" for the audio tap ring
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/audio_tap.h:11–12, 104`, `audio_tap.cpp:153`, `src/VL.Qt.Native/protocol.h:749`
- **Finding:** The comments say `read` runs "on any thread"; the ring is single-producer single-reader (`audio_tap.h:55–57`): `readRing` publishes `readFrame`/`recordRead` with plain stores, two concurrent readers of one tap corrupt it. True today (one `VideoAudioSignal` per tap), but the comment invites a second reader (a meter, the SmokeTest on another thread).
- **Proposal:** "Any thread, one reader per tap at a time" in the three places.
- **Decided:** S2 → the files are `src/VL.Qt.Native/resources/audio_tap.{h,cpp}`; `protocol.h` stays at the top of the layer
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/audio_tap.h`, `src/VL.Qt.Native/audio_tap.cpp`, `src/VL.Qt.Native/protocol.h`
- **Depends on:** none
- **Verification:** none needed (comment).

### COMP-13 Make the "engine not running" case of the tap explicit
- **Kind:** naming
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Audio/VideoAudioNode.cs:31–32, 113–117, 129`
- **Finding:** `_failedSampleRate = 0` means "no failed rate", and the check `sampleRate == _failedSampleRate` also returns early when the engine reports sample rate 0 (not running). That the node never asks QtNative for a 0 Hz tap works only through this coincidence; a reader has to work it out.
- **Proposal:** `if (sampleRate <= 0 || sampleRate == _tapSampleRate && _tap is not null || sampleRate == _failedSampleRate) return;` with the comment "no engine yet: no tap", and `_failedSampleRate` as `Optional<int>` or documented "0 = none".
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Audio/VideoAudioNode.cs`
- **Depends on:** none
- **Verification:** `HowTo Play a Video with VL.Audio` with the audio engine off, then on.

### COMP-14 Move NotificationQueue to VL.Qt
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Stride/NotificationQueue.cs`, `src/VL.Qt.Tests/VL.Qt.Tests.csproj:32`
- **Finding:** The queue has nothing of Stride; it feeds `OffscreenRenderer.Notify`, which `render-texture.md` placed in VL.Qt "so a later VL.Qt.Skia reuses it". Living in the Stride package, the unit tests must link its source file into `VL.Qt.Tests`.
- **Proposal:** Move to `src/VL.Qt/Input/NotificationQueue.cs` (or next to `OffscreenRenderer.cs`), namespace `VL.Qt`, `internal`; drop the `Compile Include` link.
- **Decided:** TARGET 2.2 and 2.4, S1 rule 4 → the target is `src/VL.Qt/Scene/NotificationQueue.cs` (not `Input/`), namespace `VL.Qt`, internal; the `Compile Include` link in `VL.Qt.Tests` goes
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Stride/NotificationQueue.cs` → `src/VL.Qt/…/NotificationQueue.cs`, `src/VL.Qt.Tests/VL.Qt.Tests.csproj`
- **Depends on:** COMP-07 (it adds the type to the seam list)
- **Verification:** `dotnet test src/VL.Qt.Tests`.

### COMP-15 One place for the VL.Audio version
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Audio/VL.Qt.Audio.csproj:16`, `VL.Qt.Audio/VL.Qt.Audio.vl:5`
- **Finding:** `1.9.21` is written in the csproj (NuGet dependency) and in the document's `NugetDependency`. An update of one leaves the other behind; nothing checks it.
- **Proposal:** Keep both (vvvv needs the version in the `.vl`), but move `VlAudioVersion` to `Directory.Build.props` next to `VvvvVersion` and add a check to `tools/vl-patch-check.py` or the build that the `.vl` carries the same version. Alternatively drop `Version` from the `.vl` dependency as `VL.Qt.Stride.vl` does for VL.Stride, if vvvv resolves it from the nuspec (check in vvvv).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Audio/VL.Qt.Audio.csproj`, `Directory.Build.props`, `VL.Qt.Audio/VL.Qt.Audio.vl` or `tools/vl-patch-check.py`
- **Depends on:** none
- **Verification:** `dotnet pack src/VL.Qt.Audio` and opening the help patch in vvvv.

## 4 Target structure

Package folders stay as decided in `render-texture.md` ("Package layout"): `VL.Qt.Stride/` and `VL.Qt.Audio/` at the root hold `*.vl`, `help/`, `README.md` and the build output `lib/net10.0-windows7.0/`; sources in `src/`. This differs from VL.StandardLibs (`VL.ImGui.Stride/src/*.csproj` inside the package folder) for a documented reason (no `bin`/`obj` in the package, VL.Qt stays at the root); no change.

`src/VL.Qt.Stride/` (namespace `VL.Qt.Stride`):
- `QtTextureNode.cs` – public, node `QtTexture [Qt]`; `Perf` removed (COMP-06).
- `QtRendererNode.cs` – public, node `QtRenderer [Stride.Rendering]`; nested `ViewportOffset` stays; one target per frame (COMP-05).
- `ToTexturePixelsNode.cs` – public, node `ToTexturePixels [Qt]`.
- `TexturePixelMapping.cs` – internal, the mapping math (now tested, COMP-09).
- `SharedTextureScene.cs` – internal, texture/bind/device loss/scheduling of one scene; no `Native`/`Protocol` (COMP-07).
- `QtTextureRenderer.cs` – internal, per-app-host batch at draw start (nested `DrawStartSystem` stays); debug log removed (COMP-10).
- `StrideInterop.cs` – internal, D3D11 pointers and the shared texture.
- `TextureFormats.cs`, `LayerComposition.cs` – internal, pure, linked into `VL.Qt.Tests`.
- `NotificationQueue.cs` – moved to `src/VL.Qt/` (COMP-14).

`src/VL.Qt.Audio/` (namespace `VL.Qt.Audio`):
- `VideoAudioNode.cs` – public, node `Video (VL.Audio) [Qt.Elements]`.
- `VideoAudioSignal.cs` – internal, VL.Audio signal, output latency (COMP-04).
- `AudioTapPlayer.cs` – internal, the control loop; class summary cut to three lines with a link to `docs/video-audio.md` "Sync" (K4), the design text lives there already.
- `IAudioTapReader.cs` → split (K3): `IAudioTapReader.cs` (interface), `NativeAudioTapReader.cs` (P/Invoke implementation).

`src/VL.Qt.Native/audio_tap.{h,cpp}`: registry replaced by the slot table (COMP-02); owned-by-another case mutes and errors (COMP-03).

Tests: `VL.Qt.Tests` + `TexturePixelMappingTests.cs`; `VL.Qt.Audio.Tests` + `VideoAudioNodePinTests.cs`.

K6 in this area: `AudioTapPlayer.Locked`, `TargetDelayNs`, `FilteredErrorNs` (`AudioTapPlayer.cs:121–128`) exist for `AudioTapPlayerTests` (7 uses). They are read-only views of real state, cheap and harmless; the same assertions could be made on the output signal (a locked loop gives `SyncErrorMs` not NaN, delay from the position of a test click in the output), which is the stronger test. Recommendation: keep `Locked` (no other observable), replace the two others by output-based assertions when the tests are touched anyway. `IAudioTapReader` is a real seam, not a hook: keep. `InternalsVisibleTo VL.Qt.Audio.Tests` in `VL.Qt.csproj:40` is needed because `IAudioTapReader` uses `Native.vlqt_audio_tap_state`.

Public after the refactoring: the four node classes, nothing else.

## 5 Questions for Sebastian

1. **InternalsVisibleTo or a public companion API?** (a) Keep `InternalsVisibleTo` with a documented seam (COMP-07): no API to freeze, refactorings of VL.Qt stay free, the compiler catches breaks because all three packages build together; third parties cannot build a companion. (b) A small public API (`OffscreenRenderer`-like scene class, audio tap reader, element node helper) in a namespace `VL.Qt.Hosting`: lets others build `VL.Qt.Skia` etc., but freezes types that are still moving and must follow `docs/versioning.md`. Recommendation: (a) for 0.10.0, as VL.ImGui does; revisit when someone outside wants to build a companion.
2. **Experimental smell.** `QtTexture`, `QtRenderer` and `ToTexturePixels` carry `[Smell(SymbolSmell.Experimental)]`, `Video (VL.Audio)` does not, although `docs/video-audio.md:57` says it was measured only on win-arm64. Recommendation: mark `Video (VL.Audio)` experimental too until it has been measured on x64, so the node browser treats the companion nodes alike.

## 6 For other areas

- Runtime/offscreen (07/19): `OffscreenRenderer` has no `Debug` setter, so `SharedTextureScene.cs:92` calls `Native.vlqt_window_set_debug` on its raw scene pointer.
- Docs: `docs/decisions/render-texture.md:98` says "A node whose output nobody draws costs nothing"; `QtTextureNode` schedules and renders (on scene changes, every frame with `Clear` off) whether or not anything draws its output.
- Docs: `render-texture.md:70` (status line) and most section titles are issue and task numbers (K1); the file is the design note the Stride code links to, so its first paragraph should say what the package does today.
- Nodes (10): `VideoNode.cs:63–81` pin docs are the source the audio variant copies; see COMP-08.
