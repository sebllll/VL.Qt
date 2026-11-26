# Development

## Prerequisites

1. **Visual Studio 2026** (18.x; 2022 from 17.14 works too) with the *.NET desktop development* workload (.NET 10 SDK) and *Desktop development with C++* (MSVC)
2. **CMake 3.25+**
3. **Qt 6.12** (`tools/setup-qt.ps1` helps with the install)
4. **Git LFS** (test assets)

## First build

```powershell
tools/setup-qt.ps1                     # once: Qt 6.12 for the host architecture into C:\Qt (installs Python via winget if missing)
tools/build.ps1                        # QtNative + Qt deploy + C# library
tools/start-vvvv.ps1 -Help "Draw a Rectangle"
```

vvvv is taken from `-VvvvDir`, else `%VVVV_DIR%`, else the newest `C:\vvvv\vvvv_gamma_*-win-<arch>`. Only the Qt of your machine's architecture is needed locally; the other one is built by the CI.

To build QtNative from Visual Studio or Rider (open `src/VL.Qt.Native` as a CMake folder) or with `cmake --preset`, the presets `dev-win-x64` and `dev-win-arm64` read the Qt folder from the environment variable `VLQT_QT_DIR` (for example `C:\Qt\6.12.0\msvc2022_64`); `tools/setup-qt.ps1` prints the value at its end. Without a preset, CMake picks `win-x64` or `win-arm64` from the compiler's architecture. `tools/build-native.ps1` does not need the variable (`-QtDir` or `C:\Qt`).

## Visual Studio solution

Open `src/VL.Qt.slnx` in Visual Studio 2026 (or 2022 from 17.14) or Rider. It contains:

| Project | What it is |
|---|---|
| `VL.Qt.Native` | QtNative (C++); building it calls `tools/build-native.ps1` (CMake + Ninja, Qt deploy) |
| `VL.Qt` | the C# library (nodes, reconciler, interop); depends on `VL.Qt.Native` |
| `VL.Qt.Stride` | companion package for Stride (Windows only, references VL.Stride and VL.Qt); builds into the package folder `VL.Qt.Stride/lib/net10.0-windows7.0/`. VL.Qt never references it. |
| `VL.Qt.Audio` | companion package for VL.Audio (Windows only, references VL.Audio and VL.Qt); builds into the package folder `VL.Qt.Audio/lib/net10.0-windows7.0/`. VL.Qt never references it. See [video-audio.md](../video-audio.md). |
| `VL.Qt.Tests` | unit tests (NUnit), run them from the Test Explorer; they include the drift checks of `protocol.h` and `vlqt.h` against their C# mirrors and the node-API snapshot |
| `VL.Qt.Audio.Tests` | unit tests of the sync loop of `Video (VL.Audio)` with simulated clocks (Windows only) |
| `VL.Qt.SmokeTest` | NUnit project that drives QtNative without vvvv: windows, rendering, grabs, input, platform checks, performance limits; it covers the native side. Its assembly is also the helper executable of the scenarios that need a process of their own |
| Solution Items | `README.md`, `Directory.Build.props`, `VL.Qt.vl.targets`, the build scripts |

**Build** (`Ctrl+Shift+B`) builds QtNative first, then the C# projects. The QtNative build is incremental; the Qt deploy runs again only when QtNative or the deployed QML changed.

**F5 on `VL.Qt`** starts vvvv with this repo as source package. The build sets up the package repository for vvvv by itself (a junction under `%LOCALAPPDATA%\VL.Qt-dev`). Launch profiles (`src/VL.Qt/Properties/launchSettings.json`):

- `vvvv: HowTo Draw a Rectangle` — opens that help patch, C# debugging
- `vvvv: HowTo Draw a Rectangle (C# + C++)` — the same with native debugging, breakpoints in QtNative work too
- `vvvv (empty)` — vvvv without a patch

Edit the profile's patch path to start with another patch. After changes to QtNative, restart vvvv.

## Build and test step by step

