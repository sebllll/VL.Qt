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
| `VL.Qt.Audio` | companion package for VL.Audio (Windows only, references VL.Audio and VL.Qt); builds into the package folder `VL.Qt.Audio/lib/net10.0-windows7.0/`. VL.Qt never references it. See [video-audio.md](video-audio.md). |
| `VL.Qt.Tests` | unit tests (NUnit), run them from the Test Explorer |
| `VL.Qt.Audio.Tests` | unit tests of the sync loop of `Video (VL.Audio)` with simulated clocks (Windows only) |
| `VL.Qt.SmokeTest` | drives QtNative without vvvv: rendering, grabs, performance limits |
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

# Smoke tests: QtNative without vvvv (rendering, performance)
dotnet run --project src/VL.Qt.SmokeTest -c Release -- --all

# Compile-check a patch with vvvvc (exit 0 = no build errors)
tools/vvvvc.ps1 -File help/HelloWorld.vl
```

Timing checks of the SmokeTest (`perfoverlay`) tell a busy machine from a regression: a check that reads peaks or long frames prints `SKIP ... host too loaded` instead of failing when the loop of the test itself stalled (a step over 150 ms for the peak checks, more than 20 % of the steps over 1.5 frame budgets for the idle check). Such a SKIP is not a pass: repeat the run on a quieter machine.

### Tearing test pattern (`--tearing`)

A manual scenario, not part of `--all`: N windows, each with a vertical bar sweeping fast across the whole width, so tearing shows as a broken bar. It pumps like the vvvv main loop and prints numbers once per second.

```powershell
dotnet run --project src/VL.Qt.SmokeTest -c Release -- --tearing --windows 2 --interval 0 --seconds 20
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

`pump` is the whole `vlqt_app_process_events_for`, so it is the same for every window of one run. On Windows the display is kept awake during the run (`SetThreadExecutionState`) and a locked session is detected (`display=ok|locked|unknown`). `TEARING WARNING` lines say when the numbers cannot be trusted: fps about 0, refresh `n/a`, or a locked session. The exit code is 0 after a completed run, 2 for bad arguments.

