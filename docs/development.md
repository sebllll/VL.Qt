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
| `VL.Qt.Stride` | companion package for Stride (Windows only, references VL.Stride and VL.Qt); builds into the package folder `VL.Qt.Stride/lib/net10.0-windows7.0/`. VL.Qt never references it. |
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

`pump` is the whole `vlqt_app_process_events`, so it is the same for every window of one run. On Windows the display is kept awake during the run (`SetThreadExecutionState`) and a locked session is detected (`display=ok|locked|unknown`). `TEARING WARNING` lines say when the numbers cannot be trusted: fps about 0, refresh `n/a`, or a locked session. The exit code is 0 after a completed run, 2 for bad arguments.

For the Linux runtime see [Linux](linux.md#building-the-linux-runtime-from-source).

## Scripts

Scripts live in `tools/`. All PowerShell scripts take `-Rid win-x64|win-arm64` where it matters (default: the machine's architecture).

**Everyday**

| Script | Use |
|---|---|
| `build.ps1` | Builds everything: QtNative + Qt deploy, then `dotnet build src/VL.Qt`. `-Test` adds unit tests and the SmokeTest (log and grabs in `build/smoke/`), `-Only <scenario,...>` limits the SmokeTest, `-SmokeTimeout <s>` sets its time limit (default 600 s), `-Patch <doc.vl>` compile-checks a patch with vvvvc. `-SkipNative` for C#-only changes, `-Configuration Debug`, `-ForceDeploy`. Exit 0 = all steps passed, a summary lists each step. |
| `start-vvvv.ps1` | Starts vvvv with VL.Qt from this repo. `-Build` builds first, `-Help "Draw Text"` opens `help/Qt/HowTo Draw Text.vl`, `-Patch <file>` any patch, `-PrepareOnly` only sets up the package repository. |
| `vvvvc.ps1` | Compiles or exports a patch with vvvvc (vvvv's command-line compiler). `-File <doc.vl>` (default `help/HelloWorld.vl`, a test patch of the repo that is not in the NuGet package; use a `help/Qt/` patch with a package), `-Out <dir>` (default `build/vvvvc`), further arguments go to vvvvc. Exit 0 = no build errors. |
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
| `vl-patch-check.py` | Read-only check of the VL.Qt nodes in `.vl` files against their C# pins (`vl_pin_catalog.py`): hidden pins with a value, IOBoxes on `Interaction`, links to removed pins (errors), IOBoxes at the default value (hints). No argument: all `help/Qt/*.vl` and `help/*.vl`. `--fix` makes hidden pins with a value visible, `--accept <patch>` puts intended exceptions into `help/.padlint.json`. Test: `test-vl-patch-check.py`. |

## CI

The workflows are in `.github/workflows/` (`build.yml`, `public-check.yml`; `sanitizers.yml` runs by hand). Every action is pinned to a full commit SHA with the version as a comment, Dependabot keeps the pins current.

The repository `sebllll/VL.Qt` differs from forks and private copies in a few places. What costs minutes is keyed on the repository being public (`github.event.repository.private == false`), so it stays off while the repository is private; the nuget.org push, the CLA and the contributor workflows check the name `sebllll/VL.Qt` (GitHub compares it case-insensitively):

- The hosted runners are free for public repositories, so the `win-arm64` and `linux-arm64` entries also run on pull requests. Elsewhere they run only on tags and on a plain `workflow_dispatch`.
- The workflow `public-check.yml` (keyed on the name only, so it runs while the repository is private too) fails on every pull request and push to `develop` and `main` if a file of a maintainer-only working directory (plans, internal notes, local agent settings) is tracked. Its job is a required status check.
- The `release` job runs only for a `v*` tag whose commit is on `main` (job `release-gate`), in the GitHub environment `release`. In `sebllll/VL.Qt` with the environment variable `NUGET_USER` set, it also pushes the `.nupkg` to nuget.org through Trusted Publishing (no stored API key); elsewhere or without the variable that step is skipped with a notice. See [Release](#release).
- Pull requests use `pull_request` (not `pull_request_target`); their jobs get no secrets and only `contents: read`.

## Release

One-time setup by a maintainer (repository settings):

- [ ] Environment `release` (Settings > Environments): deployment branches and tags limited to `v*`. A required reviewer is optional: the tag ruleset below already decides who can release.
- [ ] nuget.org Trusted Publishing policy (nuget.org > account > Trusted Publishing): repository owner `sebllll`, repository `VL.Qt`, workflow file `build.yml`, environment `release`, scope push for the package `VL.Qt`. A policy for a private repository is only active for a few days until its first successful push binds it to the repository; if it expired, create it again.
- [ ] Environment variable `NUGET_USER` in `release` (not a secret): the nuget.org profile name that owns the policy.
- [ ] Rulesets (Settings > Rules > Rulesets):
  - `develop` and `main`: pull request required, no deletion or force push, required check `public check (no private files or content)`.
  - `main` only: restrict updates, bypass for the role Repository admin in pull requests only. Others with write access can open pull requests to `main` but not merge them.
  - Tags `refs/tags/v*`: restrict creation, update and deletion, bypass for the role Repository admin. Only admins can start a release. This is the actual protection: a tag runs `build.yml` as it is in the tagged commit, so the job `release-gate` alone does not stop a tag on a branch with a changed workflow.

Per release:

- [ ] Everything for the release is merged into `develop`; `tools/build.ps1 -Test` and the CI on `develop` are green.
- [ ] Open the PR `develop` -> `main` and merge it with a merge commit (no squash).
- [ ] Tag the merge commit on `main`: `git tag v<version>` (for example `v1.0.0`, `v1.1.0-rc.1`) and `git push origin v<version>`. The tag name is the package version.
- [ ] The tag starts `build.yml` with all four RIDs. Check the job `pack`: the step "Verify package" prints the package size (limit 250 MB), the `qt/<rid>/` layout and the nuspec metadata (id, version, license, repository).
- [ ] The job `release` creates the GitHub release with the `.nupkg` and pushes it to nuget.org (approve the deployment first if the environment has a required reviewer).
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
