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

## Visual Studio solution

Open `src/VL.Qt.slnx` in Visual Studio 2026 (or 2022 from 17.14) or Rider. It contains:

| Project | What it is |
|---|---|
| `VL.Qt.Native` | QtNative (C++); building it calls `tools/build-native.ps1` (CMake + Ninja, Qt deploy) |
| `VL.Qt` | the C# library (nodes, reconciler, interop); depends on `VL.Qt.Native` |
| `VL.Qt.Tests` | unit tests (NUnit), run them from the Test Explorer |
| `VL.Qt.SmokeTest` | drives QtNative without vvvv: rendering, grabs, performance limits, protocol drift |
| `VL.Qt.Probe` | opens one Qt window from C# without vvvv and grabs a PNG; for platform and loader checks |
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

# Smoke tests: QtNative without vvvv (rendering, performance, protocol drift)
dotnet run --project src/VL.Qt.SmokeTest -c Release -- --all

# Compile-check a patch with vvvvc (exit 0 = no build errors)
tools/vvvvc.ps1 -File help/HelloWorld.vl
```

For the Linux runtime see [Linux](linux.md#building-the-linux-runtime-from-source).

## Scripts

Scripts live in `tools/`. All PowerShell scripts take `-Rid win-x64|win-arm64` where it matters (default: the machine's architecture).

**Everyday**

| Script | Use |
|---|---|
| `build.ps1` | Builds everything: QtNative + Qt deploy, then `dotnet build src/VL.Qt`. `-Test` adds unit tests and the SmokeTest (log and grabs in `build/smoke/`), `-Only <scenario,...>` limits the SmokeTest, `-Patch <doc.vl>` compile-checks a patch with vvvvc. `-SkipNative` for C#-only changes, `-Configuration Debug`, `-ForceDeploy`. Exit 0 = all steps passed, a summary lists each step. |
| `start-vvvv.ps1` | Starts vvvv with VL.Qt from this repo. `-Build` builds first, `-Help "Draw Text"` opens `help/Qt/HowTo Draw Text.vl`, `-Patch <file>` any patch, `-PrepareOnly` only sets up the package repository. |
| `vvvvc.ps1` | Compiles or exports a patch with vvvvc (vvvv's command-line compiler). `-File <doc.vl>` (default `help/HelloWorld.vl`), `-Out <dir>` (default `build/vvvvc`), further arguments go to vvvvc. Exit 0 = no build errors. |
| `start-vvvv-with-nupkg.ps1` / `.cmd` | Starts vvvv with a packed `VL.Qt.*.nupkg` without installing it, e.g. to test a release. Takes the newest nupkg next to the script or from `-NupkgDir`; `-Help`, `-Patch` like above. The `.cmd` is for double-clicking. |

**Native build and Qt**

| Script | Use |
|---|---|
| `setup-qt.ps1` | Installs the pinned Qt version (LGPL modules only, plus Qt Multimedia and Shader Tools) via aqtinstall. `-OutDir` (default `C:\Qt`). |
| `build-native.ps1` | Builds only QtNative and deploys Qt into `runtimes/<rid>/native` (windeployqt). `-QtDir` for another Qt location, `-ForceDeploy` to redeploy. |
| `trim-deploy.ps1` | Removes Qt parts VL.Qt never loads from `runtimes/<rid>/native`. Idempotent, `-WhatIf` shows what would go. |
| `check-licenses.ps1` | Checks that the deployed runtime contains only LGPL-compatible Qt parts (whitelist). Exit 1 on a violation. |
| `test-nupkg-consumer.ps1` | Consumes a packed nupkg like a vvvv export does and checks the published layout. `-Package <nupkg>`. |

**Linux (run in WSL or on Linux)**

| Script | Use |
|---|---|
| `setup-wsl.sh` | Prepares Ubuntu 24.04 / WSL2: build tools, Qt's runtime libraries, .NET 10 SDK in `~/.dotnet`. `--no-apt`, `--no-dotnet`. |
| `setup-qt.sh` | Installs Qt 6.12 for Linux into `~/Qt` (or `$VLQT_QT_ROOT`). |
| `build-native.sh` | Builds `libvlqt.so` into `runtimes/<rid>/native`. `--sanitize asan\|tsan` for a sanitizer build. |
| `deploy-linux.sh` | Bundles the Qt runtime next to `libvlqt.so`, sets RUNPATH, trims (`trim-linux.sh`) and checks licenses (`check-licenses.sh`). Run after every `build-native.sh`. |
| `test-linux.sh` | Builds and runs everything: QtNative + bundle, SmokeTest, unit tests. `--platform offscreen\|wayland\|xcb\|auto`, `--software` without GPU/X server, `--skip-native`. |
| `check-wslg.sh` | Checks Qt under WSLg with a small QML scene, offscreen and as a window; `--gpu` uses the host GPU via d3d12. |

**Profiling**

| Script | Use |
|---|---|
| `profile.ps1`, `profile-summary.py` | CPU profiling with Ultra, see [Profiling](profiling.md). |

**Repo maintenance**

| Script | Use |
|---|---|
| `fix-encoding.py` | Checks/repairs the BOM: files, `--all`, `--staged` (used by the git hook), `--check` to only report. See [Encoding and git hook](#encoding-and-git-hook). |
| `vl-pad-layout.py` | Lays out IOBoxes around Qt nodes in `.vl` files (`--check` first on hand-edited patches). |

## CI

The workflows are in `.github/workflows/` (`build.yml`, `public-check.yml`; `sanitizers.yml` runs by hand). Every action is pinned to a full commit SHA with the version as a comment, Dependabot keeps the pins current.

The repository `sebllll/VL.Qt` differs from forks and private copies in a few places. What costs minutes or publishes something is keyed on the repository being public (`github.event.repository.private == false`), so it stays off while the repository is private; the CLA and contributor workflows also check the name `sebllll/VL.Qt` (GitHub compares it case-insensitively):

- The hosted runners are free for public repositories, so the `win-arm64` and `linux-arm64` entries also run on pull requests. Elsewhere they run only on tags and on a plain `workflow_dispatch`.
- The workflow `public-check.yml` (keyed on the name only, so it runs while the repository is private too) fails on every pull request and push to `develop` and `main` if a file of a maintainer-only working directory (plans, internal notes, local agent settings) is tracked. Its job is a required status check.
- The `release` job runs in the GitHub environment `release`. If the repository is public and the environment secret `NUGET_API_KEY` is set, it also pushes the `.nupkg` to nuget.org; without the secret that step is skipped.
- Pull requests use `pull_request` (not `pull_request_target`); their jobs get no secrets and only `contents: read`.

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

```
src/VL.Qt/                  # C# library
  Elements/                 # element types (Rectangle, Text, ..., SinkRegistry)
  Nodes/                    # vvvv node wrappers
  Reconciler/               # scene diffing and sync
  CommandWriter.cs          # binary command encoder
  EventReader.cs            # event buffer parser
  Protocol.cs               # IDs mirrored from protocol.h
