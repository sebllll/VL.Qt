# QtNative: Build, Packaging and Deployment

How the native layer (QtNative) is built, licensed, packaged and located at runtime.

## Design
### Licensing and Qt version
- **LGPL compliance.** VL.Qt is open source. Qt is linked dynamically only, unmodified, and only LGPL modules are used. There is no dependency on Qt Commercial.
- **Qt 6.12** (LTS), pinned to a fixed version. The project deliberately follows upstream minor versions instead of relying on LTS patch releases, because those are time-limited for open source users. The minimum is 6.8 because of the D3D11 render target features.
- **Trademark, patents, devices.** The name "VL.Qt" is kept as a tool made for use with Qt (fair use under section 1.6 of the Qt trademark guidelines) with the trademark notice and the conditions listed in [LICENSES.md](../../LICENSES.md#trademarks). Codec patents (H.264/HEVC via FFmpeg) and the LGPL-3.0 "Installation Information" duty for locked-down devices are the responsibility of whoever distributes or uses the result; both are described in the [notes for users](../../LICENSES.md#notes-for-users). Open: confirm the name with The Qt Company or a licensing expert before 1.0.
- **LGPL package contents:** licence texts, `LICENSES.md` and a link to the Qt sources. CI checks the module list: only LGPL modules, no GPL-only parts (for example Quick3D, Charts). Obligations are automated instead of manual.

### API granularity: command buffer
- The C# reconciler writes the differences of each frame as a binary command list (`create id type`, `setProp id prop value`, `reparent id parent index`, `destroy id`) and hands it over with **one** call, `apply(buf, len)`. QtNative stays simple and only holds a type/property ID table. The way back (events/signals) uses `poll(outBuf)` on the same principle.
- Rationale: one managed/native transition per frame; a small, stable QtNative API (every QtNative change means a vvvv restart); logic that stays live-editable in C#; a buffer that can be recorded and replayed.
- Consequence: QtNative cannot be hot-reloaded (the DLL is locked), so it is kept small.

### Logging
- All Qt messages (debug, info, warning, critical, fatal, including QML warnings) end up in the vvvv log window. Path: `qInstallMessageHandler` in QtNative → native callback → C# → `ILogger` (category `VL.Qt`, level mapping). The handler is process-wide like the `QGuiApplication` and therefore hangs on the same resource provider. Errors of the QtNative exports use the same channel (error code plus `lastError()`).
- The target is the vvvv log window, not Sysinternals DebugView / `OutputDebugString`.
- On shutdown `DllMain(DLL_PROCESS_DETACH)` sets the log callback to `nullptr`; otherwise Qt would log into C# after the CLR has ended. This works because the QtNative DLL unloads before `Qt6Core`.

### Build
- CMake + MSVC for **win-x64 and win-arm64**, a pure C ABI (`extern "C"`).
- CI (GitHub Actions, Windows runners, `aqtinstall` with cached Qt → CMake → `windeployqt` → `dotnet pack`) is the **only source of published builds**: reproducible, licence files always complete.
- An optional CMake preset provides a local QtNative build for development and native debugging only, never for publishing. Without it every QtNative change would require push → wait → artifact → vvvv restart.
- The `VL.Core` reference in the project file points to the DLLs of the vvvv installation (`VvvvDir`, overridable), because the NuGet previews use a different version scheme than the installation.
- Debugging: CI ships the PDBs of QtNative; mixed-mode attach to `vvvv.exe`; `-qmljsdebugger` optionally through an environment variable.
- The main thread must be STA (otherwise `OleInitialize() failed`). vvvv satisfies this; custom test hosts need `[STAThread]`.

### Packaging
- **One NuGet package `VL.Qt`** containing the C# code, QtNative and the Qt runtime, so the Qt version always matches QtNative. Splitting the runtime into its own package is only worth it if the size becomes a problem (nuget.org limit: 250 MB).
- `windeployqt --qmldir` fills the per-RID tree (QtNative, Qt DLLs, `plugins/`, `qml/`; only the modules actually used). CI checks structure and package size. In the package the tree lives under `qt/<rid>/` and the assembly under `lib/net10.0/`.

### Locating Qt files at runtime
The package folder is derived from the location of the C# assembly.
- **Qt DLLs:** `<native>` is prepended to the **process-local** `PATH` (`Environment.SetEnvironmentVariable`) before the first P/Invoke, and QtNative is loaded explicitly with `NativeLibrary.Load`. This also covers QML plugins that look for `Qt6QuickShapes.dll` in the native root.
- **Plugins:** `QCoreApplication::addLibraryPath(<package>/plugins)` before the `QGuiApplication` constructor.
- **QML:** `engine->addImportPath(<package>/qml)`.
- Rationale: by default Qt searches next to `vvvv.exe`, and `qt.conf` does not help because relative paths in it refer to `vvvv.exe`.

### vvvv export
- **Source package:** `VL.Qt.vl.targets` next to `VL.Qt.vl` is the mechanism (the exporter imports `<document>.vl.props/.targets`; `buildTransitive/` is not read for source packages). It copies `runtimes/$(RuntimeIdentifier)/**` as content, with the RID coming from the export props. If `RuntimeIdentifier` is empty, everything under `runtimes/` is copied. Exporting for one RID saves about 45 MB.
- **Installed package (NuGet dependency):** the export exe receives `VL.Qt` through a `PackageReference`, and `VL.Qt.vl.targets` is not imported. `buildTransitive/VL.Qt.targets` in the package copies `qt/<rid>/**` to `runtimes/<rid>/native/` of the app instead.
- **Keep-alive:** the WinForms host VL.App.WindowsForms lives as long as `Application.OpenForms` or an open `AppHost.KeepAlive()`, and the `QQuickWindow` is not a form. `Renderer [Qt]` therefore holds `AppHost.KeepAlive()` in the WinForms host. Console exports use `KeepAppAlive` in the top-level patch.

## Open questions
- **Trimming `windeployqt` output:** it also pulls in `Qt6Network`, `Qt6OpenGL`, `Qt6Svg`, `qmltooling/`, `tls/` and `networkinformation/`. `Qt6Network` stays (a dependency of Qml); the rest should be reviewed before shipping.

## Rejected alternatives
- Qt Commercial as a prerequisite (contradicts open source).
- One P/Invoke call per property (too many transitions, and the QtNative API grows with every property).
- JSON or QML text as a diff (expensive to parse every frame, no type safety).
- A separate runtime package (version coupling is error-prone).
- `qt.conf` / `QT_PLUGIN_PATH` for path resolution (relative to the exe, or affects the whole process).
- `AddDllDirectory` for the Qt DLLs: it requires `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)`, which takes effect process-wide; after that `LoadLibrary` no longer searches `PATH` for any vvvv package.
- `buildTransitive/` for exporting source packages (it is not read).
- Embedding the `QQuickWindow` into a WinForms form for export (`SetParent` problems with focus, input and DPI; the form plus render control makes it unnecessary).
