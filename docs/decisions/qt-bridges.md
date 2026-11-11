# Qt Bridges (Qt Bridge for C#) and VL.Qt

Brainstorm started 2026-11-11: does the official Qt Bridge for C# change the direction of VL.Qt? Research: [01_qt-bridge-csharp.md](../research/01_qt-bridge-csharp.md).

## Decided

- **No switch to the Bridge.** The Bridge is MVVM (QML writes the UI, C# is the backend), generates glue at build time with a C++ toolchain, and lets Qt own the main loop and the time. VL.Qt needs the opposite: the patch writes the UI, vvvv owns loop and clock, no toolchain on the user's machine. (2026-11-12)
- **No redundant features.** Every Bridge feature was checked against VL.Qt ([redundancy check](../research/01_qt-bridge-csharp.md#redundancy-check-2026-11-12)). The only overlaps are small (window icon/app metadata, Qt version string) or not replaceable (Qt deployment: version and modules of the Bridge's bundled Qt are undocumented, QtNative must link against the Qt it ships). Nothing to remove or replace. (2026-11-12)
- **No wrong assumptions found.** Our main-thread pump matches the Bridge's `Qml.ProcessEvents()` rule; LGPL dynamic linking is the same model. (2026-11-12)
- **User QML inside VL.Qt:** own path in QtNative, not via the Bridge; issue #104 (milestone 2.0). Details: [user-qml.md](user-qml.md), handover [topics/user-qml.md](../topics/user-qml.md). (2026-11-11)

## Open

- Watch the Bridge: if it later documents its bundled Qt (version, modules) or offers custom Qt Quick items from C#, check again. Not checked: the release video, Jira QTBRIDGES (login needed). (2026-11-12)

## Rejected

- **Using the Bridge as a building block** (its NuGet Qt, its QObject wrappers, its event loop): one `QGuiApplication` per process, owned by the Bridge's loop; it cannot run inside vvvv's frame loop. (2026-11-12)