src/VL.Qt.Native/           # QtNative (C++)
  protocol.h                # shared type/property/event IDs
  vlqt.h/cpp                # C API (app, window, rendering)
  commands.h/cpp            # command processing
  elements.h/cpp            # element implementations
  events.h/cpp              # event polling
  device.h/cpp              # shared D3D11 device, device loss
  resources.h/cpp, *_resource.h/cpp  # images, shaders, snapshots
  perf.h/cpp                # perf counters/overlays
src/VL.Qt.Tests/            # C# unit tests
src/VL.Qt.SmokeTest/        # native smoke + perf tests
src/VL.Qt.Probe/            # one Qt window from C# without vvvv
help/Qt/                    # help patches + assets
docs/                       # documentation
tools/                      # build, export, license and test scripts
```

## Typical workflows

**Change a patch or C# node:** edit, run vvvv with editable packages, hot-reload on save.

**Change Qt rendering behavior:** edit `src/VL.Qt.Native/`, run `tools/build-native.ps1`, restart vvvv.

**Change the protocol:**
1. Edit `src/VL.Qt.Native/protocol.h` (type or property IDs)
2. Mirror the change in `src/VL.Qt/Protocol.cs` by hand
3. Run `dotnet run --project src/VL.Qt.SmokeTest -- --drift` to check that header and C# agree
4. Implement both sides (`elements.cpp`, `Elements/`, `Reconciler/`)

**Add a new element type:**
1. Add IDs to `protocol.h` and `Protocol.cs`
2. Implement the QML item in `elements.cpp`
3. Add the `Element` class in `src/VL.Qt/Elements/`
4. Add property diffing in `Reconciler/PropertyDiff.cs`
5. Add the node in `src/VL.Qt/Nodes/`
6. Write unit and smoke tests

See [Architecture](architecture.md) for how the pieces fit together.