```powershell
# Native C++ layer (QtNative)
tools/build-native.ps1

# C# assemblies + NuGet package
dotnet build src/VL.Qt -c Release

# Unit tests
dotnet test src/VL.Qt.Tests

# Smoke tests: QtNative without vvvv (rendering, performance); one scenario = one TestCategory
dotnet test src/VL.Qt.SmokeTest -c Release
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=rectangle

# Stride layer: the pure parts (pixel mapping, texture formats, layer composition, the texture bookkeeping of SharedTextureScene)
# are compiled into VL.Qt.Tests (src/VL.Qt.Tests/Stride/). The rest needs Stride's device and is a manual check: the three
# QtTexture patches in src/VL.Qt.Stride/help/manual-checks/ (README there), which no test runs. VL.Qt.Audio is the same:
# VideoAudioNode is a thin adapter over AudioTapPlayer (tested in VL.Qt.Audio.Tests), the adapter itself has no test.

# Compile-check a patch with vvvvc (exit 0 = no build errors)
tools/vvvvc.ps1 -File help/HelloWorld.vl
```

The SmokeTest also has scenarios that measure (poll cost, `QtTexture` render threads, pump spikes, tearing); how to run and read them is in [Profiling](profiling.md#smoketest-measurement-scenarios). Options of a run (stress duration, update of the reference images, perf limits) are NUnit run settings or environment variables `VLQT_SMOKE_<NAME>`, listed in `src/VL.Qt.SmokeTest/Framework/SmokeParameters.cs`. The drift checks are unit tests (`ProtocolDriftTests`, `NativeLayoutTests`, `ApiDriftTests`), see [Change the protocol](#typical-workflows).

For the Linux runtime see [Linux](linux.md#building-the-linux-runtime-from-source).

### Video (VL.Audio) tests

The sound of `Video (VL.Audio)` is described in [Video sound through VL.Audio](../video-audio.md).

- `src/VL.Qt.Audio.Tests`: the control loop with simulated clocks (drift, jitter), underrun, discontinuities, rate change and allocations per buffer, without VL.Audio or Qt.
- SmokeTest scenario `videoaudio` (`dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=videoaudio`): QtNative with `help/Qt/assets/qt-help-video-audio.mp4`, samples, rate, loop wrap, pause and source change, audio/frame consistency.
- Run the help patch from the repository with `tools/start-vvvv.ps1 -Patch "VL.Qt.Audio/help/HowTo Play a Video with VL.Audio.vl"`.
- Help patch check: `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File "VL.Qt.Audio/help/HowTo Play a Video with VL.Audio.vl"` (the script packs `VL.Qt` and `VL.Qt.Audio` into a local feed; VL.Audio is restored from NuGet).

## Scripts

Scripts live in `tools/`. All PowerShell scripts take `-Rid win-x64|win-arm64` where it matters (default: the machine's architecture).

**Everyday**

| Script | Use |
|---|---|
| `build.ps1` | Builds everything: QtNative + Qt deploy, then `dotnet build` of `src/VL.Qt`, `src/VL.Qt.Stride` and `src/VL.Qt.Audio`. `-Test` adds the source rules check, the unit tests (`VL.Qt.Tests`, `VL.Qt.Audio.Tests`) and the SmokeTest (`dotnet test src/VL.Qt.SmokeTest`; results, log and grabs in `build/smoke/`), `-Only <scenario,...>` limits the SmokeTest to those TestCategory values, `-SmokeTimeout <s>` sets its time limit (default 600 s), `-Patch <doc.vl>` compile-checks a patch with vvvvc. `-Rid`, `-Configuration Debug`, `-SkipNative` for C#-only changes, `-ForceDeploy`. Exit 0 = all steps passed, a summary lists each step. |
| `start-vvvv.ps1` | Starts vvvv with VL.Qt, VL.Qt.Stride and VL.Qt.Audio from this repo (three junctions in the package repository). `-Build` builds QtNative, VL.Qt and VL.Qt.Stride first (VL.Qt.Audio: `tools/build.ps1`), `-Help "Draw Text"` opens `help/Qt/HowTo Draw Text.vl`, `-Patch <file>` any patch, `-PrepareOnly` only sets up the package repository, `-RepoDir` another checkout. |
| `vvvvc.ps1` | Compiles or exports a patch with vvvvc (vvvv's command-line compiler). `-File <doc.vl>` (default `help/HelloWorld.vl`, a test patch of the repo that is not in the NuGet package; use a `help/Qt/` patch with a package), `-Out <dir>` (default `build/vvvvc`), further arguments go to vvvvc. Exit 0 = no build errors. A patch in `VL.Qt.Stride/` or `VL.Qt.Audio/`, or one that depends on one of them (no feed has them before the release), also gets the dev package repository, `--editable-packages` and a local feed `build/vvvvc-feed` with `VL.Qt` and that package packed as version 0.0.1 (repacked when the build output is newer; run `tools/build.ps1` first). `VL.Audio` is not part of vvvv: the export restores it from NuGet (the version pinned in `VL.Qt.Audio.vl` and the patch, 1.9.21; offline from the NuGet cache). |
| `start-vvvv-with-nupkg.ps1` / `.cmd` | Starts vvvv with a packed `VL.Qt.<version>.nupkg` without installing it, e.g. to test a release; the `VL.Qt.Stride.<version>.nupkg` of the same version next to it is unpacked too (VL.Qt.Audio is not). Takes the newest nupkg next to the script or from `-NupkgDir`; `-Help`, `-Patch` like above. The `.cmd` is for double-clicking. |

**Native build and Qt**

| Script | Use |
|---|---|
| `setup-qt.ps1` | Installs the pinned Qt version (LGPL modules only, plus Qt Multimedia and Shader Tools) via aqtinstall. `-Rid`, `-OutDir` (default `C:\Qt`), `-DebugSymbols` adds the Qt PDBs for profiling and debugging (several GB). Prints the value for `VLQT_QT_DIR`. |
| `build-native.ps1` | Builds only QtNative and deploys Qt into `runtimes/<rid>/native` (windeployqt). `-QtDir` for another Qt location, `-ForceDeploy` to redeploy. |
| `trim-deploy.ps1` | Removes Qt parts VL.Qt never loads from `runtimes/<rid>/native`. Idempotent, `-WhatIf` shows what would go. |
| `check-licenses.ps1` | Checks that the deployed runtime contains only LGPL-compatible Qt parts (whitelist). Exit 1 on a violation. |
| `test-nupkg-consumer.ps1` | Consumes a packed nupkg like a vvvv export does and checks the published layout. `-Package <nupkg>`. On Windows RIDs it also consumes `VL.Qt.Stride`: its nupkg of the same version must lie next to it and contain `VL.Qt.Stride.vl`, otherwise the test fails. |

**Linux (run in WSL or on Linux)**

| Script | Use |
|---|---|
| `setup-wsl.sh` | Prepares Ubuntu 24.04 / WSL2: build tools, Qt's runtime libraries, .NET 10 SDK in `~/.dotnet`. `--no-apt`, `--no-dotnet`. |
| `setup-qt.sh` | Installs Qt 6.12 for Linux into `~/Qt` (or `$VLQT_QT_ROOT`). |
| `build-native.sh` | Builds `libvlqt.so` into `runtimes/<rid>/native`. `--sanitize asan\|tsan` for a sanitizer build. |
| `deploy-linux.sh` | Bundles the Qt runtime next to `libvlqt.so`, sets RUNPATH, trims (`trim-linux.sh`) and checks licenses (`check-licenses.sh`). Run after every `build-native.sh`. |
| `test-linux.sh` | Builds and runs everything: QtNative + bundle, SmokeTest, unit tests. `--platform offscreen\|wayland\|xcb\|auto`, `--software` without GPU/X server, `--skip-native`, `--no-smoke`, `--no-unit`, `--only <scenario,...>` (TestCategory filter), `--sanitize asan\|tsan`. `tools/smoke-summary.py <dir>` prints the failures of a run. |
| `check-wslg.sh` | Checks Qt under WSLg with a small QML scene, offscreen and as a window; `--gpu` uses the host GPU via d3d12. |

**Profiling**

| Script | Use |
|---|---|
| `profile.ps1`, `profile-summary.py` | CPU profiling with Ultra, see [Profiling](profiling.md). |
| `perf-baseline.ps1` | Runs the SmokeTest perf scenarios several times, writes the medians as CSV and Markdown and compares a new run with a stored baseline (`-Compare`). |

**Repo maintenance**

| Script | Use |
|---|---|
| `fix-encoding.py` | Checks/repairs the BOM: files, `--all`, `--staged` (used by the git hook), `--check` to only report. See [Encoding and git hook](#encoding-and-git-hook). |
| `vl-patch-check.py` | Read-only check of the VL.Qt nodes in `.vl` files against their pins in `src/VL.Qt.Tests/Api/node-api.txt` (`vl_node_api.py`): hidden pins with a value, IOBoxes on `Interaction`, links to removed pins (errors), IOBoxes at the default value (hints). No argument: all `help/Qt/*.vl`, `help/*.vl`, `VL.Qt.Stride/help/*.vl` and `VL.Qt.Audio/help/*.vl`. `--fix` makes hidden pins with a value visible, `--accept <patch>` puts intended exceptions into `help/.padlint.json`. Test: `test-vl-patch-check.py`. |
| `vl-patch-migrate.py` | Applies the rows of `vl-patch-mapping.txt` (renamed nodes and pins, moved categories) to every patch of the repository; `--check` lists what would change. Regression test: `test-vl-patch-migrate.py`. |
| `record-traces.ps1` | Records command traces from help patches into `src/VL.Qt.SmokeTest/traces/`; the SmokeTest replays them against QtNative and compares the last frame with a reference. Re-record after every protocol change. |
| `check-source-rules.py` | Closing check of the refactoring, part of the gate (`build.ps1 -Test`, `test-linux.sh`): no issue reference (`#<number>`) in `src`, no comment line over 160 characters, no C# file with two top-level types besides the named exceptions in the script (`--list` prints them with their reasons). Read-only; test: `test-source-rules.py`. |

## Packages

The repository builds three NuGet packages:

| Package | Folder | Content |
|---|---|---|
| `VL.Qt` | repo root (`VL.Qt.vl`, `help/`, `src/VL.Qt/`) | the Qt layer and the Qt runtime for Windows and Linux; platform-neutral, no Stride |
| `VL.Qt.Stride` | `VL.Qt.Stride/` (package folder: `VL.Qt.Stride.vl`, `help/`, build output `lib/net10.0-windows7.0/`); C# source in `src/VL.Qt.Stride/` | Qt scenes rendered into Stride textures, Windows only; depends on `VL.Qt` |
| `VL.Qt.Audio` | `VL.Qt.Audio/` (package folder: `VL.Qt.Audio.vl`, `help/`, `README.md`, build output `lib/net10.0-windows7.0/`); C# source in `src/VL.Qt.Audio/` | `Video (VL.Audio)`: the sound of a video as VL.Audio signals, Windows only; depends on `VL.Qt` (same version) and `VL.Audio`, see [video-audio.md](../video-audio.md) |

`VL.Qt` never references `VL.Qt.Stride` or `VL.Qt.Audio`. `VL.Qt.Stride.vl` (like `VL.Qt.Audio.vl`) is a minimal package document (dependencies and the assembly, no nodes); its help patches may be written in vvvv or directly (see `AGENTS.md`, "Checking patches"); `lib/` is not tracked by git. Why this layout: [design/stride-output.md](../design/stride-output.md#package-layout).

```powershell
dotnet build src/VL.Qt.Stride -c Release                  # DLL into VL.Qt.Stride/lib/net10.0-windows7.0/
dotnet pack src/VL.Qt.Stride -c Release -p:Version=1.2.3  # VL.Qt.Stride.1.2.3.nupkg
```

**All three packages have the same version**: `VersionPrefix` in `Directory.Build.props` for local builds, one `-p:Version` for all in the CI. `VL.Qt.Stride` and `VL.Qt.Audio` declare their dependency on `VL.Qt` with the version of the project reference, so packing one of them with another `-p:Version` than `VL.Qt` points it at a `VL.Qt` that was not released with it. Packing `VL.Qt` needs the native Qt trees first, see the comment in `src/VL.Qt/VL.Qt.csproj`.

The CI (`build.yml`) builds `VL.Qt.Stride` and `VL.Qt.Audio` on the Windows runners and packs them in the job `pack` on every run, so a broken pack shows up on a pull request. The steps "Verify package (VL.Qt.Stride)" and "Verify package (VL.Qt.Audio)" check the layout and the nuspec (id, version, dependencies: `VL.Qt` at the same version, for Audio also `VL.Audio`). A tag or a plain `workflow_dispatch` uploads all three `.nupkg` files; the release attaches them to the GitHub release and pushes them to nuget.org (see [Release](../maintaining/release.md#release)).

For vvvv, `tools/start-vvvv.ps1` puts the package folders into its package repository as junctions (`VL.Qt`, `VL.Qt.Stride`, `VL.Qt.Audio`), so all are source packages in the dev workflow.

## Encoding and git hook

Line endings are normalized by `.gitattributes` (LF in the repository; `.vl` and `help/**/*.props` are checked out with CRLF because vvvv saves them that way). The BOM rule follows VL.StandardLibs: UTF-8 **with** BOM for `.cs` and `.vl`, **without** BOM for everything else (`.editorconfig`). `help/*.props` are written by vvvv and left alone.

A git hook keeps the BOM right at every commit. It runs `tools/fix-encoding.py --staged`, which repairs the staged files and stages the fix. If a repaired file also has unstaged changes, the hook stops and tells you to stage it again. Enable it once per clone:

```sh
git config core.hooksPath .githooks
```

The hook needs Python on the `PATH` and skips the check with a notice if there is none. To repair or check everything by hand:

```sh
python tools/fix-encoding.py --all             # repair all tracked files
python tools/fix-encoding.py --check --all     # only report, exit 1 on a wrong BOM
```

If a diff shows whole files as changed, the cause is line endings or BOM: run `git add --renormalize .` or the script, do not rewrite the content.

## Project layout

One line per folder; files are found by the folder. Why the layers are cut this way: [Architecture](architecture.md).

```
src/VL.Qt/                  # the C# library (package VL.Qt)
  Runtime/                  # QtApp, the pump, logging, NodeErrors, the exported-app support
  Scene/                    # scenes of windows and offscreen targets, geometry and space, input mapping, resync
  Window/                   # window placement and settings, PerfStats and the other performance records
  Interop/                  # P/Invoke declarations of vlqt.h (Native.*.cs), the ABI check, the font and resource APIs
  Wire/                     # the protocol to QtNative: Protocol.cs (mirror of protocol.h), CommandWriter, EventReader
  Reconciliation/           # scene diffing: the Reconciler walks the tree, PropertyDiff writes the SETs, ItemMirror is what QtNative holds
  Elements/                 # the element descriptions (Rectangle, Text, ...); Effects/, Feedback/, Shaders/, Qml/ and Enums/ share the namespace
  Styles/                   # style values, merging and the stylable elements
  Images/                   # images, resources, the resource client and the image file writer
  Paths/                    # QtPath and the geometry of the path generators
  Qml/                      # QML file parser and the node factory for user .qml files
  Shaders/                  # shader files, reflection and the pins of shader nodes
  NodeFactories/            # factories that make nodes from files in a folder
  Nodes/                    # the vvvv nodes, one subfolder per category (Elements/, Widgets/, Effects/, ...)
  buildTransitive/          # MSBuild targets that reach consumers of the package
src/VL.Qt.Native/           # QtNative (C++), builds vlqt.dll / libvlqt.so
  vlqt.h, vlqt_test.h       # the C API, and the test exports that are in every build
  protocol.h                # shared type, property and event IDs
  app/                      # app lifecycle, pump, logging, last error, fonts, animation clock, render gate
  platform/                 # platform code for Windows and Linux
  window/                   # windows and their exports, event filters, focus, touch input
  scene/                    # the scene of a window: object table, command decoder, handlers, measuring, events
  elements/                 # element types (type_*.cpp), their table, geometry, the Group frame, shape paths
  effects/                  # effect chain, Shader item and its material, layer requests
  user_qml/                 # QmlElement: loads a user .qml file, watches it, wire values
  style/                    # VlStyle, the attached style of the Controls, and the base style VLQt/*.qml
  resources/                # resource manager, pixel formats, the Image element, the audio tap
  device/                   # the shared D3D11 device, device loss, the context lock, the Qt render threads
  offscreen/                # QtTexture scenes: binding, rendering, input, exports, linear pass
  diagnostics/              # perf monitor, F2/F5 overlays, debug overlay
  test/                     # the test exports of vlqt_test.h
  testkit/                  # vlqt_testkit, the stand-in D3D11 device of the SmokeTest (not packed)
  qml/                      # QML of the built-in elements, handlers, effect chains and the perf overlay
  shaders/                  # the built-in effect shaders (.frag)
  tools/                    # glprobe, the OpenGL check under WSLg (Linux)
src/VL.Qt.Stride/           # C# source of the package VL.Qt.Stride (QtTexture, QtRenderer; flat); manual-check patches in help/manual-checks/
src/VL.Qt.Audio/            # C# source of the package VL.Qt.Audio (Video (VL.Audio); flat)
src/VL.Qt.Tests/            # unit tests; folders mirror src/VL.Qt/, shared fakes and recorders in Support/, the node-API snapshot in Api/
src/VL.Qt.Audio.Tests/      # unit tests of VL.Qt.Audio
src/VL.Qt.SmokeTest/        # NUnit scenarios against QtNative without vvvv; Framework/, reference images, traces, assets
src/VL.Qt.TestSupport/      # sources shared by the test projects: the test exports of QtNative and their constants
VL.Qt.Stride/, VL.Qt.Audio/ # package folders: the .vl document, help/, README.md, lib/ (build output, not tracked)
help/                       # help patches of VL.Qt (help/Qt/ one HowTo per element) and Help.xml
docs/                       # documentation; design/ holds the design notes, research/ the measurements
tools/                      # build, test, patch-check, profiling and release scripts
```

## Typical workflows

**Change a patch or C# node:** edit, run vvvv with editable packages, hot-reload on save.

**Change Qt rendering behavior:** edit `src/VL.Qt.Native/`, run `tools/build-native.ps1`, restart vvvv.

**Change the protocol:**
1. Edit `src/VL.Qt.Native/protocol.h` (type or property IDs)
2. Mirror the change in `src/VL.Qt/Wire/Protocol.cs` by hand (`VLQT_<GROUP>_<NAME>` is `Protocol.<Group>.<Name>`; a group name ending in `Id` or `Flags` drops that word in C: `Protocol.EventId.Hover` is `VLQT_EVENT_HOVER`, `Protocol.ImageLoadFlags.Mipmaps` is `VLQT_IMAGE_LOAD_MIPMAPS`)
3. Bump `VLQT_ABI_VERSION` and `Protocol.AbiVersion` (every change to a constant, a layout or an exported signature; `QtApp` refuses a `vlqt.dll` with another number)
4. Run `dotnet test src/VL.Qt.Tests --filter "FullyQualifiedName~DriftTests|FullyQualifiedName~NativeLayoutTests"` to check that header and C# agree: constants and fixed wire sizes of `protocol.h` and the constants of `vlqt_test.h` (`ProtocolDriftTests`, against `Protocol.cs` and `TestProtocol.cs`), struct layouts of `vlqt.h` (`NativeLayoutTests`, numbers also in the `static_assert`s of `app/app.cpp`) and the `VLQT_API` exports of `vlqt.h`, `vlqt_test.h` and `testkit/testkit.h` against the `[DllImport]`s of `Native` and `NativeTest` (`ApiDriftTests`)
5. Implement both sides (`elements/`, `Elements/`, `Reconciliation/`)

**Add a new element type:** follow the walkthrough [Add an element](add-an-element.md): protocol entry, element record, diff, node, QtNative item, feedback, tests and help patch. The conventions of the node are in [Adding an element node](architecture.md#adding-an-element-node).

See [Architecture](architecture.md) for how the pieces fit together.
