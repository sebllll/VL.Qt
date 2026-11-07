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
- The window runs with vsync at the display refresh rate. Per-frame values assume that rate.
- vvvv's main loop timer busy-waits (`VL.Core.PreciseMainLoopTimer`) and shows up as constant CPU load in every scenario. That is vvvv, not VL.Qt; look at the other threads.
- Runs on different machines, refresh rates or vvvv/Qt versions are not directly comparable. Compare runs from the same machine.
- Do not launch the program through Ultra (`ultra profile -- <exe>`): the window is then never shown, Qt renders nothing, and the profile misses the whole render side.
- "GUI thread" is vvvv's update, the Qt events, the scene graph sync and the render (VL.Qt uses Qt's `basic` render loop on that thread). Sync and render are split by function name and don't add up to the whole thread.
