# Linux for contributors

Building the Linux runtime, running the SmokeTest on Linux and the known problems of those tests. Exporting and running an app on Linux, for users: [Linux](../linux.md).

## Building the Linux runtime from source

Needed when you export for Linux from the repo sources (the release package already contains it). In the WSL shell, inside the repo (clone it in WSL, or copy the checkout; the repo is also visible under `/mnt/c/...`, but building in the Linux file system is faster):

```bash
tools/setup-wsl.sh          # once: packages + .NET 10 SDK, about 1.5 minutes (x64 on a fast line: 40 s)
tools/setup-qt.sh           # once: Qt 6.12 for Linux into ~/Qt, about 1.5 minutes, 2 GB (x64 on a fast line: 25 s)
tools/build-native.sh       # libvlqt.so
tools/deploy-linux.sh       # Qt bundle next to it in runtimes/linux-<arch>/native
tools/test-linux.sh --skip-native   # optional: SmokeTest + unit tests (--software: without an X server, effect pixel checks skipped)
```

`tools/test-linux.sh` without `--skip-native` runs the build and the bundle step itself (about 1 minute). The RID follows the WSL architecture (`linux-x64` or `linux-arm64`). After that, an export from Windows with this RID includes the Linux runtime. When the repo lives in the WSL file system, copy `runtimes/linux-<arch>` into the `runtimes/` folder of the Windows checkout first. On x64 building directly in `/mnt/c/<path-to-repo>` also works (QtNative 20 s, bundle 20 s) and needs no copy. The Linux scripts are listed in [Development](development.md#scripts).

### Testing the window pins under WSLg

The default platform of `tools/test-linux.sh` is `offscreen` (no window). The window scenarios (`Bounds`, `Borderless`, `Fullscreen`, `Is Visible`) run in a real window with:

```bash
tools/test-linux.sh --skip-native --platform xcb --no-unit --only geometry,renderer,visibility,borderless,fullscreen
```

Log and grabbed PNGs are in `build/test-linux-<rid>/xcb/` (`smoke.out`, `smoke/`). On WSL2 Ubuntu 24.04 arm64 (WSLg) `geometry`, `renderer`, `visibility` and the checks of `borderless` and `fullscreen` that do not depend on the window position pass; four checks are about where the compositor puts the window and print `SKIP` there when it misses the requested position (see [Known problems of the tests](#known-problems-of-the-tests)).

On x64 the keys were checked in an exported `HelloWorldLinux` under WSLg: `F11` and `Alt+Enter` each switch to fullscreen over the whole screen (3440x1440 on that screen) and back to the original position and size; the app ends with exit code 0 on SIGTERM. The pins and the screen choice with two monitors are not checked yet.

For the window pins that need a person (live `Borderless` toggle, `Fullscreen` screen choice, F11 and Alt+Enter) start an export (see [Export from vvvv](../linux.md#2-export-from-vvvv)) and press the keys in its window: F11 or Alt+Enter toggles fullscreen like the `Fullscreen` pin. The live toggles of `Borderless` and `Fullscreen` can be watched in the SmokeTest scenarios (`--filter TestCategory=borderless`, `--filter TestCategory=fullscreen`, the test windows open on the desktop for a moment); no help patch drives these pins live in an exported Linux app yet, and the editor runs on Windows only.

### Timing checks on slower machines

The time limits of the SmokeTest (e.g. 10 000 SETs in one apply < 2 ms) were set on a current Windows x64 desktop CPU. On Linux the SmokeTest first runs a short calibration loop and prints `INFO perf calibration: ... CPU-bound limits x<f>`: the three purely CPU-bound limits (10 000 SET in one apply, in `apply` and in the core checks, and the idle change check of `measure`) are multiplied by that factor (1 on a fast core, at most 3). A measurement above the limit is repeated up to three times before it counts, so a late clock-up or a woken process does not fail the run; a real regression stays above the limit in every round and still fails. `VLQT_PERF_FACTOR` / `--perf-factor` set the factor by hand and switch the calibration off (`VLQT_PERF_FACTOR=0.5` shows that the checks bite). Details of the other limits: `--perf-warn-only`, `src/VL.Qt.SmokeTest/PerfLimits.cs`.

## Known problems of the tests

| Problem | Cause | Workaround |
|---|---|---|
| The SmokeTest hangs at the end (WSLg, Mesa d3d12, Qualcomm arm64), exit code 124 after 10 minutes | the driver hangs when the process ends; exported apps are not affected (SIGTERM ends them in under a second) | `tools/test-linux.sh` stops the SmokeTest 30 s after its last line and takes the result from the log; `VLQT_GPU=software` avoids the hang (llvmpipe instead of the host GPU) |
| `--platform wayland` with d3d12 hangs the whole WSL VM | Mesa d3d12 with Wayland under WSLg | use `--platform xcb` (the default of an export), or `GALLIUM_DRIVER=llvmpipe`; `tools/test-linux.sh` refuses the combination |
| Under WSLg (x64 and arm64, xcb) the position part of four SmokeTest checks prints `SKIP ... position chosen by the WSLg window manager`: `233: created borderless at Bounds`, `143/234 live: Bounds not written while fullscreen`, `234 on create: off restores position + size`, `143/234 on create: Bounds after the round trip unchanged` | WSLg's window manager places the window itself: the position of a new window and the position after leaving fullscreen differ from the requested one (e.g. `X:0 Y:0` instead of `X:180 Y:140`); size and fullscreen itself are right and stay checked | none needed for patches that do not rely on the window position; the SmokeTest skips only these position checks, only under WSLg with xcb (`Program.PositionByWindowManager`) |
| Under WSLg's Wayland the SmokeTest reports ten failed checks (`Bounds` position, `Fullscreen` restore, `Borderless` position) | a Wayland client cannot choose its position, the compositor reports its own; after leaving fullscreen the size differs from the requested one (e.g. 494x267 instead of 500x300) | use xcb under WSLg; document, no fix possible in the client |
| Full `tools/test-linux.sh` runs (offscreen, llvmpipe, WSLg) failed about every second run in one random scenario: its grab showed only the background, a 100 x 100 corner of the frame with the rest transparent or torn, or the picture of an earlier grab | Qt's offscreen platform renders each window into one hidden X window per GL context (created 100 x 100), resizes it in `makeCurrent` and does nothing in `swapBuffers`. Mesa's software winsys (drisw, llvmpipe without a DRM device) reads the X window's size when the drawable is first validated and again only after a buffer swap, not at a bind. So the default framebuffer could keep 100 x 100 while the window was 800 x 600, and the grab read memory that was never written | fixed in QtNative: before a window grab on the offscreen platform with OpenGL, the window's context is made current and its drawable swapped once (`offscreen/offscreen_glx.cpp`), so the grab's frame has the window's size. Only grabs read pixels there. If it comes back: a `vlqt.grab: offscreen GLX framebuffer smaller than the window` warning in `smoke.log` names the grab |
