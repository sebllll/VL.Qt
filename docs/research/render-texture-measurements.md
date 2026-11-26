# Render texture measurements (research, 2026-11-15)

Measurements behind the design notes [render-thread.md](../design/render-thread.md) (thread pool) and [clear-and-msaa.md](../design/clear-and-msaa.md) (the `PreviousFrameLayer` and MSAA). They carry no design rules; the notes cite them.

## Machine and versions

All numbers come from one machine: Snapdragon X Plus (8 cores), Adreno X1-45, 16 GB, Windows 11 ARM64, Direct3D 11, Qt 6.12.0, display scale 150 % where a window was measured. **x64 not measured.** GPU clocks vary between runs on this notebook; the trends held, single values are ±10 to 50 % (noted per block).

## Render threads

Scenario to rerun: SmokeTest `qttextureperf` (opt-in, about a minute, own process): `tools/build.ps1 -Test -Only qttextureperf` (`src/VL.Qt.SmokeTest/QtTexturePerfTest.cs`). It measures the pool size of the pin `Render Threads` and then the main-thread mode (0). The per-scene thread variant of the first measurement is no longer implemented and not in the scenario.

Scene: 1280×720, 2000 rounded rectangles + 80 texts ("heavy") or 100 + 4 ("light"); per frame 50 colours, 10 texts and a frame-number marker change. A frame imitates vvvv: apply, `render_batch`, 6 ms of Stride CPU work, then a GPU read of scene 0's texture on the test device (`Map`; stands in for Stride's draw). Two rounds; round 2 below, round 1 within ±10 % except the noisy first runs. Medians in ms, columns are `Render Threads` 0 (main thread) / 1 / pool of 2 / one thread per scene (dropped variant):

| Scenes | Main time per QtTexture | Frame | Qt CPU render per scene | Stride GPU read wait |
|---|---|---|---|---|
| 1 heavy | 2.57 / 0.34 / 0.34 / 0.30 | 9.9 / 7.8 / 7.7 / 7.6 | 2.6 / 2.4 / 2.4 / 2.4 | 1.0 / 1.1 / 1.1 / 1.0 |
| 4 heavy | 2.63 / 0.18 / 0.17 / 0.20 | 18.3 / 11.8 / 9.7 / 8.9 | 2.5 / 2.4 / 3.4 / 4.8 | 1.0 / 4.5 / 2.3 / 1.4 |
| 8 heavy | 2.76 / 0.17 / 0.17 / 0.28 | 31.1 / 23.5 / 18.7 / 17.2 | 2.6 / 2.5 / 3.7 / 7.8 | 1.1 / 14.9 / 10.2 / 7.8 |
| 8 light | 0.72 / 0.07 / 0.08 / 0.18 | 14.0 / 8.2 / 8.3 / 8.9 | 0.7 / 0.6 / 1.0 / 2.6 | 1.1 / 1.0 / 1.0 / 1.0 |

- Main-thread time per QtTexture drops from 0.7–2.8 ms to 0.07–0.34 ms (polish, blocking sync, hand-off; sync alone 0.06–0.19 ms). Every frame of every run held its own marker (0 of 230 misses): same frame.
- More threads shorten the frame only for several heavy scenes, and pay with CPU: render time per scene grows 1.4× (pool of 2) to 3× (a thread per scene) through contention (cores shared with vvvv, Qt-global locks, the context lock: 0.12–0.16 ms wait per scene frame vs < 0.001 ms with one thread). Round 1 showed ±50 % run-to-run noise for the 4- and 8-scene cases.
- WARP (software device): the foreign context waiting for a value the render thread signals later deadlocked in 9 of 10 runs (hardware: never). The fallback in [render-thread.md](../design/render-thread.md) (wait for the submission on the main thread) ran 2 of 2.
- The `qttexture` suite passed with one render thread: 208 PASS, 0 FAIL at the time.

## MSAA cost

Scenario to rerun: none for the cost. The measurement came from a spike scenario that was replaced by `Msaa` (`src/VL.Qt.SmokeTest/MsaaTest.cs`), which checks edges, counts and the window swap but does not time them. Reproduce by timing a window and a `QtTexture` with the scene below at each `Multisample Count`, with VSync off, reading `PerfStats` (GPU ms) for the window and `render_batch` + readback for the texture.

Scene: heavy, 2000 rotated 40×40 rectangles, 50 recoloured per frame, no VSync; window 1280×720 DIP (1920×1080 px); medians.

| Count | Window: GPU ms / fps | QtTexture 1280×720: render_batch + readback ms (two runs) |
|---|---|---|
| None | 2.4 / 180 | 9.6 / 11.4 |
| X2 | 4.9 / 192 | 10.6 / 14.4 |
| X4 | 8.0 / 120 | 11.7 / 16.4 |
| X8 | 15.5 / 69 | 16.4 / 20.0 |

On this GPU 4× costs about 3.3× the GPU time of None in a window (today's `Renderer` default). The offscreen numbers include a constant CPU readback of the texture and vary by ±20 % between runs.

Other facts measured with the same spike: a count change of a window costs 16–21 ms for the call and 24–43 ms for the first pump; edge pixels of the standard pattern at 1/2/4/8 samples are 0 / 352 / 572 / 814 (UNORM) and 3 / 5 / 9 grey levels as expected; `CheckMultisampleQualityLevels` listed 1/2/4/8 and not 16 for `B8G8R8A8_UNORM`, `R8G8B8A8_UNORM`, `R16G16B16A16_FLOAT` and `D24_UNORM_S8_UINT`.

## Clear cost (the PreviousFrameLayer)

Scenario to rerun: SmokeTest `clear-pin` (`src/VL.Qt.SmokeTest/ClearPinTest.cs`, run with `tools/build.ps1 -Test -Only clear-pin`). It measures the mean GPU and render time of a window with `Clear` on and off (best of 3 rounds, because the GPU clock varies) and holds them against a limit; it does not print the table below.

Setup: swap interval 0, display scale 1.5, window with 4× MSAA, GPU time per frame from the perf sample, means of 120 frames, 3 interleaved rounds, 7 runs. The numbers include the MSAA cost of the layer (a 4-sample layer plus resolve, then the window pass at 4 samples); layer samples 1 and 4 cost the same.

- 800×600 DIP: +0 to +0.25 ms (for example `Clear` on 0.44 ms, off 0.69 ms; in other runs both 1.6–2.3 ms).
- 1920×1080 DIP (2880×1620 px): `Clear` on 2.0–2.2 ms, off 2.3–2.5 ms in most runs, up to 3.3–3.5 ms in one: about +0.3 ms, at worst +1 ms.
- CPU render 0.02 → 0.05 ms.
- Memory: two textures of the window size (recursive), RGBA8: 2 × 18.7 MB at 2880×1620, plus the 4-sample render buffer of the layer in a window (computed, not measured: about 75 MB at that size).
- Edge pixels of the layer against `Clear` on, rotated square (distinct values between background and fill; n samples give n − 1): window `Clear` on 3, layer default 3, layer 1 3, layer 2 1, layer 4 3, layer 8 7; offscreen `Clear` on 0, layer default 0, layer 4 3. The default keeps the edges exactly as with `Clear` on.
- Idle: with `Clear` off, 30 of 30 idle pumps of a window counted as changes and 10 of 10 offscreen batches rendered, because a recursive live layer requests the next frame itself.
