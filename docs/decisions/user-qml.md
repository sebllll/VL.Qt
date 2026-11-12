# User QML inside VL.Qt

Brainstorm started 2026-11-11, sub-topic of [qt-bridges.md](qt-bridges.md). Background: [topics/user-qml.md](../topics/user-qml.md). Implementation: issue sebllll/VL.Qt#104 (milestone 2.0). User page: [../user-qml.md](../user-qml.md).

## Decided

Implemented as `QmlElement` (#104) unless noted.

- **Worth doing**, as a small escape hatch, not a second UI model: reuse existing QML components and things VL.Qt has no element for. Native cost is low: `effectchain.cpp` already compiles `QQmlComponent`s on the per-window `QQmlEngine`. (2026-11-11) *Done.*
- **Minimal form**: one element `QmlElement` (`File`, `Properties` as `Dictionary<string, object>`; out `Properties`, `Signals`, `Error`), placed in the element tree like any other element. Only the properties and signals declared in the root of the user file come back, no echo of the patch's own sets. Hot reload on file change. Details and protocol: #104. (2026-11-11) *Done; deviations below.*
- **Time**: QML animations follow vvvv's clock already (QtNative installs `VlqtAnimationDriver`, `src/VL.Qt.Native/vlqt.cpp:115`), so they are deterministic. QML `Timer` runs on wall time; documented, not fixed. (2026-11-11, corrects the first proposal "animations run free") *Done (documented).*
- **Licensing**: no import filter; QtNative deploys only LGPL modules, other imports fail to load and show at the node. Documented. (2026-11-11) *Done (documented).*
- **Qt Bridge reuse**: no; a hand-written path in QtNative (one op, two properties, two events) is smaller than the Bridge's build-time wrappers. (2026-11-11) *Done: `qmlitem.{h,cpp}`, `SET_QML_PROPERTIES`, `QML_SOURCE`/`QML_RELOAD`, `EV_QML_PROPERTY`/`EV_QML_SIGNAL`.*

## Where the implementation differs from the first design

The issue text of #104 was the design; these points came out differently when it was built, and are the accepted behavior (documented in [user-qml.md](../user-qml.md)):

- **No echo is narrower than "no echo at all".** Only the value the patch has just set is suppressed. If QML changes the property afterwards (a `Behavior`, an animation, a binding) or properties that depend on it change, those changes come back to the `Properties` output.
- **Auto Size takes `width`/`height` of the root**, which is `implicitWidth`/`implicitHeight` unless the file declares an explicit size on its root; then the explicit size wins.
- **A property error stays** on `Error` until the name is set again or the file is reloaded, also after the key was removed from the dictionary.
- **Hot reload watches only the folder of the file** (the file, `*.qml`, `qmldir`): no subfolders, no `.js` files, no images. A folder that does not exist when `File` is set is watched only after the next set.
- **Strings in `SET_QML_PROPERTIES`** are u32 length + UTF-8, padded to 4 (the issue only said "like SET_UNIFORMS entries"; the string encoding is the one of SET); capped at 20000 characters in C#.
- **Extra outputs** `Measured Size` and `Scene Bounds` (optional pins), like on the other elements.
- **Unsupported property types** going back to the patch (list, object) give one `Error` line and are not reported; an unsupported signal argument arrives as its string form (as designed).

## Open

- **Dynamic pins** from the QML file (node factory like `ShaderNodeFactory`): out of scope of #104, tracked in #107 (open questions there: where the pins come from, hot reload with changed pins, input vs. output pins, search paths).
- **C# objects/models as context properties, calling QML functions**: out of scope of #104; revisit when the dictionary form is in use.

## Rejected

- *(none yet)*
