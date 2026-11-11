# User QML inside VL.Qt

Brainstorm started 2026-11-11, sub-topic of [qt-bridges.md](qt-bridges.md). Background: [topics/user-qml.md](../topics/user-qml.md). Implementation: issue sebllll/VL.Qt#104 (milestone 2.0).

## Decided

- **Worth doing**, as a small escape hatch, not a second UI model: reuse existing QML components and things VL.Qt has no element for. Native cost is low: `effectchain.cpp` already compiles `QQmlComponent`s on the per-window `QQmlEngine`. (2026-11-11)
- **Minimal form**: one element `QmlElement` (`File`, `Properties` as `Dictionary<string, object>`; out `Properties`, `Signals`, `Error`), placed in the element tree like any other element. Only the properties and signals declared in the root of the user file come back, no echo of the patch's own sets. Hot reload on file change. Details and protocol: #104. (2026-11-11)
- **Time**: QML animations follow vvvv's clock already (QtNative installs `VlqtAnimationDriver`, `src/VL.Qt.Native/vlqt.cpp:115`), so they are deterministic. QML `Timer` runs on wall time; documented, not fixed. (2026-11-11, corrects the first proposal "animations run free")
- **Licensing**: no import filter; QtNative deploys only LGPL modules, other imports fail to load and show at the node. Documented. (2026-11-11)
- **Qt Bridge reuse**: no; a hand-written path in QtNative (one op, two properties, two events) is smaller than the Bridge's build-time wrappers. (2026-11-11)

## Open

- **Dynamic pins** from the QML file (node factory like `ShaderNodeFactory`), C# objects/models as context properties, calling QML functions: out of scope of #104; revisit when the dictionary form is in use.

## Rejected

- *(none yet)*
