# Deployment

How QtNative and the Qt runtime are licensed, packaged and found at run time. Code and files: `src/VL.Qt/VL.Qt.csproj` (package layout), `src/VL.Qt/Interop/QtNativeLibrary.cs` (loader), `VL.Qt.vl.targets` and `src/VL.Qt/buildTransitive/VL.Qt.targets` (export), `.github/workflows/build.yml` (the CI), `tools/check-licenses.*` and `tools/trim-deploy.ps1`. The layer itself is described in [native-boundary.md](native-boundary.md). Build steps are in [../development.md](../contributing/development.md), export steps in [../export.md](../export.md) and [../linux.md](../linux.md), licence texts in [../../LICENSES.md](../../LICENSES.md).

## Context

VL.Qt is open source and ships Qt next to its own native layer. Qt searches for its libraries and plugins next to `vvvv.exe` by default, which is not where a vvvv package lives, and vvvv hosts many other packages in the same process.

## Design

### Licensing and Qt version

- **LGPL only.** Qt is linked dynamically and unmodified, and only LGPL modules are used; there is no dependency on Qt Commercial. The CI checks the module list (`tools/check-licenses.ps1`, `.sh`): no GPL-only parts such as Quick3D or Charts. The package carries the licence texts, `LICENSES.md` and a link to the Qt sources.
- **Qt 6.12**, pinned (`find_package(Qt6 6.12 ...)` in `CMakeLists.txt`, `QT_VERSION` in the CI). The project follows upstream minor versions instead of relying on LTS patch releases, which are time-limited for open source users.
- **Trademark, patents, devices.** The name "VL.Qt" names a tool made for use with Qt (fair use under section 1.6 of the Qt trademark guidelines), with the notice and conditions in [LICENSES.md](../../LICENSES.md#trademarks). Codec patents (H.264/HEVC via FFmpeg) and the LGPL-3.0 "Installation Information" duty for locked-down devices belong to whoever distributes or uses the result; see the [notes for users](../../LICENSES.md#notes-for-users).

### Qt private classes

QtNative uses Qt private classes (`*_p.h`, `qpa/`, `rhi/`), which the LGPL allows and Qt does not keep compatible. Every use is listed in the head comment of `src/VL.Qt.Native/elements/elements.h` (and in the head comment of the file that has it), and `CMakeLists.txt` links the `...Private` modules for them. That list is the checklist of a Qt update: build, run `tools/build.ps1 -Test`, check each entry.

### Platforms

| RID | Library | Built with |
|---|---|---|
| `win-x64`, `win-arm64` | `vlqt.dll` | CMake + MSVC, `windeployqt`, `tools/trim-deploy.ps1` |
| `linux-x64`, `linux-arm64` | `libvlqt.so` | CMake, `tools/deploy-linux.sh` |

Each RID is built on a runner of its own architecture, because the deploy tool of an arm64 Qt is an arm64 program. The CI (GitHub Actions, `aqtinstall` with cached Qt) is the only source of published builds, so the licence files are always complete; a local build is for development and native debugging. A `v*` tag builds all four RIDs and the package; which RIDs a pull request builds is set in `build.yml`. The CI ships the PDBs of QtNative.

### One package with the runtime

- One NuGet package `VL.Qt` holds the C# code (`lib/net10.0/`), QtNative and the Qt runtime, so the Qt version always matches QtNative. A separate runtime package is worth it only if the size becomes a problem (nuget.org limit: 250 MB).
- The per-RID tree (QtNative, Qt libraries, `plugins/`, `qml/`; only the modules used) lives under `qt/<rid>/`, not under `runtimes/<rid>/native`: NuGet would treat every file there as a native asset and flatten it into the app output. In the repository and in an app's output the tree is `runtimes/<rid>/native/`.

### Finding Qt at run time

`QtNativeLibrary` derives the folder from the location of the C# assembly: `runtimes/<rid>/native` next to the assembly (app output), `../../qt/<rid>` (package), `../../runtimes/<rid>/native` (repository). The first folder that holds `vlqt.dll` or `libvlqt.so` wins.

- **Qt libraries (Windows):** the folder is prepended to the process-local `PATH` before the first P/Invoke, and QtNative is loaded explicitly with `NativeLibrary.Load`. This also covers QML plugins that look for `Qt6Quick*.dll` in the native root. On Linux `libvlqt.so` finds Qt through its `RUNPATH` (`$ORIGIN`).
- **Plugins:** `QCoreApplication::addLibraryPath(<folder>/plugins)` in `vlqt_app_init`, before the `QGuiApplication` is created.
- **QML:** `engine->addImportPath(<folder>/qml)` for each engine.

### Export

An export copies the runtime beside the exported app. A source package (`VL.Qt.vl` next to `VL.Qt.vl.targets`; the exporter imports `<document>.vl.targets`) copies `runtimes/<rid>/native` for the RID of the export, or all RIDs when none is set. An installed package (a `PackageReference` of the export project) imports `buildTransitive/VL.Qt.targets` instead, which copies `qt/<rid>/` to `runtimes/<rid>/native/` of the app. A console export needs a `KeepAppAlive` in the top patch, otherwise the app ends after the first frame. Details: [../export.md](../export.md).

## Consequences

- An export that does not set its RID carries the runtime of every RID; set the RID.
- A Qt update is a version bump in `setup-qt.*`, the CI and `CMakeLists.txt`, plus the private-class checklist.
- The RID resolution lives in three places that must agree: `QtNativeLibrary`, `VL.Qt.vl.targets` and `buildTransitive/VL.Qt.targets`.

## Alternatives considered

- **Qt Commercial as a prerequisite:** contradicts open source.
- **A separate runtime package:** version coupling between two packages is error-prone.
- **`qt.conf` or `QT_PLUGIN_PATH`:** `qt.conf` paths are relative to `vvvv.exe`; the variable affects the whole process.
- **`AddDllDirectory` for the Qt DLLs:** needs `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)`, which takes effect process-wide; afterwards `LoadLibrary` no longer searches `PATH` for any vvvv package.
- **`buildTransitive/` for source packages:** the exporter does not read it for them.

## Open questions

- Trimming: `windeployqt` also pulls in `Qt6Network`, `Qt6OpenGL`, `Qt6Svg`, `qmltooling/`, `tls/` and `networkinformation/`. `tools/trim-deploy.ps1` removes what VL.Qt never loads; `Qt6Network` and `Qt6OpenGL` stay as hard imports of Qml and Quick. What remains should be reviewed before shipping.

## Evidence

- `tools/trim-deploy.ps1` lists each removed part with its reason and the parts that cannot go (`dumpbin /dependents`).
- The CI run of a pull request is the reference for package size.