For the Linux runtime see [Linux](linux.md#building-the-linux-runtime-from-source).

## Scripts

Scripts live in `tools/`. All PowerShell scripts take `-Rid win-x64|win-arm64` where it matters (default: the machine's architecture).

**Everyday**

| Script | Use |
|---|---|
| `build.ps1` | Builds everything: QtNative + Qt deploy, then `dotnet build src/VL.Qt` and `src/VL.Qt.Stride`. `-Test` adds unit tests and the SmokeTest (log and grabs in `build/smoke/`), `-Only <scenario,...>` limits the SmokeTest, `-SmokeTimeout <s>` sets its time limit (default 600 s), `-Patch <doc.vl>` compile-checks a patch with vvvvc. `-SkipNative` for C#-only changes, `-Configuration Debug`, `-ForceDeploy`. Exit 0 = all steps passed, a summary lists each step. |
| `start-vvvv.ps1` | Starts vvvv with VL.Qt, VL.Qt.Stride and VL.Qt.Audio from this repo (three junctions in the package repository). `-Build` builds QtNative, VL.Qt and VL.Qt.Stride first (VL.Qt.Audio: `tools/build.ps1`), `-Help "Draw Text"` opens `help/Qt/HowTo Draw Text.vl`, `-Patch <file>` any patch, `-PrepareOnly` only sets up the package repository. |
| `vvvvc.ps1` | Compiles or exports a patch with vvvvc (vvvv's command-line compiler). `-File <doc.vl>` (default `help/HelloWorld.vl`, a test patch of the repo that is not in the NuGet package; use a `help/Qt/` patch with a package), `-Out <dir>` (default `build/vvvvc`), further arguments go to vvvvc. Exit 0 = no build errors. A patch in `VL.Qt.Stride/` or `VL.Qt.Audio/`, or one that depends on one of them (no feed has them before the release), also gets the dev package repository, `--editable-packages` and a local feed `build/vvvvc-feed` with `VL.Qt` and that package packed as version 0.0.1 (repacked when the build output is newer; run `tools/build.ps1` first). `VL.Audio` is not part of vvvv: the export restores it from NuGet (the version pinned in `VL.Qt.Audio.vl` and the patch, 1.9.21; offline from the NuGet cache). |
| `start-vvvv-with-nupkg.ps1` / `.cmd` | Starts vvvv with a packed `VL.Qt.<version>.nupkg` without installing it, e.g. to test a release; the `VL.Qt.Stride.<version>.nupkg` of the same version next to it is unpacked too. Takes the newest nupkg next to the script or from `-NupkgDir`; `-Help`, `-Patch` like above. The `.cmd` is for double-clicking. |

**Native build and Qt**

| Script | Use |
|---|---|
| `setup-qt.ps1` | Installs the pinned Qt version (LGPL modules only, plus Qt Multimedia and Shader Tools) via aqtinstall. `-OutDir` (default `C:\Qt`). Prints the value for `VLQT_QT_DIR`. |
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
| `vl-patch-check.py` | Read-only check of the VL.Qt nodes in `.vl` files against their C# pins (`vl_pin_catalog.py`): hidden pins with a value, IOBoxes on `Interaction`, links to removed pins (errors), IOBoxes at the default value (hints). No argument: all `help/Qt/*.vl`, `help/*.vl`, `VL.Qt.Stride/help/*.vl` and `VL.Qt.Audio/help/*.vl`. `--fix` makes hidden pins with a value visible, `--accept <patch>` puts intended exceptions into `help/.padlint.json`. Test: `test-vl-patch-check.py`. |
| `vl-patch-migrate.py` | Applies the rows of `vl-patch-mapping.txt` (renamed nodes and pins, moved categories) to every patch of the repository; `--check` lists what would change. Regression test: `test-vl-patch-migrate.py`. |

## Packages

The repository builds three NuGet packages:

| Package | Folder | Content |
|---|---|---|
| `VL.Qt` | repo root (`VL.Qt.vl`, `help/`, `src/VL.Qt/`) | the Qt layer and the Qt runtime for Windows and Linux; platform-neutral, no Stride |
| `VL.Qt.Stride` | `VL.Qt.Stride/` (package folder: `VL.Qt.Stride.vl`, `help/`, build output `lib/net10.0-windows7.0/`); C# source in `src/VL.Qt.Stride/` | Qt scenes rendered into Stride textures, Windows only; depends on `VL.Qt` |
| `VL.Qt.Audio` | `VL.Qt.Audio/` (package folder: `VL.Qt.Audio.vl`, `help/`, `README.md`, build output `lib/net10.0-windows7.0/`); C# source in `src/VL.Qt.Audio/` | `Video (VL.Audio)`: the sound of a video as VL.Audio signals, Windows only; depends on `VL.Qt` (same version) and `VL.Audio`, see [video-audio.md](video-audio.md) |

`VL.Qt` never references `VL.Qt.Stride` or `VL.Qt.Audio`. `VL.Qt.Stride.vl` (like `VL.Qt.Audio.vl`) is a minimal package document (dependencies and the assembly, no nodes); its help patches may be written in vvvv or directly (see `AGENTS.md`, "Checking patches"); `lib/` is not tracked by git. Why this layout: [decisions/render-texture.md](decisions/render-texture.md#package-layout-2026-11-14-task-21).

```powershell
dotnet build src/VL.Qt.Stride -c Release                  # DLL into VL.Qt.Stride/lib/net10.0-windows7.0/
dotnet pack src/VL.Qt.Stride -c Release -p:Version=1.2.3  # VL.Qt.Stride.1.2.3.nupkg
```

**Both packages have the same version**: `VersionPrefix` in `Directory.Build.props` for local builds, one `-p:Version` for both in the CI. `VL.Qt.Stride` declares its dependency on `VL.Qt` with the version of the project reference, so packing it with another `-p:Version` than `VL.Qt` points it at a `VL.Qt` that was not released with it. Packing `VL.Qt` needs the native Qt trees first, see the comment in `src/VL.Qt/VL.Qt.csproj`.

The CI (`build.yml`) builds `VL.Qt.Stride` on the Windows runners and packs it in the job `pack` on every run, so a broken pack shows up on a pull request. The step "Verify package (VL.Qt.Stride)" checks the layout and the nuspec (id, version, the only dependency `VL.Qt` at the same version). A tag or a plain `workflow_dispatch` uploads both `.nupkg` files; the release attaches both to the GitHub release and pushes both to nuget.org (see [Release](#release)).

For vvvv, `tools/start-vvvv.ps1` puts the package folders into its package repository as junctions (`VL.Qt`, `VL.Qt.Stride`, `VL.Qt.Audio`), so all are source packages in the dev workflow.

## CI

The workflows are in `.github/workflows/` (`build.yml`, `public-check.yml`; `sanitizers.yml` runs by hand). Every action is pinned to a full commit SHA with the version as a comment, Dependabot keeps the pins current.

The repository `sebllll/VL.Qt` differs from forks and private copies in a few places. What costs minutes is keyed on the repository being public (`github.event.repository.private == false`), so it stays off while the repository is private; the nuget.org push, the CLA and the contributor workflows check the name `sebllll/VL.Qt` (GitHub compares it case-insensitively):

- The hosted runners are free for public repositories, so the `win-arm64` and `linux-arm64` entries also run on pull requests. Elsewhere they run only on tags and on a plain `workflow_dispatch`.
- The workflow `public-check.yml` (keyed on the name only, so it runs while the repository is private too) fails on every pull request and push to `develop` and `main` if a file of a maintainer-only working directory (plans, internal notes, local agent settings) is tracked. Its job is a required status check.
- The `release` job runs only for a `v*` tag whose commit is on `main` (job `release-gate`), in the GitHub environment `release`. In `sebllll/VL.Qt` with the environment variable `NUGET_USER` set, it also pushes all `.nupkg` files (`VL.Qt`, `VL.Qt.Stride`, `VL.Qt.Audio`) to nuget.org through Trusted Publishing (no stored API key); elsewhere or without the variable that step is skipped with a notice. See [Release](#release).
- Pull requests use `pull_request` (not `pull_request_target`); their jobs get no secrets and only `contents: read`.

## Release

One-time setup by a maintainer (repository settings):

- [ ] Environment `release` (Settings > Environments): deployment branches and tags limited to `v*`. A required reviewer is optional: the tag ruleset below already decides who can release.
- [ ] nuget.org Trusted Publishing policy (nuget.org > account > Trusted Publishing): one single policy for all packages, repository owner `sebllll`, repository `VL.Qt`, workflow file `build.yml`, environment `release`, package patterns `VL.Qt`, `VL.Qt.Audio`, `VL.Qt.Stride`, scope "Push new packages and package versions" (needed for package IDs that do not exist yet). Do not create one policy per package: with three policies of the same origin the push of `VL.Qt.Audio` got a 403 (v0.9.9). A package ID that is added later must be added to the patterns by hand on nuget.org before the first release that contains it; nothing in the repository or the CI does that, and without it the push of that package fails. A policy for a private repository is only active for a few days until its first successful push binds it to the repository; if it expired, create it again.
- [ ] Environment variable `NUGET_USER` in `release` (not a secret): the nuget.org profile name that owns the policy.
- [ ] Rulesets (Settings > Rules > Rulesets):
  - `develop` and `main`: pull request required, no deletion or force push, required check `public check (no private files or content)`.
  - `main` only: restrict updates, bypass for the role Repository admin in pull requests only. Others with write access can open pull requests to `main` but not merge them.
  - Tags `refs/tags/v*`: restrict creation, update and deletion, bypass for the role Repository admin. Only admins can start a release. This is the actual protection: a tag runs `build.yml` as it is in the tagged commit, so the job `release-gate` alone does not stop a tag on a branch with a changed workflow.

Per release:

- [ ] Everything for the release is merged into `develop`; `tools/build.ps1 -Test` and the CI on `develop` are green.
- [ ] Open the PR `develop` -> `main` and merge it with a merge commit (no squash).
- [ ] Tag the merge commit on `main`: `git tag v<version>` (for example `v1.0.0`, `v1.1.0-rc.1`) and `git push origin v<version>`. The tag name is the package version.
- [ ] The tag starts `build.yml` with all four RIDs. Check the job `pack`: the step "Verify package" prints the package size (limit 250 MB), the `qt/<rid>/` layout and the nuspec metadata (id, version, license, repository); the steps "Verify package (VL.Qt.Stride)" and "Verify package (VL.Qt.Audio)" do the same for the companion packages.
- [ ] The job `release` creates the GitHub release with all `.nupkg` files and pushes them to nuget.org (approve the deployment first if the environment has a required reviewer).
- [ ] Without `NUGET_USER` (or in a fork) the push is skipped with a notice instead of an error. A failed login means the policy does not match (repository, workflow file, environment) or has expired. Fallback: push by hand with `dotnet nuget push <file> -s https://api.nuget.org/v3/index.json -k <key>` and a short-lived API key.
- [ ] A tag on a commit that is not on `main` builds and tests but does not release (warning in the job `release-gate`). Delete the tag and tag the right commit.
- [ ] After publishing: check the package on nuget.org and install it with `tools/start-vvvv-with-nupkg.ps1`.

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
  Elements/                 # element types (Rectangle, Text, ...); subfolders Effects/, Interactions/,
                            # Feedback/, Shaders/, Qml/, Enums/ share the namespace VL.Qt.Elements
  Nodes/                    # vvvv node wrappers, one folder per category (Elements/, Widgets/, ...)
  Reconciliation/           # scene diffing (namespace VL.Qt.Reconciliation, internal): Reconciler walks the tree,
                            # PropertyDiff writes the SETs, ItemMirror is what QtNative holds, FeedbackRouter routes events
  Wire/                     # the protocol to QtNative (namespace VL.Qt.Wire, internal)
    CommandSinkExtensions.cs  # Stride-typed overloads (Color4, RectangleF, string) for any ICommandSink
    CommandWriter.cs        # binary command encoder
    EventReader.cs          # event buffer parser
    ICommandSink.cs         # target of the reconciler's commands
    Protocol.cs             # IDs mirrored from protocol.h
src/VL.Qt.Native/           # QtNative (C++)
  vlqt.h                    # C API (the exports of all groups)
  vlqt_test.h               # test exports, in every build
  protocol.h                # shared type/property/event IDs
  app/                      # app lifecycle, pump, logging, last error, fonts, animation clock, render gate, scene change count
  window/                   # windows and their exports, event filters, focus, touch input
  platform/                 # platform code (Windows, Linux)
  scene/                    # scene of a window: object table, command decoder, events, input handlers
  elements/                 # element types (type_*.cpp), their table, geometry, Group frame, effect sources
  effects/                  # effect chain, Shader item and its material, layer requests
  resources/                # resource manager (resources.h, resource_*.h/cpp), pixel formats
  offscreen/                # QtTexture scenes: binding, rendering, input, exports, linear pass
  diagnostics/              # perf monitor (perf_*), F2/F5 overlays, debug overlay
  test/                     # the test exports of vlqt_test.h
  testkit/                  # vlqt_testkit, the stand-in D3D11 device of the SmokeTest (not packed)
  tools/glprobe.c           # vlqt-glprobe, the OpenGL check under WSLg (Linux)
  device.h/cpp              # shared D3D11 device, device loss
  render_thread.h/cpp       # Qt render threads of the QtTexture scenes
  *_resource.h/cpp          # images, shaders, snapshots
  qtimageitem.h/cpp         # Image element: draws an image resource
  image_writer.h/cpp        # writes pixels to an image file (vlqt_image_write_file)
  qmlitem.h/cpp             # QmlElement: loads a user .qml file
  vlstyle.h/cpp             # VlStyle, the attached style of the Controls
  audio_tap.h/cpp           # audio tap: the sound of a Video element as samples
src/VL.Qt.Tests/            # C# unit tests
src/VL.Qt.SmokeTest/        # native smoke + perf tests
src/VL.Qt.Probe/            # one Qt window from C# without vvvv
src/VL.Qt.Stride/           # C# source of the VL.Qt.Stride package (see Packages)
VL.Qt.Stride/               # its package folder: VL.Qt.Stride.vl, help/, README.md, lib/ (build output)
help/Qt/                    # help patches + assets
docs/                       # documentation
tools/                      # build, export, license and test scripts
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

**Add a new element type:**
1. Add IDs to `protocol.h` and `Protocol.cs`
2. Implement the item and its property table in `elements/type_*.cpp` and add it to the type table (`elements/element_types.cpp`)
3. Add the `Element` class in `src/VL.Qt/Elements/`
4. Add property diffing in `Reconciliation/PropertyDiff.cs`
5. Add the node in `src/VL.Qt/Nodes/`
6. Write unit and smoke tests

See [Architecture](architecture.md) for how the pieces fit together.
