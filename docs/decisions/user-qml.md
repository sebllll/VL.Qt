# User QML inside VL.Qt

Brainstorm started 2026-11-11, sub-topic of [qt-bridges.md](qt-bridges.md). Background: [topics/user-qml.md](../topics/user-qml.md). Implementation: issue sebllll/VL.Qt#104 (milestone 2.0); QML node factory: #107. User page: [../user-qml.md](../user-qml.md).

## Decided

Implemented as `QmlElement` (#104) unless noted.

- **Worth doing**, as a small escape hatch, not a second UI model: reuse existing QML components and things VL.Qt has no element for. Native cost is low: `effectchain.cpp` already compiles `QQmlComponent`s on the per-window `QQmlEngine`. (2026-11-11) *Done.*
- **Minimal form**: one element `QmlElement` (`File`, `Properties` as `Dictionary<string, object>`; out `Properties`, `Signals`, `Error`), placed in the element tree like any other element. Only the properties and signals declared in the root of the user file come back, no echo of the patch's own sets. Hot reload on file change. Details and protocol: #104. (2026-11-11) *Done; deviations below.*
- **Time**: QML animations follow vvvv's clock already (QtNative installs `VlqtAnimationDriver`, `src/VL.Qt.Native/vlqt.cpp:115`), so they are deterministic. QML `Timer` runs on wall time; documented, not fixed. (2026-11-11, corrects the first proposal "animations run free") *Done (documented).*
- **Licensing**: no import filter; QtNative deploys only LGPL modules, other imports fail to load and show at the node. Documented. (2026-11-11) *Done (documented).*
- **Qt Bridge reuse**: no; a hand-written path in QtNative (one op, two properties, two events) is smaller than the Bridge's build-time wrappers. (2026-11-11) *Done: `qmlitem.{h,cpp}`, `SET_QML_PROPERTIES`, `QML_SOURCE`/`QML_RELOAD`, `EVENT_QML_PROPERTY`/`EVENT_QML_SIGNAL`.*

## Where the implementation differs from the first design

The issue text of #104 was the design; these points came out differently when it was built, and are the accepted behavior (documented in [user-qml.md](../user-qml.md)):

- **No echo is narrower than "no echo at all".** Only the value the patch has just set is suppressed. If QML changes the property afterwards (a `Behavior`, an animation, a binding) or properties that depend on it change, those changes come back to the `Properties` output.
- **Auto Size takes `width`/`height` of the root**, which is `implicitWidth`/`implicitHeight` unless the file declares an explicit size on its root; then the explicit size wins.
- **A property error stays** on `Error` until the name is set again or the file is reloaded, also after the key was removed from the dictionary.
- **Hot reload watches only the folder of the file** (the file, `*.qml`, `qmldir`): no subfolders, no `.js` files, no images. A folder that does not exist when `File` is set is watched only after the next set.
- **Strings in `SET_QML_PROPERTIES`** are u32 length + UTF-8, padded to 4 (the issue only said "like SET_UNIFORMS entries"; the string encoding is the one of SET); capped at 20000 characters in C#.
- **Extra outputs** `Measured Size` and `Scene Bounds` (optional pins), like on the other elements.
- **Unsupported property types** going back to the patch (list, object) give one `Error` line and are not reported; an unsupported signal argument arrives as its string form (as designed).

## Decided: QML node factory (#107, 2026-11-12)

Nodes with named pins from the root declarations of a `.qml` file. Contract in `src/VL.Qt/Qml/` (namespace `VL.Qt.Qml`); the exact pin lists are in `QmlNodeLayout`.

- **Pins from a C# parser** of the root object's declarations (`QmlDeclarations.Parse`), no native probe, no native change. Reason: works at node-browser time without a running Qt engine, runs no user code, is unit-testable, and is the model of the shader annotations; QmlWatcher in `qmlitem.cpp` reports the same root declarations at runtime. Supported: `property`, `readonly property`, `required property`, `default property` of type `real`/`double`/`int`/`bool`/`string`/`color`/`vector2d`/`vector3d`/`vector4d`/`rect`; `signal name(type a, …)`. `property alias`, `var`, `list<…>`, object types (and other value types such as `url`, `font`): no pin, one info message on the node. A root type that is a sibling `.qml` of the same folder (`Gauge { }` + `Gauge.qml`) contributes its declarations first (recursive, cycle guard). Reason: covers what the wire (`QmlValue`) carries; a component built on another one in the same folder is the common case.
- **Defaults**: a literal initializer (number, `true`/`false`, string, `"#rrggbb"`/`"#aarrggbb"`/SVG colour name, `Qt.rgba`/`Qt.vector2d/3d/4d`/`Qt.rect` with literal arguments) is the pin default; a binding or no initializer gives the type's default. An input is sent once it differs from the declared default, from then on on every change; a `required property` always. Reason: a binding in the file stays alive until the patch really sets the value.
- **Inputs and outputs**: an input per non-readonly property; an output per property ("last value QML changed", optional pin, visible for `readonly` and binding-initialised properties); every signal visible: no arguments → `bool` (true in the frame it fired), one → `Spread<T>`, two or more → `Spread<QmlSignal>`. Common pins exactly as `QmlElement`. Reason: readonly and bound properties are what a patch reads; the others are mostly set.
- **`Properties` input stays only as plan B** for what gets no pin (alias, var, list, object types): an optional (hidden) `Dictionary<string, object>` pin; a typed pin wins on a name clash. Reason: one escape hatch instead of losing those properties, without a second way to set what has a pin.
- **Input and output of one property share the pin name** ("Accent Color" in and out). Reason: vvvv stores a pin with its kind (`<Pin Name="Properties" Kind="InputPin" />` and `Kind="OutputPin"` in `help/Qt/HowTo Use a QML file.vl`, which `QmlElement` has shipped with since #104), so the names do not clash. A property or signal whose name is taken by a common pin or an earlier declaration gets a number ("Size 2") and a warning, like the shader pins.
- **Pin names** like the shader factory (`ShaderPinModel.PinName`: `accentColor` → "Accent Color"); pin summary = the `//` comment lines directly above the declaration. Reason: one naming rule for generated nodes.
- **Discovery in `qml/` folders** next to documents and in packages, recursive, same mechanism as `ShaderNodeFactory` (`qtshaders/`). Reason: Qt projects conventionally keep their QML in `qml/`, and unlike `shaders/` (Stride) no other vvvv library claims that name; Qt's own deployed QML modules live under `runtimes/<rid>/native/qml`, not at a package or document root, so the factory never sees them. Only files whose name starts with an uppercase letter (QML's component rule). Node name from the file name with the `_` variant of the shader nodes; category default `Qt.Elements.Qml`; head tags `// @category`, `// @summary`, `// @tags` (comma separated, passed as the `tags` argument of `NodeBuilding.NewNodeDescription`; #8 added no tag mechanism to reuse). Smell experimental like `QmlElement`. *(Smell: not possible, see "Where the implementation differs (#107)".)*
- **Hot reload**: the node description is invalidated only when the parsed declaration signature (`QmlDeclarations.Signature`: everything that reaches the description, without line numbers) changes; any other edit reloads the running instance (the watcher and reload counter of `QmlElementNode`). vvvv keeps links on pins with the same name. Reason: editing the body of a component must not rebuild the patch.
- **Errors**: a file that does not parse still gets a node (common pins and `Error`, the parser messages on the description); compile and runtime errors at `Error` as in `QmlElement`. Reason: a typo must not drop the node and its links.
- **Known limitation, `required property`**: QtNative creates the root with `QQmlComponent::create()` and sets properties afterwards (`src/VL.Qt.Native/qmlitem.cpp:482`), so a `required property` without initializer fails at creation in Qt 6 even though its pin "always sends". Fixing it needs initial properties at creation (native, out of scope of #107).

### Where the implementation differs (#107)

User documentation of the generated nodes: [../user-qml.md](../user-qml.md#nodes-from-qml-files).

- **No `Experimental` smell on the generated nodes.** `NodeBuilding.NewNodeDescription` and `IVLNodeDescription` (VL.Core 2026.8) have no smell; `SymbolSmell` only exists as the `[Smell]` attribute on symbols. The nodes are experimental anyway, said in the user docs and here (the smell rule of [../versioning.md](../versioning.md) cannot be met for factory nodes).

### Task cut (#107)

T1 (this contract) owns nothing afterwards. T2–T4 run in parallel; each touches only its files, and asks the orchestrator before it changes a file of another task or of T1's contract records.

| Task | Content | Files it may touch |
|---|---|---|
| T2 parser | `QmlDeclarations.Parse`, `ParseHead`, `Signature`; root members at depth 1 only, literals, summaries, head tags, sibling root types with cycle guard, messages | `src/VL.Qt/Qml/QmlDeclarations.cs`, `src/VL.Qt/Qml/Parsing/`, new `src/VL.Qt.Tests/QmlDeclarationsTests.cs` |
| T3 factory | `QmlNodeFactory` (ForPath, Scan, GetNodeName, CreateDescriptions, WatchSignature), `QmlNodeDescription` (New, OpenEditor, smell), registration in `Initialization.Configure`, `QmlNodeLayout` fixes if needed | `src/VL.Qt/Qml/QmlNodeFactory.cs`, `src/VL.Qt/Qml/QmlNodeDescription.cs`, `src/VL.Qt/Qml/QmlNodeLayout.cs`, `src/VL.Qt/Initialization.cs`, new `src/VL.Qt.Tests/QmlNodeFactoryTests.cs` (tests build `QmlDeclarations` by hand until T2 lands) |
| T4 instance | `QmlNodeInstance`: typed pins → dictionary (send rule, required, Properties plan B), `QmlElementNode` underneath, property and signal outputs | `src/VL.Qt/Qml/QmlNodeInstance.cs`, new `src/VL.Qt.Tests/QmlNodeInstanceTests.cs` (layouts via `QmlNodeLayout.Build` with hand-made declarations) |
| T5 docs | user docs of the QML nodes, this decision file updated after the merge | `docs/user-qml.md`, `docs/decisions/user-qml.md`, `docs/README.md` (index line) |
| T6 help patch | `help/Qt/HowTo Create QML Nodes from Files.vl`, its line in `help/Help.xml`, example `.qml` files under `help/Qt/qml/`; written by an agent after T2–T4 are merged, the user corrects its layout in vvvv | `help/Qt/HowTo Create QML Nodes from Files.vl`, `help/Help.xml`, `help/Qt/qml/*.qml`, `help/.padlint.json` |

Shared and read-only for T2–T4: `src/VL.Qt/Qml/QmlTypes.cs` (QML type → `QmlValueKind` → CLR type → wire), the records of the declarations, one file each in `src/VL.Qt/Qml/` (T2 owns the parser in `Qml/Parsing/`, but not the record shapes), `ShaderPinDescription`/`ValuePin`/`ShaderPinModel.PinName` (reused from `src/VL.Qt/Shaders/`, not moved), `QmlElementNode`.

## Open

- **C# objects/models as context properties, calling QML functions**: out of scope of #104; revisit when the dictionary form is in use.

## Rejected

- *(none yet)*
