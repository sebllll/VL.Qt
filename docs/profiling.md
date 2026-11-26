# Profiling

A repeatable CPU baseline: the same test patches, exported the same way and recorded with the sampling profiler [Ultra](https://github.com/xoofx/ultra) (managed + native + kernel stacks, via ETW). Use it to spot performance regressions (compare a new run with an earlier one) and to compare VL.Qt with other renderers, such as VL.Skia, on the same scenario.

For the overlays and the in-app numbers see [Diagnostics](diagnostics.md).

## Scenarios

The scenarios live in `tools/profiling/`, one patch each, window 1280×720 DIP, time-based deterministic animation. Details: [tools/profiling/README.md](../tools/profiling/README.md).

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
- The window runs with VSync at the display refresh rate (the default of the `VSync` pin). Per-frame values assume that rate; a frame can wait for the refresh without using CPU (F5 segment "vsync wait").
- vvvv's main loop timer busy-waits (`VL.Core.PreciseMainLoopTimer`) and shows up as constant CPU load in every scenario. That is vvvv, not VL.Qt; look at the other threads.
- Runs on different machines, refresh rates or vvvv/Qt versions are not directly comparable. Compare runs from the same machine.
- Do not launch the program through Ultra (`ultra profile -- <exe>`): the window is then never shown, Qt renders nothing, and the profile misses the whole render side.
- "GUI thread" is vvvv's update, the Qt events, the scene graph sync and the render (VL.Qt uses Qt's `basic` render loop on that thread). Sync and render are split by function name and don't add up to the whole thread.

## SmokeTest measurement scenarios

The SmokeTest (`src/VL.Qt.SmokeTest`, NUnit) drives QtNative without vvvv and has scenarios that measure. Run one by its category, as a filter of `dotnet test` or with `tools/build.ps1 -Test -Only <scenario>`. Options come from the run settings or from environment variables `VLQT_SMOKE_<NAME>` (`src/VL.Qt.SmokeTest/Framework/SmokeParameters.cs`). Logs and grabs land in `build/smoke/`.

| Scenario | What it measures | Run |
|---|---|---|
| `measure` | the cost of the poll that reads `Measured Size` and `Scene Bounds`: a static scene measures nothing and polishes nothing | `tools/build.ps1 -Test -Only measure` |
| `qttextureperf` | what `QtTexture` scenes cost the vvvv main thread on a render thread and on the main thread (about a minute, own process, explicit) | `tools/build.ps1 -Test -Only qttextureperf`; `VLQT_SMOKE_RENDERTHREADS=0` runs only the main-thread mode |
| `replay-cli` | replays a command trace (or the built-in `anim` scene) like the vvvv main loop and attributes pump spikes to a phase (explicit) | parameters `ReplayTrace`, `ReplayArgs` |
| `tearing` | N windows with a fast sweeping bar, to see tearing and present times (explicit) | parameter `TearingArgs` |

Timing checks of the SmokeTest tell a busy machine from a regression: a check that reads peaks or long frames prints `SKIP ... host too loaded` instead of failing when the loop of the test itself stalled. Such a SKIP is not a pass: repeat the run on a quieter machine. `tools/perf-baseline.ps1` runs the perf scenarios several times and compares medians with a stored baseline.

The numbers of these scenarios, with machine, Qt version and date, are in [research/](research/): [poll measurements](research/poll-measurements.md) (a static scene polls in 0.0001 ms at 2000 elements), [render texture measurements](research/render-texture-measurements.md) (a render thread cuts the main-thread time per `QtTexture` from 0.7 to 2.8 ms to under 0.35 ms; more threads only help several heavy scenes and cost CPU) and [pump spikes](research/pump-spikes.md).

### Tearing test pattern

A manual scenario, not part of the normal run: N windows, each with a vertical bar sweeping fast across the whole width, so tearing shows as a broken bar. It pumps like the vvvv main loop and prints numbers once per second into the test output.

```powershell
$env:VLQT_SMOKE_TEARINGARGS = '--windows 2 --interval 0 --seconds 20'
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=tearing --logger "console;verbosity=detailed"
```

| Argument | Default | Meaning |
| --- | --- | --- |
| `--windows N` | 1 | Number of windows (1 to 8), side by side. |
| `--interval 0\|1` | 1 | Swap interval: 0 = Present never waits, 1 = VSync. |
| `--fullscreen` | off | Borderless fullscreen; window i on screen i if there is one (Windows), else all on the primary screen. |
| `--pump-hz N` | 60 | Pump rate (like the vvvv main loop); 0 = as fast as possible. |
| `--seconds N` | 20 | Duration; 0 = until a window is closed or Ctrl+C. |

Per second and window: `fps` (swaps/s), `present` and `pump` p50/p99 in ms (from `vlqt_perf_sample` of that second's pumps), the refresh rate of the window's screen (`n/a` if unknown), and once per second the `hostpump` p50/p99 (apply, advance and process events of all windows). The first second is warm-up. At the end one line per window over the run without the warm-up:

```
TEARING SUMMARY win=0 windows=2 interval=1 fullscreen=0 fps=59.9 present_p50=0.36 present_p99=0.73 pump_p50=0.70 pump_p99=1.24 hostpump_p50=0.76 hostpump_p99=1.33 refresh=60.0 display=ok
```

`pump` is the whole `vlqt_app_process_events_for`, so it is the same for every window of one run. On Windows the display is kept awake during the run (`SetThreadExecutionState`) and a locked session is detected (`display=ok|locked|unknown`). `TEARING WARNING` lines say when the numbers cannot be trusted: fps about 0, refresh `n/a`, or a locked session. A bad argument fails the test.

## Thread names

QtNative names its threads (`vlqt-gui`, `vlqt-res-0` …, `vlqt-render-0` …), so they are easy to find in a profile, a thread dump or the debugger. The names and what each thread runs are in the Threads section of [Architecture](architecture.md#threads). Windows shows them as the thread description (WinDbg and cdb, Visual Studio, ETW-based profilers), Linux as the `comm` of the thread (`ps -L`, `top -H`, gdb `info threads`). Qt's own threads keep their names (`QQmlThread`, `QFFmpeg::StreamDecoder0` …).
