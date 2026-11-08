# Linux

Linux x64 and ARM64 are a preview. vvvv exports only console apps on Linux, without Stride/Skia. VL.Qt therefore opens its own Qt window there (kiosk-style). Closing the window or SIGTERM/SIGINT ends the app cleanly (exit code 0, patches are disposed).

You export on Windows and run the result on Linux. WSL2 with WSLg (Windows 11) is enough to try it: the Qt window opens on the Windows desktop.

## 1. Prepare the patch

- Build the scene as usual and end it in a `Renderer [Qt]`.
- Add a `KeepAppAlive [System]` to the top patch (Application) with `Is Alive` = true. A console app otherwise ends after the first frame.
- Set `Bound To Document` on the Renderer to true if closing the window should end the app.

`help/HelloWorldLinux.vl` is such a patch (Rectangle + Text → Group → Renderer, KeepAppAlive).

## 2. Export from vvvv

The export needs these settings; vvvv keeps them in a `.props` file next to the patch (`help/HelloWorldLinux.props` is an example):

| Setting | Value |
|---|---|
| Output type | `Exe` (console application) |
| Target framework | `net10.0` (not `net10.0-windows`) |
| Runtime identifier | `linux-x64` (for an arm64 machine `linux-arm64`) |

**In the editor:** open the application exporter from the quad menu, set the values from the table and export.

**From the command line** (the `.props` next to the patch wins over `--rid`):

```powershell
tools/vvvvc.ps1 -File help/HelloWorldLinux.vl -Out build/export-linux
```

The export contains an executable without file extension (`HelloWorldLinux`), its `.dll` files and the Qt runtime in `runtimes/linux-x64/native/` (only for this RID, about 105 MB).

> [!IMPORTANT]
> The Qt runtime for Linux has to be inside the VL.Qt package you export with. The release package contains all four RIDs. When you work with VL.Qt from the repo sources, build the Linux runtime first in WSL (see [Building the Linux runtime from source](#building-the-linux-runtime-from-source)), otherwise `runtimes/linux-*` is missing from the export.

## 3. Set up WSL once

In PowerShell (as admin, once), then restart if asked:

```powershell
wsl --install -d Ubuntu-24.04
```

In the Ubuntu shell: the export is framework-dependent and needs the .NET 10 runtime, plus the system libraries Qt uses (GL, xkb, xcb/Wayland, fonts):

```bash
sudo apt update
sudo apt install -y libgl1 libegl1 libgbm1 libgl1-mesa-dri libxkbcommon0 libxkbcommon-x11-0 \
  libxcb-cursor0 libxcb-icccm4 libxcb-image0 libxcb-keysyms1 libxcb-randr0 libxcb-render-util0 \
  libxcb-shape0 libxcb-xkb1 libwayland-client0 libwayland-cursor0 libwayland-egl1 \
  libfontconfig1 fonts-dejavu-core libdbus-1-3 libpulse0
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --runtime dotnet
```

The last line installs the runtime into `~/.dotnet` without sudo. If you have the repo, `tools/setup-wsl.sh` does all of this (plus the build tools).

## 4. Run it in WSL

Copy the export folder into the Linux file system (starting from `/mnt/c/...` works, but is slower to load):

```bash
cp -r /mnt/c/<path-to-repo>/build/export-linux/HelloWorldLinux ~/
cd ~/HelloWorldLinux
chmod +x HelloWorldLinux
DOTNET_ROOT=~/.dotnet ./HelloWorldLinux
```

The window appears on the Windows desktop. End it with the window's close button or `Ctrl+C`. Qt messages go to the vvvv logger; errors also go to stderr.

- **No text, only boxes, or a `vlqt.font` warning:** no font installed, see [Fonts](#fonts).
- **`You must install .NET`:** `DOTNET_ROOT` is not set or points to a folder without the .NET 10 runtime.
- **Black window or no GPU:** WSLg renders through Mesa's d3d12 driver; check with `glxinfo -B` (package `mesa-utils`). Mesa warnings like `ZINK: failed to choose pdev` on stderr are harmless.

On a real Linux machine it is the same: copy the folder, install the packages and .NET 10, start the executable.

## Fonts

VL.Qt sets no font family, so Qt takes the system default via fontconfig (DejaVu Sans on Ubuntu/WSL). Install a font package on the target, e.g. `sudo apt install fontconfig fonts-dejavu-core`. A minimal or kiosk system without any font would render all text as empty boxes (tofu): in that case VL.Qt logs a warning (category `vlqt.font`) and falls back to the DejaVu Sans Regular in the bundle (`fonts/DejaVuSans.ttf`, only used when no system font exists). Other fonts used by your content still have to be installed on the target.

## Building the Linux runtime from source

Needed when you export for Linux from the repo sources (the release package already contains it). In the WSL shell, inside the repo:

```bash
tools/setup-wsl.sh          # once: packages + .NET 10 SDK
tools/setup-qt.sh           # once: Qt 6.12 for Linux into ~/Qt
tools/build-native.sh       # libvlqt.so
tools/deploy-linux.sh       # Qt bundle next to it in runtimes/linux-<arch>/native
tools/test-linux.sh --skip-native   # optional: SmokeTest + unit tests
```

The RID follows the WSL architecture (`linux-x64` or `linux-arm64`). After that, an export from Windows with this RID includes the Linux runtime. The Linux scripts are listed in [Development](development.md#scripts).

### Timing checks on slower machines

The time limits of the SmokeTest (e.g. 10 000 SETs in one apply < 2 ms) were set on a current Windows x64 desktop CPU. On Linux the SmokeTest first runs a short calibration loop and prints `INFO perf calibration: ... CPU-bound limits x<f>`: the three purely CPU-bound limits (10 000 SET in one apply, in `apply` and in the core checks, and the idle change check of `measure`) are multiplied by that factor (1 on a fast core, at most 3). A measurement above the limit is repeated up to three times before it counts, so a late clock-up or a woken process does not fail the run; a real regression stays above the limit in every round and still fails. `VLQT_PERF_FACTOR` / `--perf-factor` set the factor by hand and switch the calibration off (`VLQT_PERF_FACTOR=0.5` shows that the checks bite). Details of the other limits: `--perf-warn-only`, `src/VL.Qt.SmokeTest/PerfLimits.cs`.
