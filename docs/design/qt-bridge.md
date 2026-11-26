# Qt Bridge

Why VL.Qt does not use Qt Bridge for C# (the official C# binding of Qt Quick by The Qt Company), and how the two differ. The user-facing comparison is in the [README](../../README.md#compared-to-qt-bridge-for-c); the code this is about is QtNative (`src/VL.Qt.Native/`) and the pump (`src/VL.Qt/Runtime/QtPump.cs`).

## Context

Qt Bridge for C# lets C# code serve a QML application: QML files describe the UI, public C# types are exposed to QML as backend objects (MVVM, `INotifyPropertyChanged`, list and table models), and the Bridge's native layer runs Qt's event loop. Its status is a beta (see Evidence). VL.Qt does the opposite: the vvvv patch builds an element tree each frame, the reconciler diffs it, and QtNative applies it to Qt Quick items.

## Design

VL.Qt does not use the Bridge, for three reasons:

- **Who writes the UI.** The Bridge is MVVM: QML writes the UI, C# is the backend. VL.Qt needs the patch to write the UI.
- **Build-time code generation.** The Bridge generates its glue at build time and needs a C++ toolchain (CMake, Ninja) on the user's machine. vvvv patches compile live and users have no toolchain.
- **Loop and clock.** Qt owns the main loop and the time in the Bridge. vvvv owns both in VL.Qt: Qt's `basic` render loop is pumped once per vvvv frame and `IFrameClock` is the only clock ([timing.md](timing.md)).

User QML inside VL.Qt takes its own small path in QtNative, not the Bridge ([user-qml.md](user-qml.md)). The pump follows the same rule as the Bridge's `Qml.ProcessEvents()` (main thread only), and both use Qt under the LGPL by dynamic linking.

### Comparison

| Aspect | Qt Bridge for C# | VL.Qt |
| --- | --- | --- |
| Status | Beta / Technology Preview, NuGet `QtGroup.Qt.Bridge.CSharp.<rid>` 0.5.*, APIs may change | 0.9.x, heading to 1.0 |
| License | LGPL-3.0-only or Qt Commercial | LGPL use of Qt, open source |
| Platforms | Windows x64/arm64 most mature; Linux, macOS newer; no mobile/web | win-x64, win-arm64, Linux |
| Who writes the UI | QML files (declarative, written by hand or in Qt Design Studio) | the vvvv patch, as an element tree each frame |
| Role of C# | backend / view model (MVVM): public types exposed to QML, `INotifyPropertyChanged` → bindings, events → QML signal handlers, `ListModel<T>`/`TableModel<T>`/`ObservableCollection<T>` → models | builds the scene; reconciler diffs it, command buffer to QtNative |
| Glue | generated at **build time**: "bridge information and native support"; needs C++ toolchain + CMake + Ninja on the user's machine | hand-written QtNative (C ABI, P/Invoke), protocol IDs, one apply per frame |
| Main loop | Qt owns it: `Qml.LoadFromRootModule("Main"); Qml.WaitForExit();`, `Qml.ProcessEvents()` "must be called from the main thread" | vvvv owns it; Qt `basic` render loop pumped once per frame, no `exec()` |
| Time | Qt's own timers and animations | vvvv `IFrameClock` only, deterministic, scrubbable |
| Custom rendering | not documented (no QQuickItem / scene graph hooks found); extending with C++ "not a priority" (forum) | own elements, effects, shaders, video, images in QtNative |
| Embedding | "HWND embedding" mentioned for WPF migration | own windows; texture output to Stride |
| Tooling | VS / VS Code extension, QML language server, preview, debugger | vvvv editor, help patches |

Other bridges of The Qt Company: Rust in Technology Preview too; Java/Kotlin beta; Python and Swift paused.

### Redundancy check

Every feature of the Bridge (API namespaces `Qt`, `Qt.Bridge`, `Qt.Bridge.Models`, `Qt.Resources`, `Qt.Quick`) against VL.Qt. The overlaps are small or not replaceable; nothing in VL.Qt needs to be removed or replaced.

| Bridge feature | VL.Qt counterpart | Redundant? |
| --- | --- | --- |
| Generated QObject wrappers for C# types, `[Qt.Include]`/`[Qt.Ignore]`, `INotifyPropertyChanged` → QML bindings, C# events → signals | none; the patch sets properties through the command buffer | no |
| Models: `ListModel<T>`, `TableModel<T>`, `TreeModel<T>`, `ObservableCollection<T>` | none; spreads become elements in the tree | no |
| `Qml.LoadFromModule`, `WaitForExit`, `ProcessEvents` (load QML, run the event loop) | `vlqt_app_process_events_for` pumps once per vvvv frame; QML is loaded only internally (control style, effects) and for user QML | same task, opposite owner of the loop; the Bridge's version cannot run inside vvvv |
| `Application` (name, organization, version, `SetWindowIcon`) | `WindowSettings` node (icon, title per window) | small overlap, a few lines of Qt calls each; nothing to share |
| `Globals.Version` (Qt version) | `qVersion()` in the perf overlay | trivial |
| `Qt.Resources` (`qrc:/` packaging of assets via `<QtResource>`) | users give file paths; `qrc:` only for the embedded style | no |
| Bundled Qt in the per-RID NuGet package (Windows) | own deployment: Qt pinned, `windeployqt`, LGPL module list checked in CI | same function, but not replaceable: the Qt version and module set of the Bridge package are not documented, QtNative must link against exactly the Qt it ships, and it needs Multimedia/FFmpeg and D3D11 features |
| VS / VS Code extension, QML language server, QML preview | none (vvvv editor) | no |
| HWND embedding (blog: WPF migration) | none | no |

## Consequences

- No overlap in the core: the Bridge is "C# backend for a QML app", VL.Qt is "Qt Quick as the render target of a vvvv patch". The reconciler, the clock-driven pump and the command buffer have no counterpart in the Bridge.
- Overlap at the edges: Qt deployment as per-RID packages, P/Invoke into a native Qt layer, and the main-thread rule for pumping.

## Alternatives considered

- **The Bridge as a building block** (its NuGet Qt, its QObject wrappers, its event loop): there is one `QGuiApplication` per process. The Bridge's native layer creates and runs it with Qt owning the loop, and QtNative does the same under vvvv's loop. Both in one vvvv process would mean two owners of one Qt, and with different Qt builds two sets of Qt DLLs with the same names; the Bridge cannot run inside vvvv's frame loop.

## Open questions

Watch the Bridge and check again if it documents its bundled Qt (version, modules) or offers custom Qt Quick items from C#. Its generated QObject wrappers for C# types (could expose vvvv data to user QML), its model classes and HWND embedding are worth watching. Not checked: the release video and the Jira project QTBRIDGES (login needed).

## Evidence

Research of 2026-11-11 and 2026-11-12 (status "as of" those dates: beta, NuGet 0.5.*); the tables above are from it.

- Product page: https://www.qt.io/development/qt-framework/qt-bridges/csharp-gui
- Docs (snapshot): https://doc-snapshots.qt.io/qtbridge-csharp/introduction.html, `csharp-and-qml.html`, `existing-csharp-projects.html`, `release-status.html`, `api/Qt.Quick.Qml.html`
- Blog: https://www.qt.io/blog/csharp-ui-framework-via-bridging-technology
- Forum: https://forum.qt.io/category/78/qt-bridges
- Jira QTBRIDGES: not read (needs login). Video https://www.youtube.com/watch?v=gIWvdEeC9WQ: not watched.
