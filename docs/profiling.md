# Profiling

A repeatable CPU baseline: the same test patches, exported the same way and recorded with the sampling profiler [Ultra](https://github.com/xoofx/ultra) (managed + native + kernel stacks, via ETW). Use it to spot performance regressions (compare a new run with an earlier one) and to compare VL.Qt with other renderers, such as VL.Skia, on the same scenario.

For the overlays and the in-app numbers see [Diagnostics](diagnostics.md).

## Scenarios

The scenarios live in `tests/profiling/`, one patch each, window 1280×720 DIP, time-based deterministic animation. Details: [tests/profiling/README.md](../tests/profiling/README.md).

| Scenario | Patch | Content |
|---|---|---|
| `Idle` | `Profile01_Idle.vl` | empty `Renderer [Qt]` |
| `Elements500` / `Elements2000` | `Profile02_Elements.vl` | 500 / 2000 animated `Rectangle` (`VLQT_PROFILE_COUNT`) |
| `Typo` | `Profile03_Typo.vl` | 200 `Text`, content changes every frame |
| `Images` | `Profile04_Images.vl` | 6 × `LoadImage (Async)`, 1920×1080 JPEGs, new file every 0.1 s |
| `Video` | `Profile05_Video.vl` | `Video`, looped MP4 |
| `Effects` | `Profile06_Effects.vl` | 20 animated elements with Blur + DropShadow |

## Run it

Windows, PowerShell 7, .NET 10 SDK for `dnx`:

```powershell
tools/setup-qt.ps1 -DebugSymbols     # once: Qt PDBs, otherwise Qt frames stay unnamed
tools/profile.ps1 -Scenario all      # or e.g. -Scenario Elements2000,Typo; -WarmUp 10 -Duration 30 are the defaults
```

`profile.ps1` builds VL.Qt (Release), exports each patch with vvvvc as a window program (`--output-type WinExe`), then asks **once** for administrator rights (UAC) and records every scenario in turn: it starts the program itself, waits for the warm-up, attaches `ultra profile --pid` for the duration and ends the program. Output goes to `%LOCALAPPDATA%\VL.Qt\profiling\<yyMMdd_HHmm>_<rid>\`: one profile per scenario (`win-<arch>_<Scenario>.json.gz`, open it at [profiler.firefox.com](https://profiler.firefox.com/)), Ultra's text report and `manifest.json` (commit, CPU, vvvv and Qt version, refresh rate). Don't touch the machine while it records.

## Evaluate and compare

```powershell
python tools/profile-summary.py <profile.json.gz>                 # CPU per thread and layer, top functions, P/Invoke boundary
python tools/profile-summary.py --json <profile.json.gz> > X.summary.json
python tools/profile-summary.py --compare <A> <B>                 # A/B: .json.gz or .summary.json; Δ per layer
```

The layers are VL.Qt managed, vvvv/VL, .NET runtime/GC, `vlqt.dll` (native layer), Qt, GPU driver/D3D, kernel. The exclusive (leaf) values are the meaningful ones. Keep the `.summary.json` of a run as your baseline and compare later runs against it with `--compare`.

To compare with another renderer, build the same scenario as a patch with the other library (same window size, same animation, same count) and record it with `tools/profile.ps1 -Scenario <name> -PatchOverride <patch.vl>`.

## Read the numbers with care

- It measures **CPU only**. GPU time is not in the profile (see `GPU` in the performance overlay).
- The window runs with VSync at the display refresh rate (the default of the `VSync` pin). Per-frame values assume that rate; a present can wait for the refresh without using CPU.
- vvvv's main loop timer busy-waits (`VL.Core.PreciseMainLoopTimer`) and shows up as constant CPU load in every scenario. That is vvvv, not VL.Qt; look at the other threads.
- Runs on different machines, refresh rates or vvvv/Qt versions are not directly comparable. Compare runs from the same machine.
- Do not launch the program through Ultra (`ultra profile -- <exe>`): the window is then never shown, Qt renders nothing, and the profile misses the whole render side.
- "GUI thread" is vvvv's update, the Qt events, the scene graph sync and the render (VL.Qt uses Qt's `basic` render loop on that thread). Sync and render are split by function name and don't add up to the whole thread.

## Measurement path (#20)

`Measured Size` and `Scene Bounds` of every bound element come from the poll (`collectMeasurements`, `src/VL.Qt.Native/events.cpp`). Since #20 it reads the geometry only of elements whose geometry changed since the last poll (x/y, size, implicit size, parent, Matrix4x4, anchor, Group frame) and of everything below them; a static scene reads none. Media, error, clicks and user values are read only for element types that have them.

Measured without a profiler by the SmokeTest (`tools/build.ps1 -Test -Only measure`, lines `INFO 20` in `build/smoke/stdout.log`): N bound `Rectangle` in one Group, median of 201 polls, Stopwatch around `vlqt_window_poll`, items measured per poll from `vlqt_test_measure_count`. win-x64, Release.

| Scene | Before: poll | Before: measured/poll | After: poll | After: measured/poll |
|---|---|---|---|---|
| 500 static | 0.044 ms | 500 | 0.004 ms | 0 |
| 2000 static | 0.183 ms | 2000 | 0.017 ms | 0 |
| 500, one moving | 0.042 ms | 500 | 0.001 ms | 1 |
| 2000, one moving | 0.186 ms | 2000 | 0.002 ms | 1 |

What remained in the static rows after #20 was not the measurement: when Qt did not render in a pump (nothing changed), the poll first ran `Scene::polish` over all elements (#215), which is linear in the element count. In the "one moving" rows Qt rendered, so the poll skips it. A change of an ancestor still measures its whole subtree, as it must (all of it moved).

Since #71 `Scene::polish` returns at once when nothing changed since the last poll: no apply, no marked geometry, no window geometry change and no polish request pending in the window (`itemsToPolish`, where Row/Column, Text implicit size and Group frames register until Qt polishes them in a render). Same measurement, static scenes only (the "one moving" rows did not run it before either):

| Scene | Before #71: poll | After #71: poll |
|---|---|---|
| 500 static | 0.0039 ms | 0.0001 ms |
| 2000 static | 0.0171 ms | 0.0001 ms |

The SmokeTest `measure` checks that a poll without a render right after an apply still reports the new layout (Button text in a Row, Column spacing, Text measure) and that the next render agrees with it.

## Thread names

QtNative names its threads, so they are easy to find in a profile, a thread dump or the debugger (Windows: thread description, Linux: thread name; the same names, at most 15 characters):

| Name | Thread |
|---|---|
| `vlqt-gui` | The Qt GUI thread: the thread that creates the `QGuiApplication` (`vlqt_app_init`), i.e. the thread of the host (the vvvv main thread, the main thread of the SmokeTest). It is named only while it has no name yet, a name the host gave it stays. |
| `vlqt-res-0` … `vlqt-res-3` | The decode workers of the resource manager (images, shaders; their number is `VLQT_RES_WORKERS`, at most 16). |

Qt's own threads keep their names (`QQmlThread`, `QFFmpeg::StreamDecoder0` …). Tools that read thread descriptions (WinDbg and cdb, Visual Studio, ETW-based profilers) show the names instead of "Thread (id)"; on Linux they are the `comm` of the thread (`ps -L`, `top -H`, gdb `info threads`).
