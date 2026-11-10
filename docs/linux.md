# Linux

VL.Qt runs on Linux x64 and ARM64. vvvv exports only console apps on Linux, without Stride/Skia. VL.Qt therefore opens its own Qt window there (kiosk-style). Closing the window or SIGTERM/SIGINT ends the app cleanly (exit code 0, patches are disposed).

You export on Windows and run the result on Linux. WSL2 with WSLg (Windows 11) is enough to try it: the Qt window opens on the Windows desktop.

> [!NOTE]
> **What was tested for 1.0:** WSL2 Ubuntu 24.04 with WSLg (x64 and arm64) only. Native Ubuntu desktops with X11 or Wayland, Raspberry Pi and eglfs are **not tested** yet, see [Other Linux systems](#other-linux-systems-not-tested). Problems that are known today are listed in [Known problems](#known-problems).

## 1. Prepare the patch

- Build the scene as usual and end it in a `Renderer [Qt]`.
- Add a `KeepAppAlive [System]` to the top patch (Application) with `Is Alive` = true. A console app otherwise ends after the first frame, without a window and without a message. This also holds for the `HowTo …` help patches: they have no `KeepAppAlive`, add one to try them on Linux.
- Set `Bound To Document` on the Renderer to true if closing the window should end the app.

`help/HelloWorldLinux.vl` in the repository is such a patch (Rectangle + Text → Group → Renderer, KeepAppAlive). It is a test patch of the repo and is **not part of the NuGet package**; with the package, take any patch of `help/Qt/` (for example `HowTo Draw a Rectangle`), add a `KeepAppAlive` and export that.

## 2. Export from vvvv

The export needs these settings; vvvv keeps them in a `.props` file next to the patch (`help/HelloWorldLinux.props` in the repository is an example):

| Setting | Value |
|---|---|
| Output type | `Exe` (console application) |
| Target framework | `net10.0` (not `net10.0-windows`) |
| Runtime identifier | `linux-x64` (for an arm64 machine `linux-arm64`) |

**In the editor:** open the application exporter from the quad menu, set the values from the table and export.

**From the command line** (the `.props` next to the patch wins over `--rid`). With the repo sources, the test patch `help/HelloWorldLinux.vl` is exported like this:

```powershell
tools/vvvvc.ps1 -File help/HelloWorldLinux.vl -Out build/export-linux
```

For a patch without a `.props` (all of `help/Qt/`, also the patches that ship in the package) pass the settings as arguments:

```powershell
tools/vvvvc.ps1 -File "help/Qt/HowTo Draw a Rectangle.vl" -Out build/export-linux-arm64 --rid linux-arm64 --output-type Exe --asset-behavior RelativeToOutput
```

The export contains an executable without file extension (`HelloWorldLinux`), its `.dll` files and the Qt runtime in `runtimes/linux-x64/native/` (only for this RID, about 120 MB on x64). vvvvc exports **self-contained** by default: the .NET runtime is inside the folder (on x64 about 550 MB in total for `HelloWorldLinux`), so the target needs no .NET. With `--self-contained false` the export is framework-dependent (about 130 MB on x64) and needs the .NET 10 runtime on the target.

> [!IMPORTANT]
> The Qt runtime for Linux has to be inside the VL.Qt package you export with. The release package contains all four RIDs. When you work with VL.Qt from the repo sources, build the Linux runtime first in WSL (see [Building the Linux runtime from source](#building-the-linux-runtime-from-source)), otherwise `runtimes/linux-*` is missing from the export. The export still succeeds in that case; the check is a `runtimes/<rid>/native/` folder in the export (about 45 files).

> [!WARNING]
> The `.props` next to a patch wins over `--rid`. `help/HelloWorldLinux.props` says `linux-x64`: exported on an arm64 machine with only the `linux-arm64` runtime built, it silently gets **no Qt runtime** at all. For arm64 use a patch without a `.props` and the arguments above, or set the runtime identifier to `linux-arm64` in the exporter dialog.

## 3. Set up WSL once

In PowerShell (as admin, once), then restart if asked:

```powershell
wsl --install -d Ubuntu-24.04
```

The first start asks for a user name and password. To try it in a separate distribution next to an existing one use `wsl --install Ubuntu-24.04 --name vlqt-test`; `wsl --unregister vlqt-test` removes it again, with everything in it.

In the Ubuntu shell, one command installs what the exported app needs, the system libraries Qt uses (GL, xkb, xcb/Wayland, fonts) and, if you want a framework-dependent export, the .NET 10 runtime:

```bash
bash /mnt/c/<path-to-repo>/tools/setup-wsl.sh --runtime-only
```

It takes about half a minute and checks the prerequisites first (Ubuntu 24.04, sudo, WSLg) and says what is missing. With a self-contained export `--no-dotnet` skips the runtime. Without the repo, install the packages by hand:

```bash
sudo apt update
sudo apt install -y libgl1 libegl1 libgbm1 libgl1-mesa-dri libxkbcommon0 libxkbcommon-x11-0 \
  libxcb-cursor0 libxcb-icccm4 libxcb-image0 libxcb-keysyms1 libxcb-randr0 libxcb-render-util0 \
  libxcb-shape0 libxcb-xkb1 libsm6 libice6 libxrender1 libxrandr2 libglib2.0-0t64 \
  libwayland-client0 libwayland-cursor0 libwayland-egl1 libfontconfig1 fonts-dejavu-core \
  libdbus-1-3 libpulse0 libicu74
# only for a framework-dependent export:
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --runtime dotnet
```

The last line installs the .NET runtime into `~/.dotnet` without sudo. `tools/setup-wsl.sh` without arguments does the same plus the build tools and the .NET SDK for [building from source](#building-the-linux-runtime-from-source).

## 4. Run it in WSL

Copy the export folder into the Linux file system (starting from `/mnt/c/...` works, but is slower to load). Names with spaces (the `HowTo …` patches) have to be quoted:

```bash
cp -r /mnt/c/<path-to-repo>/build/export-linux/HelloWorldLinux ~/
cd ~/HelloWorldLinux
chmod +x HelloWorldLinux
./HelloWorldLinux
```

A framework-dependent export needs `DOTNET_ROOT=~/.dotnet ./HelloWorldLinux`. The window appears on the Windows desktop. End it with the window's close button or `Ctrl+C`. Qt messages go to the vvvv logger; errors also go to stderr.

- **`Permission denied`:** the executable bit is lost on copying from Windows, run `chmod +x <name>`.
- **No window, the program ends at once:** no `KeepAppAlive` in the patch, see [Prepare the patch](#1-prepare-the-patch).
- **No text, only boxes, or a `vlqt.font` warning:** no font installed, see [Fonts](#fonts).
- **`You must install .NET`:** the export is framework-dependent and `DOTNET_ROOT` is not set or points to a folder without the .NET 10 runtime.
- **Black window or no GPU:** WSLg renders through Mesa's d3d12 driver; check with `glxinfo -B` (package `mesa-utils`). Mesa warnings like `ZINK: failed to choose pdev` on stderr are harmless.
- **`libSM.so.6: not found`** and similar: a system library is missing, see [Known problems](#known-problems).

On a real Linux machine it is the same: copy the folder, install the packages (`setup-wsl.sh --runtime-only` works on Ubuntu 24.04), start the executable.

## Fonts

VL.Qt sets no font family, so Qt takes the system default via fontconfig (DejaVu Sans on Ubuntu/WSL). Install a font package on the target, e.g. `sudo apt install fontconfig fonts-dejavu-core`. A minimal or kiosk system without any font would render all text as empty boxes (tofu): in that case VL.Qt logs a warning (category `vlqt.font`) and falls back to the DejaVu Sans Regular in the bundle (`fonts/DejaVuSans.ttf`, only used when no system font exists). Other fonts used by your content have to be installed on the target, or shipped with the export: put the files in a `fonts/` folder next to the patch and load them with `LoadFont`, see [Exporting an app](export.md#fonts). `fonts/` is copied next to the executable and works the same on Linux. Without the font, Qt substitutes another one and VL.Qt logs the missing family once (category `vlqt.font`). Check the license of a font before shipping it.

## Building the Linux runtime from source

Needed when you export for Linux from the repo sources (the release package already contains it). In the WSL shell, inside the repo (clone it in WSL, or copy the checkout; the repo is also visible under `/mnt/c/...`, but building in the Linux file system is faster):

```bash
tools/setup-wsl.sh          # once: packages + .NET 10 SDK, about 1.5 minutes (x64 on a fast line: 40 s)
tools/setup-qt.sh           # once: Qt 6.12 for Linux into ~/Qt, about 1.5 minutes, 2 GB (x64 on a fast line: 25 s)
tools/build-native.sh       # libvlqt.so
tools/deploy-linux.sh       # Qt bundle next to it in runtimes/linux-<arch>/native
tools/test-linux.sh --software --skip-native   # optional: SmokeTest + unit tests (without --software see Known problems)
```

`tools/test-linux.sh` without `--skip-native` runs the build and the bundle step itself (about 1 minute). The RID follows the WSL architecture (`linux-x64` or `linux-arm64`). After that, an export from Windows with this RID includes the Linux runtime. When the repo lives in the WSL file system, copy `runtimes/linux-<arch>` into the `runtimes/` folder of the Windows checkout first. On x64 building directly in `/mnt/c/<path-to-repo>` also works (QtNative 20 s, bundle 20 s) and needs no copy. The Linux scripts are listed in [Development](development.md#scripts).

### Testing the window pins under WSLg

The default platform of `tools/test-linux.sh` is `offscreen` (no window). The window scenarios (`Bounds`, `Borderless`, `Fullscreen`, `Is Visible`) run in a real window with:

```bash
tools/test-linux.sh --skip-native --platform xcb --no-unit -- --only geometry,renderer,visibility,borderless,fullscreen
```

Log and grabbed PNGs are in `build/test-linux-<rid>/xcb/` (`smoke.out`, `smoke/`). On WSL2 Ubuntu 24.04 arm64 (WSLg) `geometry`, `renderer`, `visibility` and the checks of `borderless` and `fullscreen` that do not depend on the window position pass; four checks fail, all about where the compositor puts the window (see [Known problems](#known-problems)).

On x64 the keys were checked in an exported `HelloWorldLinux` under WSLg: `F11` and `Alt+Enter` each switch to fullscreen over the whole screen (3440x1440 on that screen) and back to the original position and size; the app ends with exit code 0 on SIGTERM. The pins and the screen choice with two monitors are not checked yet.

For the window pins that need a person (live `Borderless` toggle, `Fullscreen` screen choice, F11 and Alt+Enter) start an export (see above) and press the keys in its window: F11 or Alt+Enter toggles fullscreen like the `Fullscreen` pin. The live toggles of `Borderless` and `Fullscreen` can be watched in the SmokeTest scenarios (`--only borderless`, `--only fullscreen`, the test windows open on the desktop for a moment); no help patch drives these pins live in an exported Linux app yet, and the editor runs on Windows only.

### Timing checks on slower machines

The time limits of the SmokeTest (e.g. 10 000 SETs in one apply < 2 ms) were set on a current Windows x64 desktop CPU. On Linux the SmokeTest first runs a short calibration loop and prints `INFO perf calibration: ... CPU-bound limits x<f>`: the three purely CPU-bound limits (10 000 SET in one apply, in `apply` and in the core checks, and the idle change check of `measure`) are multiplied by that factor (1 on a fast core, at most 3). A measurement above the limit is repeated up to three times before it counts, so a late clock-up or a woken process does not fail the run; a real regression stays above the limit in every round and still fails. `VLQT_PERF_FACTOR` / `--perf-factor` set the factor by hand and switch the calibration off (`VLQT_PERF_FACTOR=0.5` shows that the checks bite). Details of the other limits: `--perf-warn-only`, `src/VL.Qt.SmokeTest/PerfLimits.cs`.

## Other Linux systems (not tested)

For 1.0 only WSL2 with WSLg was tested. What to expect elsewhere, from how the platforms work:

| System | State | Notes |
|---|---|---|
| Ubuntu desktop with X11 (xcb) | not tested | the platform WSLg uses as well (through XWayland); `Bounds`, `Borderless` and `Fullscreen` follow the window manager |
| Ubuntu desktop with Wayland | not tested | the compositor decides: a Wayland client cannot place its window, so the position part of `Bounds` is ignored (under WSLg's Wayland the window reports a position of its own); the compositor decides about decorations and the screen of a fullscreen window |
| Raspberry Pi / eglfs | not tested | eglfs shows one fullscreen surface: `Borderless`, `Fullscreen` and `Bounds` have no effect |

Run `tools/test-linux.sh` as above on such a system and compare with the failures listed under [Known problems](#known-problems) before reporting a problem.

## Known problems

| Problem | Cause | Workaround |
|---|---|---|
| `tools/test-linux.sh` fails at the bundle step, `libSM.so.6 => not found` / `libICE.so.6 => not found` in `libqxcb.so` | the fresh Ubuntu 24.04 WSL image lacks two X11 libraries that Qt's xcb plugin loads | `tools/setup-wsl.sh` or `sudo apt install libsm6 libice6` (not needed on x64: the fresh x64 image runs without them, `libqxcb.so` of the linux-x64 bundle does not depend on them; installing them does no harm) |
| The export has no `runtimes/<rid>/native/`, the app cannot find Qt | the Linux runtime was not built, or a `.props` next to the patch names another RID (`help/HelloWorldLinux.props`: `linux-x64`) | [Build the runtime](#building-the-linux-runtime-from-source), then check the RID, see [Export from vvvv](#2-export-from-vvvv) |
| A `HowTo …` patch exported for Linux ends at once without a window | no `KeepAppAlive` in the patch | add `KeepAppAlive [System]` with `Is Alive` = true |
| The SmokeTest hangs at the end (WSLg, Mesa d3d12, Qualcomm arm64), exit code 124 after 10 minutes | the driver hangs when the process ends; exported apps are not affected (SIGTERM ends them in under a second) | `tools/test-linux.sh` stops the SmokeTest 30 s after its last line and takes the result from the log; `VLQT_GPU=software` avoids the hang (llvmpipe instead of the host GPU) |
| `--platform wayland` with d3d12 hangs the whole WSL VM | Mesa d3d12 with Wayland under WSLg (#216) | use `--platform xcb` (the default of an export), or `GALLIUM_DRIVER=llvmpipe`; `tools/test-linux.sh` refuses the combination |
| Under WSLg (x64 and arm64) the SmokeTest fails the same four window checks: `233: created borderless at Bounds`, `143/234 live: Bounds not written while fullscreen`, `234 on create: off restores position + size`, `143/234 on create: Bounds after the round trip unchanged` (xcb) | WSLg's window manager places the window itself: the position of a new window and the position after leaving fullscreen differ from the requested one (e.g. `X:0 Y:0` instead of `X:180 Y:140`); size and fullscreen itself are right | none needed for patches that do not rely on the window position; open |
| `tools/test-linux.sh` without `--software` fails 12 effect checks on x64 under WSLg, in the default `offscreen` run and with `--platform xcb` (`effect-chain: Mask node text`, `effect-passes`, `487`, `438`; probes read 0, a hitch-rate check `361` failed once as well) | not found. An exported `HowTo Add a Glow` shows the glow correctly in the WSLg window, so the effect itself works; the checks read the effect back from a grab. Not seen with `--software`, where these checks are skipped | run `tools/test-linux.sh --software` (as the CI does) for the full suite; open |
| Under WSLg's Wayland the SmokeTest reports ten failed checks (`Bounds` position, `Fullscreen` restore, `Borderless` position) | a Wayland client cannot choose its position, the compositor reports its own; after leaving fullscreen the size differs from the requested one (e.g. 494x267 instead of 500x300) | use xcb under WSLg; document, no fix possible in the client |
| `libEGL warning`, `MESA: error: ZINK: failed to choose pdev` on stderr | Mesa probes drivers without a DRM device (WSL has `/dev/dxg` only) | harmless |
| All text shows as boxes | no font installed | see [Fonts](#fonts) |
| Copying the export from Windows: `Permission denied` | the executable bit is lost | `chmod +x <name>` |
| `wsl --install` shows no user prompt (`--no-launch`) | the distribution is installed but never started | start it once with `wsl -d <name>` and create the user, or as root: `useradd -m -s /bin/bash -G sudo <user>` and `printf '[user]\ndefault=<user>\n' > /etc/wsl.conf`, then `wsl --terminate <name>` |
