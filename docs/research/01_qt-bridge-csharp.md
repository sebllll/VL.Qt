# Qt Bridge for C# (research, 2026-11-11)

## Sources

- Product page: https://www.qt.io/development/qt-framework/qt-bridges/csharp-gui
- Docs (snapshot): https://doc-snapshots.qt.io/qtbridge-csharp/introduction.html, `csharp-and-qml.html`, `existing-csharp-projects.html`, `release-status.html`, `api/Qt.Quick.Qml.html`
- Blog: https://www.qt.io/blog/csharp-ui-framework-via-bridging-technology
- Forum: https://forum.qt.io/category/78/qt-bridges
- Jira QTBRIDGES: not read (needs login)
- Video https://www.youtube.com/watch?v=gIWvdEeC9WQ: not watched

## Findings

| Aspect | Qt Bridge for C# | VL.Qt |
| --- | --- | --- |
| Status | Beta / Technology Preview, NuGet `QtGroup.Qt.Bridge.CSharp.<rid>` 0.5.*, APIs may change | 0.9.x, heading to 1.0 |
| License | LGPL-3.0-only or Qt Commercial | LGPL use of Qt, open source |
| Platforms | Windows x64/arm64 most mature; Linux, macOS newer; no mobile/web | win-x64, win-arm64, Linux |
| Who writes the UI | QML files (declarative, written by hand or in Qt Design Studio) | the vvvv patch, as an element tree each frame |
| Role of C# | backend / view model (MVVM): public types exposed to QML, `INotifyPropertyChanged` → bindings, events → QML signal handlers, `ListModel<T>`/`TableModel<T>`/`ObservableCollection<T>` → models | builds the scene; reconciler diffs it, command buffer to QtNative |
| Glue | generated at **build time**: "bridge information and native support"; needs C++ toolchain + CMake + Ninja on the user's machine | hand-written QtNative (C ABI, P/Invoke), protocol IDs, one `apply(buf)` per frame |
| Main loop | Qt owns it: `Qml.LoadFromRootModule("Main"); Qml.WaitForExit();`, `Qml.ProcessEvents()` "must be called from the main thread" | vvvv owns it; Qt `basic` render loop pumped once per frame, no `exec()` |
| Time | Qt's own timers and animations | vvvv `IFrameClock` only, deterministic, scrubbable |
| Custom rendering | not documented (no QQuickItem / scene graph hooks found); extending with C++ "not a priority" (forum) | own elements, effects, shaders, video, images in QtNative |
| Embedding | "HWND embedding" mentioned for WPF migration | own windows; no texture output to Stride/Skia |
| Tooling | VS / VS Code extension, QML language server, preview, debugger | vvvv editor, help patches |

Other bridges: Rust in Technology Preview too; Java/Kotlin beta; Python and Swift paused.

## Redundancy check (2026-11-12)

Every feature of the Bridge (API namespaces `Qt`, `Qt.Bridge`, `Qt.Bridge.Models`, `Qt.Resources`, `Qt.Quick`) against VL.Qt:

| Bridge feature | VL.Qt counterpart | Redundant? |
| --- | --- | --- |
| Generated QObject wrappers for C# types, `[Qt.Include]`/`[Qt.Ignore]`, `INotifyPropertyChanged` → QML bindings, C# events → signals | none; the patch sets properties through the command buffer | no |
| Models: `ListModel<T>`, `TableModel<T>`, `TreeModel<T>`, `ObservableCollection<T>` | none; spreads become elements in the tree | no |
| `Qml.LoadFromModule`, `WaitForExit`, `ProcessEvents` (load QML, run the event loop) | `vlqt_app_process_events` pumps once per vvvv frame; QML is loaded only internally (control style, effects) | same task, opposite owner of the loop; the Bridge's version cannot run inside vvvv |
| `Application` (name, organization, version, `SetWindowIcon`) | `WindowSettings` node (icon, title per window) | small overlap, a few lines of Qt calls each; nothing to share |
| `Globals.Version` (Qt version) | `qVersion()` in the perf overlay | trivial |
| `Qt.Resources` (`qrc:/` packaging of assets via `<QtResource>`) | users give file paths; `qrc:` only for the embedded style | no |
| Bundled Qt in the per-RID NuGet package (Windows) | own deployment: Qt 6.12 pinned, `windeployqt`, LGPL module list checked in CI | same function, but not replaceable: Qt version and module set of the Bridge package are not documented, QtNative must link against exactly the Qt it ships, and we need Multimedia/FFmpeg and D3D11 features |
| VS / VS Code extension, QML language server, QML preview | none (vvvv editor) | no |
| HWND embedding (blog: WPF migration) | none | no |

Not reusable as a building block: there is one `QGuiApplication` per process. The Bridge's native layer creates and runs it (Qt owns the loop), QtNative does the same under vvvv's loop. Both in one vvvv process would mean two owners of one Qt; with different Qt builds even two sets of Qt DLLs with the same names.

## Consequences

- No overlap in the core: the Bridge is "C# backend for a QML app", VL.Qt is "Qt Quick as the render target of a vvvv patch". The reconciler, the clock-driven pump and the command buffer have no counterpart there.
- Build-time code generation with a C++ toolchain does not fit vvvv: patches compile live, users have no CMake/MSVC.
- Overlap at the edges: Qt deployment as per-RID NuGet packages; P/Invoke into a native Qt layer; the main-thread rule for pumping (`ProcessEvents`) is the same as ours.
- Worth watching: their generated QObject wrappers for C# types (could expose vvvv data to user QML), their model classes, HWND embedding.
