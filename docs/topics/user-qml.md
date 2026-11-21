# Topic: user QML inside VL.Qt

Handover from the Qt Bridges brainstorm (2026-11-11, [decisions/qt-bridges.md](../decisions/qt-bridges.md), research [01_qt-bridge-csharp.md](../research/01_qt-bridge-csharp.md)).

## Context

Qt released Qt Bridge for C# (beta): QML writes the UI, C# types are exposed to QML as backend objects (MVVM, `INotifyPropertyChanged`, models). VL.Qt does the opposite: the vvvv patch builds an element tree each frame, the reconciler diffs it and QtNative applies it to Qt Quick items. QML is used today only internally (control style `src/VL.Qt.Native/style/VLQt/*.qml`, one `QQmlEngine` per window).

The question raised by the Bridge: should VL.Qt let users bring their own QML, the way the Bridge does, but inside the patch?

## Idea

An element node like `QmlElement (File)` that loads a `.qml` file as a child of the element tree, with:
- input properties set from the patch (a dictionary or dynamic pins from the QML root's properties),
- QML signals and property changes back as outputs or channels,
- optionally a vvvv object exposed as a context property (the Bridge's model idea).

## Open questions

1. Is there demand? Who would write QML next to a patch (designers with Qt Design Studio, users of existing QML components)?
2. Time: QML brings its own animations and timers; how do they follow vvvv's `IFrameClock` (determinism, scrubbing)? Disable, or accept as "free running"?
3. Pins: fixed (dictionary in, dictionary out) or dynamic pins read from the QML file (a node factory, like `ShaderNodeFactory` for `qtshaders/`)?
4. Hot reload of the `.qml` file while vvvv runs.
5. Protocol: new command types in `protocol.h` for "load component", "set dynamic property", "QML signal event".
6. Licensing: only LGPL QML modules may be imported; how to stop a user file from importing e.g. QtQuick3D or Charts (GPL)?
7. Does anything of the Bridge help here (its generated QObject wrappers), or is a hand-written path in QtNative simpler?

## Relevant files

- `docs/architecture.md`, `docs/decisions/qtnative-deployment.md`
- `src/VL.Qt.Native/elements/type_qml.cpp`, `protocol.h`, `src/VL.Qt/Wire/Protocol.cs`
- `src/VL.Qt/ShaderNodeFactory*` (dynamic node set from files at runtime)
